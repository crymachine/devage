using System.Text.Json;

namespace Devage.Tools.Teams.Graph;

internal sealed class GraphAuthConfig
{
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public string? TeamId { get; init; }
    public string? ChannelId { get; init; }
    public string? UserId { get; init; }
    public string TargetLanguage { get; init; } = "it";

    public static GraphAuthConfig From(JsonElement config)
    {
        var tenantId = Read(config, "tenantId") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_TENANT_ID");
        var clientId = Read(config, "clientId") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_CLIENT_ID");
        var clientSecret = Read(config, "clientSecret") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_CLIENT_SECRET");
        var teamId = Read(config, "teamId") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_TEAM_ID");
        var channelId = Read(config, "channelId") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_CHANNEL_ID");
        var userId = Read(config, "userId") ?? Environment.GetEnvironmentVariable("DEVAGE_GRAPH_TEAMS_USER_ID");
        var language = Read(config, "targetLanguage") ?? "it";

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(tenantId)) missing.Add("tenantId (or DEVAGE_GRAPH_TENANT_ID)");
        if (string.IsNullOrWhiteSpace(clientId)) missing.Add("clientId (or DEVAGE_GRAPH_CLIENT_ID)");
        if (string.IsNullOrWhiteSpace(clientSecret)) missing.Add("clientSecret (or DEVAGE_GRAPH_CLIENT_SECRET)");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Teams/Graph is not configured. Missing: " + string.Join(", ", missing) +
                ". Provide values at -born or via environment variables.");
        }

        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(channelId))
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new InvalidOperationException(
                    "Teams/Graph needs either teamId+channelId (channel polling) or userId (chats). " +
                    "Set at -born or DEVAGE_GRAPH_TEAM_ID / DEVAGE_GRAPH_CHANNEL_ID / DEVAGE_GRAPH_TEAMS_USER_ID.");
            }
        }

        return new GraphAuthConfig
        {
            TenantId = tenantId!,
            ClientId = clientId!,
            ClientSecret = clientSecret!,
            TeamId = teamId,
            ChannelId = channelId,
            UserId = userId,
            TargetLanguage = language
        };
    }

    public static void ValidateOrThrow(JsonElement config) => _ = From(config);

    private static string? Read(JsonElement config, string key)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(key, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString()?.Trim() : prop.ToString();
    }
}
