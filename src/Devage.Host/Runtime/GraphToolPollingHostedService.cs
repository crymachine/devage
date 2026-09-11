using System.Text.Json;
using Devage.Core.Domain;
using Devage.Core.Logging;
using Devage.Persistence;
using Devage.Tools.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Devage.Host.Runtime;

/// <summary>
/// Polls Email/Teams Graph tools every minute for Running agents. Failures are logged; Host never crashes.
/// </summary>
public sealed class GraphToolPollingHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IToolRegistry _toolRegistry;
    private readonly IActionLogger _actionLogger;
    private readonly ILogger<GraphToolPollingHostedService> _logger;

    public GraphToolPollingHostedService(
        IServiceScopeFactory scopeFactory,
        IToolRegistry toolRegistry,
        IActionLogger actionLogger,
        ILogger<GraphToolPollingHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _toolRegistry = toolRegistry;
        _actionLogger = actionLogger;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Graph tool polling started (every {Minutes} min).", Interval.TotalMinutes);

        // Initial delay so Host finishes boot / resume
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph polling cycle failed (non-fatal).");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevageDbContext>();

        var agents = await db.Agents
            .AsNoTracking()
            .Include(a => a.ToolBindings)
            .Where(a => a.Status == AgentStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var agent in agents)
        {
            foreach (var toolName in new[] { "email", "teams" })
            {
                var binding = agent.ToolBindings.FirstOrDefault(b =>
                    b.Enabled && b.ToolName.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                if (binding is null)
                {
                    continue;
                }

                var tool = _toolRegistry.GetTool(toolName);
                if (tool is null)
                {
                    continue;
                }

                try
                {
                    using var configDoc = JsonDocument.Parse(
                        string.IsNullOrWhiteSpace(binding.ConfigJson) ? "{}" : binding.ConfigJson);
                    using var inputDoc = JsonDocument.Parse("""{"action":"poll"}""");

                    var result = await tool.ExecuteAsync(new ToolExecutionContext
                    {
                        AgentId = agent.Id,
                        WorkspaceRoot = agent.WorkspaceRoot,
                        Config = configDoc.RootElement.Clone(),
                        Input = inputDoc.RootElement.Clone(),
                        Metadata = new Dictionary<string, string>
                        {
                            [ToolSendAuthorization.DecisionClassKey] = nameof(DecisionClass.Routine),
                            [ToolSendAuthorization.UserApprovedKey] = "false"
                        }
                    }, cancellationToken);

                    if (!result.Success)
                    {
                        await _actionLogger.LogAsync(
                            agent.Id,
                            "notify",
                            $"{toolName} poll skipped: {result.Error}",
                            cancellationToken: cancellationToken);
                        continue;
                    }

                    var hasNovelty = result.Data is not null &&
                                     ((result.Data.TryGetValue("newMessages", out var nm) && nm is int nmi && nmi > 0) ||
                                      (result.Data.TryGetValue("newEvents", out var ne) && ne is int nei && nei > 0));

                    await _actionLogger.LogAsync(
                        agent.Id,
                        "notify",
                        hasNovelty
                            ? $"{toolName} poll: novelties detected (summaries/drafts proposed)"
                            : $"{toolName} poll: no novelties",
                        details: new { tool = toolName, preview = Truncate(result.Output, 1500) },
                        cancellationToken: cancellationToken);

                    if (hasNovelty)
                    {
                        try
                        {
                            var dir = Path.Combine(agent.WorkspaceRoot, "tool-output");
                            Directory.CreateDirectory(dir);
                            var path = Path.Combine(dir, $"poll-{toolName}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.txt");
                            await File.WriteAllTextAsync(path, result.Output, cancellationToken);
                        }
                        catch (Exception writeEx)
                        {
                            _logger.LogDebug(writeEx, "Could not write poll output for agent {AgentId}", agent.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Graph {Tool} poll failed for agent {AgentId}", toolName, agent.Id);
                    await _actionLogger.LogAsync(
                        agent.Id,
                        "notify",
                        $"{toolName} poll error: {ex.Message}",
                        cancellationToken: cancellationToken);
                }
            }
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
