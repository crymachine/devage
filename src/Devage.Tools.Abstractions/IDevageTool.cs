using System.Text.Json;

namespace Devage.Tools.Abstractions;

public interface IDevageTool
{
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ToolConfigField> ConfigFields { get; }
    string DescribeSchema();
    Task ValidateConfigAsync(JsonElement config, CancellationToken cancellationToken = default);
    Task<ToolResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default);
}

public sealed record ToolConfigField(string Key, string Label, string? DefaultValue = null, bool Required = false);

public sealed class ToolExecutionContext
{
    public required Guid AgentId { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required JsonElement Config { get; init; }
    public required JsonElement Input { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

public sealed class ToolResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, object?>? Data { get; init; }

    public static ToolResult Ok(string output, IReadOnlyDictionary<string, object?>? data = null) =>
        new() { Success = true, Output = output, Data = data };

    public static ToolResult Fail(string error) =>
        new() { Success = false, Error = error, Output = string.Empty };
}

public interface IToolRegistry
{
    IReadOnlyList<IDevageTool> GetAvailableTools();
    IDevageTool? GetTool(string name);
    event EventHandler? ToolsChanged;
}

/// <summary>
/// Helpers for Critical-gated external send actions (Email / Teams).
/// Host sets metadata after user approval; tools refuse send without it.
/// </summary>
public static class ToolSendAuthorization
{
    public const string DecisionClassKey = "decisionClass";
    public const string UserApprovedKey = "userApproved";
    public const string SendAuthorizedKey = "sendAuthorized";

    public static bool IsSendAuthorized(ToolExecutionContext context)
    {
        if (context.Metadata.TryGetValue(SendAuthorizedKey, out var flag) &&
            flag.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var critical = context.Metadata.TryGetValue(DecisionClassKey, out var cls) &&
                       cls.Equals("Critical", StringComparison.OrdinalIgnoreCase);
        var approved = context.Metadata.TryGetValue(UserApprovedKey, out var ap) &&
                       ap.Equals("true", StringComparison.OrdinalIgnoreCase);

        if (critical && approved)
        {
            return true;
        }

        if (context.Input.ValueKind == JsonValueKind.Object &&
            context.Input.TryGetProperty("authorized", out var auth) &&
            ((auth.ValueKind == JsonValueKind.True) ||
             (auth.ValueKind == JsonValueKind.String &&
              auth.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)))
        {
            return critical && approved;
        }

        return false;
    }

    public static string DenyMessage(string action) =>
        $"Action '{action}' is gated: requires DecisionClass Critical and explicit user approval. " +
        "Do not send externally without confirmation.";
}
