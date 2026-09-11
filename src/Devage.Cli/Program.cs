using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Devage.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly string HostUrl =
        Environment.GetEnvironmentVariable("DEVAGE_HOST_URL") ?? "http://127.0.0.1:5088";

    private static readonly string PidFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "devage",
        "host.pid");

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "host" => await HandleHostAsync(args.Skip(1).ToArray()),
                "-born" or "born" => await BornAsync(args.Skip(1).ToArray()),
                "-start" or "start" => await PostAgentActionAsync(RequireId(args), "start", ParseGoal(args)),
                "-stop" or "stop" => await PostAgentActionAsync(RequireId(args), "stop"),
                "-kill" or "kill" => await PostAgentActionAsync(RequireId(args), "kill"),
                "-list" or "list" => await ListAsync(),
                "-status" or "status" => await StatusAsync(RequireId(args)),
                "-logs" or "logs" => await LogsAsync(RequireId(args)),
                "-confirm" or "confirm" => await ConfirmAsync(args.Skip(1).ToArray()),
                "-assign-master" or "assign-master" => await AssignMasterAsync(args.Skip(1).ToArray()),
                "-pending-plans" or "pending-plans" => await PendingPlansAsync(args.Skip(1).ToArray()),
                "-approve-plan" or "approve-plan" => await DecidePlanAsync(args.Skip(1).ToArray(), "Approve"),
                "-reject-plan" or "reject-plan" => await DecidePlanAsync(args.Skip(1).ToArray(), "Reject"),
                "-modify-plan" or "modify-plan" => await ModifyPlanAsync(args.Skip(1).ToArray()),
                "-subordinates" or "subordinates" => await SubordinatesAsync(RequireId(args)),
                "-help" or "--help" or "help" => PrintHelpReturn(),
                _ => Unknown(args[0])
            };
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Cannot reach Devage.Host at {HostUrl}. Is it running? ({ex.Message})");
            Console.Error.WriteLine("Start it with: devage host start");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int PrintHelpReturn()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Devage CLI

            Usage:
              devage host start|stop|install-service|uninstall-service|service-status
              devage -born [--name NAME] [--workspace PATH] [--goal TEXT] [--role Agent|Master] [--master ID|NAME]
              devage -start <id|name> [--goal TEXT]
              devage -stop <id|name>
              devage -kill <id|name>
              devage -list
              devage -status <id|name>
              devage -logs <id|name>
              devage -confirm <stepId> --yes|--no
              devage -assign-master <agent> <master|none>
              devage -subordinates <master>
              devage -pending-plans [master]
              devage -approve-plan <planId> [--comment TEXT]
              devage -reject-plan <planId> [--comment TEXT]
              devage -modify-plan <planId> --steps-json PATH [--title TEXT] [--comment TEXT]

            Environment:
              DEVAGE_HOST_URL              (default http://127.0.0.1:5088)
              DEVAGE_CONNECTION_STRING     (Host)
              DEVAGE_OPENAI_API_KEY        (Host)
              DEVAGE_GRAPH_TENANT_ID       (Email/Teams)
              DEVAGE_GRAPH_CLIENT_ID
              DEVAGE_GRAPH_CLIENT_SECRET
              DEVAGE_GRAPH_MAILBOX
              DEVAGE_GRAPH_TEAM_ID / DEVAGE_GRAPH_CHANNEL_ID
            """);
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintHelp();
        return 1;
    }

    private static string RequireId(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
        {
            throw new ArgumentException("Expected <id|name> argument.");
        }

        return args[1];
    }

    private static string? ParseGoal(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--goal" or "-g")
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static async Task<int> HandleHostAsync(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Usage: devage host start|stop|install-service|uninstall-service|service-status");
        }

        return args[0].ToLowerInvariant() switch
        {
            "start" => await StartHostAsync(),
            "stop" => StopHost(),
            "install-service" => await InstallServiceAsync(),
            "uninstall-service" => UninstallService(),
            "service-status" => ServiceStatus(),
            _ => throw new ArgumentException("Usage: devage host start|stop|install-service|uninstall-service|service-status")
        };
    }

    private static string ServicePublishDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "devage",
        "service");

    private static string ServiceExePath => Path.Combine(ServicePublishDir, "Devage.Host.exe");

    private const string WindowsServiceName = "DevageHost";

    private static async Task<int> InstallServiceAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Windows Service install is only supported on Windows.");
        }

        var hostProject = FindHostProject();
        Directory.CreateDirectory(ServicePublishDir);

        Console.WriteLine($"Publishing Host to {ServicePublishDir} ...");
        var publish = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"publish \"{hostProject}\" -c Release -o \"{ServicePublishDir}\" --self-contained false -v q",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Failed to start publish.");
        await publish.WaitForExitAsync();
        if (publish.ExitCode != 0)
        {
            Console.Error.WriteLine(await publish.StandardError.ReadToEndAsync());
            Console.Error.WriteLine(await publish.StandardOutput.ReadToEndAsync());
            throw new InvalidOperationException("Host publish failed.");
        }

        if (!File.Exists(ServiceExePath))
        {
            throw new FileNotFoundException($"Published Host exe not found at {ServiceExePath}");
        }

        // Stop existing service if present
        RunSc($"stop {WindowsServiceName}", ignoreExitCode: true);
        RunSc($"delete {WindowsServiceName}", ignoreExitCode: true);

        var binPath = $"\"{ServiceExePath}\"";
        var create = RunSc($"create {WindowsServiceName} binPath= {binPath} start= auto DisplayName= \"Devage Host\"");
        if (create != 0)
        {
            throw new InvalidOperationException(
                "sc create failed. Run the CLI elevated (Administrator) to install the Windows Service.");
        }

        RunSc($"description {WindowsServiceName} \"Devage agent host daemon (resume Running agents after reboot)\"");
        var start = RunSc($"start {WindowsServiceName}");
        if (start != 0)
        {
            Console.WriteLine("Service installed but start failed. Start it with: sc start DevageHost");
            return 1;
        }

        Console.WriteLine($"Windows Service '{WindowsServiceName}' installed and started.");
        Console.WriteLine($"Binary: {ServiceExePath}");
        Console.WriteLine("Tip: set DEVAGE_CONNECTION_STRING / DEVAGE_OPENAI_API_KEY as machine or user env vars for the service account.");
        return 0;
    }

    private static int UninstallService()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Windows Service uninstall is only supported on Windows.");
        }

        RunSc($"stop {WindowsServiceName}", ignoreExitCode: true);
        var code = RunSc($"delete {WindowsServiceName}", ignoreExitCode: true);
        Console.WriteLine(code == 0
            ? $"Windows Service '{WindowsServiceName}' removed."
            : $"Service '{WindowsServiceName}' not found or could not be deleted (try elevated shell).");
        return 0;
    }

    private static int ServiceStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Windows Service status is only supported on Windows.");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query {WindowsServiceName}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run sc.exe");
        p.WaitForExit();
        Console.WriteLine(p.StandardOutput.ReadToEnd());
        Console.Write(p.StandardError.ReadToEnd());
        return p.ExitCode == 0 ? 0 : 1;
    }

    private static int RunSc(string arguments, bool ignoreExitCode = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run sc.exe");
        p.WaitForExit();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.WriteLine(stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.WriteLine(stderr.Trim());
        }

        if (!ignoreExitCode && p.ExitCode != 0)
        {
            return p.ExitCode;
        }

        return ignoreExitCode ? 0 : p.ExitCode;
    }

    private static async Task<int> StartHostAsync()
    {
        if (await IsHostHealthyAsync())
        {
            Console.WriteLine($"Host already running at {HostUrl}");
            return 0;
        }

        var hostProject = FindHostProject();
        Directory.CreateDirectory(Path.GetDirectoryName(PidFile)!);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{hostProject}\" --no-build",
            WorkingDirectory = Path.GetDirectoryName(hostProject)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Prefer building first for reliability
        var build = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{hostProject}\" -c Debug -v q",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Failed to start build.");
        await build.WaitForExitAsync();
        if (build.ExitCode != 0)
        {
            Console.Error.WriteLine(await build.StandardError.ReadToEndAsync());
            Console.Error.WriteLine(await build.StandardOutput.ReadToEndAsync());
            throw new InvalidOperationException("Host build failed.");
        }

        psi.Arguments = $"run --project \"{hostProject}\" --no-build";
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Host process.");
        await File.WriteAllTextAsync(PidFile, process.Id.ToString());

        for (var i = 0; i < 40; i++)
        {
            if (await IsHostHealthyAsync())
            {
                Console.WriteLine($"Devage.Host started (pid {process.Id}) at {HostUrl}");
                return 0;
            }

            if (process.HasExited)
            {
                var err = await process.StandardError.ReadToEndAsync();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                throw new InvalidOperationException($"Host exited early.\n{stdout}\n{err}");
            }

            await Task.Delay(500);
        }

        Console.WriteLine($"Host process started (pid {process.Id}) but /health not ready yet. Check logs.");
        return 0;
    }

    private static int StopHost()
    {
        if (File.Exists(PidFile))
        {
            var text = File.ReadAllText(PidFile).Trim();
            if (int.TryParse(text, out var pid))
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                    Console.WriteLine($"Stopped Host pid {pid}");
                }
                catch (ArgumentException)
                {
                    Console.WriteLine("Host pid file present but process not running.");
                }
            }

            File.Delete(PidFile);
            return 0;
        }

        // Fallback: try to find dotnet running Devage.Host
        foreach (var p in Process.GetProcessesByName("dotnet"))
        {
            try
            {
                var cmd = GetCommandLine(p);
                if (cmd is not null && cmd.Contains("Devage.Host", StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    Console.WriteLine($"Stopped Host pid {p.Id}");
                    return 0;
                }
            }
            catch
            {
                // ignore access issues
            }
        }

        Console.WriteLine("No Host process found.");
        return 0;
    }

    private static string? GetCommandLine(Process process)
    {
        try
        {
            // Best-effort on Windows via WMIC-less approach: MainModule path
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string FindHostProject()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Devage.Host", "Devage.Host.csproj")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "Devage.Host", "Devage.Host.csproj")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Devage.Host", "Devage.Host.csproj")),
            @"C:\devage\src\Devage.Host\Devage.Host.csproj"
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        throw new FileNotFoundException("Could not locate Devage.Host.csproj. Run from repo root or set working directory.");
    }

    private static async Task<bool> IsHostHealthyAsync()
    {
        try
        {
            using var http = CreateHttp();
            using var response = await http.GetAsync("/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateHttp()
    {
        return new HttpClient { BaseAddress = new Uri(HostUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    private static async Task<int> BornAsync(string[] args)
    {
        var name = GetOption(args, "--name") ?? Prompt("Agent name", $"agent-{DateTime.Now:yyyyMMdd-HHmmss}");
        var workspace = GetOption(args, "--workspace") ?? Prompt("Workspace path",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "devage-workspaces", name));
        var goal = GetOption(args, "--goal") ?? Prompt("Initial goal (optional)", "");
        var role = GetOption(args, "--role") ?? Prompt("Role [Agent|Master]", "Agent");
        var masterOpt = GetOption(args, "--master");
        if (masterOpt is null && !role.Equals("Master", StringComparison.OrdinalIgnoreCase))
        {
            masterOpt = Prompt("Master id/name (optional, blank = autonomous)", "");
        }

        using var http = CreateHttp();
        var tools = await http.GetFromJsonAsync<List<AvailableToolDto>>("/api/tools", JsonOptions)
            ?? [];

        Console.WriteLine();
        Console.WriteLine("Available tools:");
        for (var i = 0; i < tools.Count; i++)
        {
            Console.WriteLine($"  [{i + 1}] {tools[i].Name} — {tools[i].Description}");
        }

        Console.WriteLine();
        var selection = Prompt("Enable tools (comma numbers, or 'all')", tools.Count == 0 ? "" : "all");
        var selected = SelectTools(tools, selection);

        var bindings = new List<ToolBindingRequest>();
        foreach (var tool in selected)
        {
            Console.WriteLine();
            Console.WriteLine($"Configure tool '{tool.Name}':");
            var config = new Dictionary<string, string?>();
            foreach (var field in tool.ConfigFields)
            {
                var label = field.Required ? $"{field.Label} (required)" : field.Label;
                var value = Prompt(label, field.DefaultValue ?? "");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    config[field.Key] = value;
                }
                else if (field.Required)
                {
                    throw new ArgumentException($"Config '{field.Key}' is required for tool {tool.Name}.");
                }
            }

            bindings.Add(new ToolBindingRequest(tool.Name, JsonSerializer.Serialize(config)));
        }

        if (bindings.Count == 0)
        {
            Console.WriteLine("Warning: no tools selected. Agent will only perform local note steps.");
        }

        Guid? masterId = null;
        if (!string.IsNullOrWhiteSpace(masterOpt) &&
            !masterOpt.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            masterId = await ResolveMasterGuidAsync(http, masterOpt);
        }

        var request = new BornAgentRequest(
            name,
            workspace,
            string.IsNullOrWhiteSpace(goal) ? null : goal,
            role,
            masterId,
            bindings);

        using var response = await http.PostAsJsonAsync("/api/agents/born", request, JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(body);
            return 1;
        }

        Console.WriteLine(body);
        Console.WriteLine("Agent born. Start with: devage -start " + name);
        return 0;
    }

    private static List<AvailableToolDto> SelectTools(List<AvailableToolDto> tools, string selection)
    {
        if (tools.Count == 0)
        {
            return [];
        }

        if (selection.Equals("all", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(selection))
        {
            return tools;
        }

        var result = new List<AvailableToolDto>();
        foreach (var part in selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var idx) && idx >= 1 && idx <= tools.Count)
            {
                result.Add(tools[idx - 1]);
            }
            else
            {
                var byName = tools.FirstOrDefault(t => t.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (byName is not null)
                {
                    result.Add(byName);
                }
            }
        }

        return result.DistinctBy(t => t.Name).ToList();
    }

    private static async Task<int> PostAgentActionAsync(string idOrName, string action, string? goal = null)
    {
        using var http = CreateHttp();
        HttpResponseMessage response;
        if (action == "start")
        {
            response = await http.PostAsJsonAsync($"/api/agents/{Uri.EscapeDataString(idOrName)}/start",
                new StartAgentRequest(goal), JsonOptions);
        }
        else
        {
            response = await http.PostAsync($"/api/agents/{Uri.EscapeDataString(idOrName)}/{action}", null);
        }

        var body = await response.Content.ReadAsStringAsync();
        Console.WriteLine(string.IsNullOrWhiteSpace(body) ? response.StatusCode.ToString() : body);

        if (action == "start" && response.IsSuccessStatusCode)
        {
            await WatchConfirmationsAsync(http, idOrName);
        }

        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task WatchConfirmationsAsync(HttpClient http, string idOrName)
    {
        Console.WriteLine("Watching for Important/Critical confirmations (Ctrl+C to detach)...");
        var agent = await http.GetFromJsonAsync<AgentDto>($"/api/agents/{Uri.EscapeDataString(idOrName)}", JsonOptions);
        if (agent is null)
        {
            return;
        }

        for (var i = 0; i < 120; i++)
        {
            var pending = await http.GetFromJsonAsync<List<PendingConfirmationDto>>(
                $"/api/confirmations?agentId={agent.Id}", JsonOptions) ?? [];

            foreach (var item in pending)
            {
                Console.WriteLine();
                Console.WriteLine($"Confirmation required ({item.DecisionClass})");
                Console.WriteLine($"  Step #{item.StepOrdinal}: {item.StepTitle}");
                Console.WriteLine($"  {item.Description}");
                if (!string.IsNullOrWhiteSpace(item.Justification))
                {
                    Console.WriteLine($"  Why: {item.Justification}");
                }

                if (!string.IsNullOrWhiteSpace(item.ProposedViewpoint))
                {
                    Console.WriteLine($"  Proposal: {item.ProposedViewpoint}");
                }

                var answer = Prompt("Approve? [y/N]", "n");
                var approved = answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                               answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
                await http.PostAsJsonAsync($"/api/confirmations/{item.StepId}",
                    new ConfirmationDecisionRequest(approved, null), JsonOptions);
            }

            var status = await http.GetFromJsonAsync<AgentStatusDto>(
                $"/api/agents/{Uri.EscapeDataString(idOrName)}/status", JsonOptions);
            if (status is null || status.Status is "Stopped" or "Killed" or "Born")
            {
                Console.WriteLine($"Agent status: {status?.Status}");
                break;
            }

            await Task.Delay(1000);
        }
    }

    private static async Task<int> ListAsync()
    {
        using var http = CreateHttp();
        var agents = await http.GetFromJsonAsync<List<AgentDto>>("/api/agents", JsonOptions) ?? [];
        if (agents.Count == 0)
        {
            Console.WriteLine("No agents.");
            return 0;
        }

        foreach (var a in agents)
        {
            var master = a.MasterId is Guid mid ? mid.ToString("N")[..8] : "-";
            Console.WriteLine($"{a.Id}  {a.Name,-24} {a.Role,-8} {a.Status,-10} master={master,-10} tools={a.ToolBindings.Count}");
        }

        return 0;
    }

    private static async Task<int> AssignMasterAsync(string[] args)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("Usage: devage -assign-master <agent> <master|none>");
        }

        var agent = args[0];
        var master = args[1];
        using var http = CreateHttp();
        var body = new AssignMasterRequest(
            master.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : master);
        using var response = await http.PostAsJsonAsync(
            $"/api/agents/{Uri.EscapeDataString(agent)}/master", body, JsonOptions);
        Console.WriteLine(await response.Content.ReadAsStringAsync());
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task<int> SubordinatesAsync(string masterIdOrName)
    {
        using var http = CreateHttp();
        var agents = await http.GetFromJsonAsync<List<AgentDto>>(
            $"/api/masters/{Uri.EscapeDataString(masterIdOrName)}/agents", JsonOptions) ?? [];
        if (agents.Count == 0)
        {
            Console.WriteLine("No subordinates.");
            return 0;
        }

        foreach (var a in agents)
        {
            Console.WriteLine($"{a.Id}  {a.Name,-24} {a.Status,-10} goal={a.Goal}");
        }

        return 0;
    }

    private static async Task<int> PendingPlansAsync(string[] args)
    {
        using var http = CreateHttp();
        var path = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? $"/api/masters/{Uri.EscapeDataString(args[0])}/pending-plans"
            : "/api/plans/pending";
        var plans = await http.GetFromJsonAsync<List<PendingMasterPlanDto>>(path, JsonOptions) ?? [];
        if (plans.Count == 0)
        {
            Console.WriteLine("No pending Master plans.");
            return 0;
        }

        foreach (var p in plans)
        {
            Console.WriteLine($"{p.PlanId}  agent={p.AgentName}  master={p.MasterName}  {p.Title}");
            Console.WriteLine($"  goal: {p.Goal}");
            foreach (var s in p.Steps)
            {
                Console.WriteLine($"  #{s.Ordinal} [{s.DecisionClass}] {s.Title}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static async Task<int> DecidePlanAsync(string[] args, string decision)
    {
        if (args.Length == 0 || !Guid.TryParse(args[0], out var planId))
        {
            throw new ArgumentException($"Usage: devage -{decision.ToLowerInvariant()}-plan <planId> [--comment TEXT]");
        }

        var comment = GetOption(args, "--comment");
        using var http = CreateHttp();
        using var response = await http.PostAsJsonAsync(
            $"/api/plans/{planId}/master-decision",
            new MasterPlanDecisionRequest(decision, comment, null, null),
            JsonOptions);
        Console.WriteLine(await response.Content.ReadAsStringAsync());
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task<int> ModifyPlanAsync(string[] args)
    {
        if (args.Length == 0 || !Guid.TryParse(args[0], out var planId))
        {
            throw new ArgumentException(
                "Usage: devage -modify-plan <planId> --steps-json PATH [--title TEXT] [--comment TEXT]");
        }

        var stepsPath = GetOption(args, "--steps-json")
            ?? throw new ArgumentException("--steps-json PATH is required (JSON array of steps).");
        var json = await File.ReadAllTextAsync(stepsPath);
        var steps = JsonSerializer.Deserialize<List<MasterPlanStepDto>>(json, JsonOptions)
            ?? throw new ArgumentException("Could not parse steps JSON.");
        if (steps.Count == 0)
        {
            throw new ArgumentException("Modified steps cannot be empty.");
        }

        using var http = CreateHttp();
        using var response = await http.PostAsJsonAsync(
            $"/api/plans/{planId}/master-decision",
            new MasterPlanDecisionRequest("Modify", GetOption(args, "--comment"), GetOption(args, "--title"), steps),
            JsonOptions);
        Console.WriteLine(await response.Content.ReadAsStringAsync());
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task<Guid> ResolveMasterGuidAsync(HttpClient http, string idOrName)
    {
        if (Guid.TryParse(idOrName, out var id))
        {
            return id;
        }

        var agent = await http.GetFromJsonAsync<AgentDto>(
            $"/api/agents/{Uri.EscapeDataString(idOrName)}", JsonOptions)
            ?? throw new InvalidOperationException($"Master '{idOrName}' not found.");
        if (!agent.Role.Equals("Master", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is not a Master.");
        }

        return agent.Id;
    }

    private static async Task<int> StatusAsync(string idOrName)
    {
        using var http = CreateHttp();
        var status = await http.GetFromJsonAsync<AgentStatusDto>(
            $"/api/agents/{Uri.EscapeDataString(idOrName)}/status", JsonOptions);
        Console.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
        return 0;
    }

    private static async Task<int> LogsAsync(string idOrName)
    {
        using var http = CreateHttp();
        var logs = await http.GetFromJsonAsync<List<ActionLogDto>>(
            $"/api/agents/{Uri.EscapeDataString(idOrName)}/logs", JsonOptions) ?? [];
        foreach (var log in logs.OrderBy(l => l.CreatedAt))
        {
            Console.WriteLine($"{log.CreatedAt:O} [{log.Category}] {log.Message}");
        }

        return 0;
    }

    private static async Task<int> ConfirmAsync(string[] args)
    {
        if (args.Length == 0 || !Guid.TryParse(args[0], out var stepId))
        {
            throw new ArgumentException("Usage: devage -confirm <stepId> --yes|--no");
        }

        var approved = args.Any(a => a is "--yes" or "-y");
        var denied = args.Any(a => a is "--no" or "-n");
        if (!approved && !denied)
        {
            throw new ArgumentException("Specify --yes or --no");
        }

        using var http = CreateHttp();
        using var response = await http.PostAsJsonAsync($"/api/confirmations/{stepId}",
            new ConfirmationDecisionRequest(approved, null), JsonOptions);
        Console.WriteLine(await response.Content.ReadAsStringAsync());
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string Prompt(string label, string defaultValue)
    {
        if (!string.IsNullOrEmpty(defaultValue))
        {
            Console.Write($"{label} [{defaultValue}]: ");
        }
        else
        {
            Console.Write($"{label}: ");
        }

        var input = Console.ReadLine();
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    private sealed record AvailableToolDto(string Name, string Description, List<ToolConfigFieldDto> ConfigFields);
    private sealed record ToolConfigFieldDto(string Key, string Label, string? DefaultValue, bool Required);
    private sealed record ToolBindingRequest(string ToolName, string ConfigJson);
    private sealed record BornAgentRequest(
        string Name,
        string WorkspaceRoot,
        string? Goal,
        string Role,
        Guid? MasterId,
        List<ToolBindingRequest> Tools);
    private sealed record StartAgentRequest(string? Goal);
    private sealed record AgentDto(
        Guid Id,
        string Name,
        string Role,
        Guid? MasterId,
        string Status,
        string WorkspaceRoot,
        string? Goal,
        DateTimeOffset CreatedAt,
        List<ToolBindingDto> ToolBindings);
    private sealed record ToolBindingDto(string ToolName, bool Enabled, string ConfigJson);
    private sealed record AgentStatusDto(
        Guid Id,
        string Name,
        string Status,
        string? Goal,
        Guid? ActivePlanId,
        string? ActivePlanTitle,
        int? CurrentStepOrdinal,
        string? CurrentStepTitle,
        string? CurrentStepStatus,
        DateTimeOffset UpdatedAt);
    private sealed record ActionLogDto(Guid Id, string Category, string Message, DateTimeOffset CreatedAt, string? DetailsJson);
    private sealed record PendingConfirmationDto(
        Guid AgentId,
        Guid PlanId,
        Guid StepId,
        int StepOrdinal,
        string StepTitle,
        string Description,
        string DecisionClass,
        string? Justification,
        string? ProposedViewpoint);
    private sealed record ConfirmationDecisionRequest(bool Approved, string? Comment);
    private sealed record AssignMasterRequest(string? MasterIdOrName);
    private sealed record MasterPlanStepDto(
        string Title,
        string Description,
        string? ToolName,
        string? ToolInputJson,
        string DecisionClass,
        string? Justification);
    private sealed record MasterPlanDecisionRequest(
        string Decision,
        string? Comment,
        string? ModifiedTitle,
        List<MasterPlanStepDto>? ModifiedSteps);
    private sealed record PendingMasterPlanStepDto(
        int Ordinal,
        string Title,
        string Description,
        string? ToolName,
        string DecisionClass,
        string? Justification);
    private sealed record PendingMasterPlanDto(
        Guid PlanId,
        Guid AgentId,
        string AgentName,
        Guid MasterId,
        string MasterName,
        string Title,
        string Goal,
        string ReviewStatus,
        DateTimeOffset CreatedAt,
        List<PendingMasterPlanStepDto> Steps);
}
