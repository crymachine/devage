using System.Text.Json;
using Devage.Core.Domain;
using Devage.Core.Logging;
using Devage.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Devage.Host.Logging;

/// <summary>
/// Triple logging: console + local file under data/ + PostgreSQL action_logs.
/// </summary>
public sealed class TripleActionLogger : IActionLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TripleActionLogger> _logger;
    private readonly string _dataRoot;

    public TripleActionLogger(
        IServiceScopeFactory scopeFactory,
        IHostEnvironment environment,
        ILogger<TripleActionLogger> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var repoData = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "data"));
        _dataRoot = Directory.Exists(Path.GetDirectoryName(repoData)!) ? repoData : Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(_dataRoot);
    }

    public async Task LogAsync(
        Guid agentId,
        string category,
        string message,
        Guid? planId = null,
        Guid? stepId = null,
        object? details = null,
        CancellationToken cancellationToken = default)
    {
        var detailsJson = details is null ? null : JsonSerializer.Serialize(details, JsonOptions);
        var line = $"{DateTimeOffset.UtcNow:O} [{category}] agent={agentId} {message}";
        if (!string.IsNullOrWhiteSpace(detailsJson))
        {
            line += $" details={detailsJson}";
        }

        // Console
        switch (category.ToLowerInvariant())
        {
            case "error":
                _logger.LogError("{Message}", line);
                break;
            case "warn":
            case "warning":
                _logger.LogWarning("{Message}", line);
                break;
            default:
                _logger.LogInformation("{Message}", line);
                break;
        }

        // File
        try
        {
            var path = Path.Combine(_dataRoot, $"agent-{agentId:N}.log");
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write file log for agent {AgentId}", agentId);
        }

        // PostgreSQL
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DevageDbContext>();
            db.ActionLogs.Add(new ActionLog
            {
                AgentId = agentId,
                PlanId = planId,
                StepId = stepId,
                Category = category,
                Message = message,
                DetailsJson = detailsJson,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist action log for agent {AgentId}", agentId);
        }
    }
}
