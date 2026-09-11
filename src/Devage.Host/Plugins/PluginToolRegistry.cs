using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Devage.Tools.Abstractions;
using Devage.Tools.Email;
using Devage.Tools.Teams;
using Devage.Tools.Web;

namespace Devage.Host.Plugins;

public sealed class PluginToolRegistry : IToolRegistry, IDisposable
{
    private readonly object _gate = new();
    private readonly string _pluginsDirectory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PluginToolRegistry> _logger;
    private readonly Dictionary<string, LoadedPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDevageTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _builtInNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reloadDebounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _watcher;

    public PluginToolRegistry(
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<PluginToolRegistry> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var root = environment.ContentRootPath;
        var repoPlugins = Path.GetFullPath(Path.Combine(root, "..", "..", "plugins"));
        var localPlugins = Path.GetFullPath(Path.Combine(root, "plugins"));
        _pluginsDirectory = Directory.Exists(repoPlugins) ? repoPlugins : localPlugins;
        Directory.CreateDirectory(_pluginsDirectory);

        RegisterBuiltIn(new WebTool(_httpClientFactory));
        RegisterBuiltIn(new EmailTool());
        RegisterBuiltIn(new TeamsTool());
        LoadAllPlugins();

        _watcher = new FileSystemWatcher(_pluginsDirectory)
        {
            Filter = "*.dll",
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Created += OnPluginChanged;
        _watcher.Changed += OnPluginChanged;
        _watcher.Deleted += OnPluginDeleted;
        _watcher.Renamed += OnPluginRenamed;
    }

    public event EventHandler? ToolsChanged;

    public IReadOnlyList<IDevageTool> GetAvailableTools()
    {
        lock (_gate)
        {
            return _tools.Values.OrderBy(t => t.Name).ToList();
        }
    }

    public IDevageTool? GetTool(string name)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out var tool) ? tool : null;
        }
    }

    private void RegisterBuiltIn(IDevageTool tool)
    {
        lock (_gate)
        {
            _tools[tool.Name] = tool;
            _builtInNames.Add(tool.Name);
        }
    }

    private void LoadAllPlugins()
    {
        foreach (var dll in Directory.EnumerateFiles(_pluginsDirectory, "*.dll"))
        {
            TryLoadPlugin(dll);
        }
    }

    private void OnPluginChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce write storms / copy-replace so a single settle wins.
        var cts = _reloadDebounce.AddOrUpdate(
            e.FullPath,
            _ => new CancellationTokenSource(),
            (_, existing) =>
            {
                existing.Cancel();
                existing.Dispose();
                return new CancellationTokenSource();
            });

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                // Brief retry: file may still be locked by the writer.
                for (var attempt = 0; attempt < 5 && !token.IsCancellationRequested; attempt++)
                {
                    if (!File.Exists(e.FullPath))
                    {
                        return;
                    }

                    if (!TryOpenForRead(e.FullPath))
                    {
                        await Task.Delay(150, token);
                        continue;
                    }

                    TryLoadPlugin(e.FullPath);
                    ToolsChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }

                _logger.LogWarning("Gave up reloading plugin {Path} after lock retries.", e.FullPath);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer change event
            }
            finally
            {
                if (_reloadDebounce.TryGetValue(e.FullPath, out var current) && ReferenceEquals(current, cts))
                {
                    _reloadDebounce.TryRemove(e.FullPath, out _);
                    cts.Dispose();
                }
            }
        }, CancellationToken.None);
    }

    private static bool TryOpenForRead(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnPluginDeleted(object sender, FileSystemEventArgs e)
    {
        UnloadPlugin(e.FullPath);
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPluginRenamed(object sender, RenamedEventArgs e)
    {
        UnloadPlugin(e.OldFullPath);
        TryLoadPlugin(e.FullPath);
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TryLoadPlugin(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            UnloadPlugin(path);

            var alc = new PluginLoadContext(path);
            var assembly = alc.LoadFromAssemblyPath(path);
            var tools = CreateTools(assembly).ToList();
            if (tools.Count == 0)
            {
                alc.Unload();
                _logger.LogWarning("Plugin {Path} contains no IDevageTool implementations.", path);
                return;
            }

            lock (_gate)
            {
                foreach (var tool in tools)
                {
                    _tools[tool.Name] = tool;
                    _logger.LogInformation("Loaded tool {Tool} from plugin {Path}", tool.Name, path);
                }

                _plugins[path] = new LoadedPlugin(alc, tools.Select(t => t.Name).ToList());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin {Path}", path);
        }
    }

    private void UnloadPlugin(string path)
    {
        lock (_gate)
        {
            if (!_plugins.Remove(path, out var loaded))
            {
                return;
            }

            foreach (var name in loaded.ToolNames)
            {
                if (_builtInNames.Contains(name))
                {
                    continue;
                }

                _tools.Remove(name);
            }

            loaded.Context.Unload();
            _logger.LogInformation("Unloaded plugin {Path}", path);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private IEnumerable<IDevageTool> CreateTools(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(IDevageTool).IsAssignableFrom(type))
            {
                continue;
            }

            object? instance = null;
            var ctorWithFactory = type.GetConstructor([typeof(IHttpClientFactory)]);
            if (ctorWithFactory is not null)
            {
                instance = ctorWithFactory.Invoke([_httpClientFactory]);
            }
            else
            {
                var ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor is not null)
                {
                    instance = ctor.Invoke(null);
                }
            }

            if (instance is IDevageTool tool)
            {
                yield return tool;
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        foreach (var cts in _reloadDebounce.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _reloadDebounce.Clear();
        lock (_gate)
        {
            foreach (var plugin in _plugins.Values)
            {
                plugin.Context.Unload();
            }

            _plugins.Clear();
        }
    }

    private sealed record LoadedPlugin(PluginLoadContext Context, List<string> ToolNames);
}

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
