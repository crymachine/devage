using System.Text.Json;

namespace Devage.Tools.Email.Graph;

internal sealed class GraphAuthConfig
{
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string MailboxUserId { get; init; }
    public string TargetLanguage { get; init; } = "it";

    public static GraphAuthConfig From(JsonElement config)
    {
        var tenantId = Read(config, "tenantId") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_TENANT_ID");
        var clientId = Read(config, "clientId") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_CLIENT_ID");
        var clientSecret = Read(config, "clientSecret") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_CLIENT_SECRET");
        var mailbox = Read(config, "mailboxUserId") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_MAILBOX");
        var language = Read(config, "targetLanguage") ?? "it";

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(tenantId)) missing.Add("tenantId (or DEVAGE_GRAPH_TENANT_ID)");
        if (string.IsNullOrWhiteSpace(clientId)) missing.Add("clientId (or DEVAGE_GRAPH_CLIENT_ID)");
        if (string.IsNullOrWhiteSpace(clientSecret)) missing.Add("clientSecret (or DEVAGE_GRAPH_CLIENT_SECRET)");
        if (string.IsNullOrWhiteSpace(mailbox)) missing.Add("mailboxUserId (or DEVAGE_GRAPH_MAILBOX)");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Email/Graph is not configured. Missing: " + string.Join(", ", missing) +
                ". Provide values at -born or via environment variables.");
        }

        return new GraphAuthConfig
        {
            TenantId = tenantId!,
            ClientId = clientId!,
            ClientSecret = clientSecret!,
            MailboxUserId = mailbox!,
            TargetLanguage = language
        };
    }

    public static void ValidateOrThrow(JsonElement config)
    {
        _ = From(config);
    }

    private static string? Read(JsonElement config, string key)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(key, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString()?.Trim() : prop.ToString();
    }
}
