using System.Text;
using System.Text.Json;
using Devage.Tools.Abstractions;
using Devage.Tools.Teams.Graph;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Devage.Tools.Teams;

/// <summary>
/// Microsoft Graph Teams channel/chat tool. Poll/read/summarize/draft are routine;
/// send requires Critical decision class and explicit user approval.
/// </summary>
public sealed class TeamsTool : IDevageTool
{
    public string Name => "teams";

    public string Description =>
        "Microsoft Graph Teams: poll channel/chat messages, summarize, draft replies from project history. " +
        "Send only with Critical user authorization.";

    public IReadOnlyList<ToolConfigField> ConfigFields { get; } =
    [
        new("tenantId", "Azure AD Tenant ID (blank → DEVAGE_GRAPH_TENANT_ID)", null, false),
        new("clientId", "App (client) ID (blank → DEVAGE_GRAPH_CLIENT_ID)", null, false),
        new("clientSecret", "Client secret (blank → DEVAGE_GRAPH_CLIENT_SECRET)", null, false),
        new("teamId", "Team ID (blank → DEVAGE_GRAPH_TEAM_ID)", null, false),
        new("channelId", "Channel ID (blank → DEVAGE_GRAPH_CHANNEL_ID)", null, false),
        new("userId", "User ID for chats (blank → DEVAGE_GRAPH_TEAMS_USER_ID)", null, false),
        new("targetLanguage", "Summary/reply language code", "it", false)
    ];

    public string DescribeSchema() =>
        """
        {
          "type": "object",
          "required": ["action"],
          "properties": {
            "action": {
              "type": "string",
              "enum": ["poll", "list_messages", "read_message", "summarize", "draft_reply", "send"]
            },
            "messageId": { "type": "string" },
            "body": { "type": "string" },
            "authorized": { "type": "boolean" },
            "top": { "type": "integer", "default": 10 }
          }
        }
        """;

    public Task ValidateConfigAsync(JsonElement config, CancellationToken cancellationToken = default)
    {
        // Allow binding without credentials at -born; ExecuteAsync / poll fail clearly if still missing.
        if (config.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)
        {
            return Task.CompletedTask;
        }

        throw new ArgumentException("teams tool config must be a JSON object.");
    }

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        GraphAuthConfig auth;
        try
        {
            auth = GraphAuthConfig.From(context.Config);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        if (!context.Input.TryGetProperty("action", out var actionProp) ||
            actionProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(actionProp.GetString()))
        {
            return ToolResult.Fail("Missing required input: action (poll|list_messages|read_message|summarize|draft_reply|send)");
        }

        var action = actionProp.GetString()!.Trim().ToLowerInvariant();

        try
        {
            var client = Graph.GraphClientFactory.Create(auth);
            return action switch
            {
                "poll" => await PollAsync(client, auth, context, cancellationToken),
                "list_messages" => await ListMessagesAsync(client, auth, context, cancellationToken),
                "read_message" => await ReadMessageAsync(client, auth, context, cancellationToken),
                "summarize" => await SummarizeAsync(client, auth, context, cancellationToken),
                "draft_reply" => await DraftReplyAsync(client, auth, context, cancellationToken),
                "send" => await SendAsync(client, auth, context, cancellationToken),
                _ => ToolResult.Fail($"Unknown teams action '{action}'.")
            };
        }
        catch (Exception ex) when (ex is ServiceException or InvalidOperationException or UnauthorizedAccessException)
        {
            return ToolResult.Fail($"Graph Teams error: {ex.Message}");
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Fail($"Graph authentication failed: {ex.Message}. Check Azure app registration and Teams permissions.");
        }
    }

    private static bool HasChannel(GraphAuthConfig auth) =>
        !string.IsNullOrWhiteSpace(auth.TeamId) && !string.IsNullOrWhiteSpace(auth.ChannelId);

    private static async Task<ToolResult> PollAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var store = new PollStateStore(context.WorkspaceRoot, "teams-poll-state.json");
        var state = await store.LoadAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"Teams poll @ {DateTimeOffset.UtcNow:O}");

        var messages = await FetchRecentMessagesAsync(client, auth, 25, cancellationToken);
        var novelty = 0;
        foreach (var msg in messages)
        {
            if (string.IsNullOrEmpty(msg.Id) || state.SeenMessageIds.Contains(msg.Id))
            {
                continue;
            }

            // Skip messages older than last check when we have a cursor
            if (state.LastCheckUtc is not null &&
                msg.CreatedDateTime is not null &&
                msg.CreatedDateTime < state.LastCheckUtc)
            {
                state.SeenMessageIds.Add(msg.Id);
                continue;
            }

            novelty++;
            state.SeenMessageIds.Add(msg.Id);
            var from = msg.From?.User?.DisplayName ?? msg.From?.User?.Id ?? "(unknown)";
            var body = msg.Body?.Content ?? "";
            sb.AppendLine();
            sb.AppendLine($"[NEW TEAMS MESSAGE] id={msg.Id}");
            sb.AppendLine(WorkspaceHistory.ProposeTranslatedSummary(auth.TargetLanguage, from, body));
            sb.AppendLine(WorkspaceHistory.ProposeReply(
                auth.TargetLanguage,
                WorkspaceHistory.LoadProjectContext(context.WorkspaceRoot),
                body));
        }

        if (state.SeenMessageIds.Count > 500)
        {
            state.SeenMessageIds = state.SeenMessageIds.TakeLast(300).ToList();
        }

        state.LastCheckUtc = DateTimeOffset.UtcNow;
        await store.SaveAsync(state, cancellationToken);

        if (novelty == 0)
        {
            sb.AppendLine("No new Teams messages since last poll.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine($"Novelties: {novelty} message(s). Summaries/drafts proposed — send still gated.");
        }

        return ToolResult.Ok(sb.ToString(), new Dictionary<string, object?> { ["newMessages"] = novelty });
    }

    private static async Task<ToolResult> ListMessagesAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var top = 10;
        if (context.Input.TryGetProperty("top", out var topProp) && topProp.TryGetInt32(out var t))
        {
            top = Math.Clamp(t, 1, 50);
        }

        var messages = await FetchRecentMessagesAsync(client, auth, top, cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"Teams messages (top {top}):");
        foreach (var msg in messages)
        {
            var from = msg.From?.User?.DisplayName ?? "(unknown)";
            sb.AppendLine($"- [{msg.CreatedDateTime:O}] {from}: {Truncate(msg.Body?.Content ?? "", 120)} | id={msg.Id}");
        }

        return ToolResult.Ok(sb.ToString());
    }

    private static async Task<ToolResult> ReadMessageAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var id = RequireString(context.Input, "messageId");
        if (id is null)
        {
            return ToolResult.Fail("read_message requires messageId");
        }

        ChatMessage? msg;
        if (HasChannel(auth))
        {
            msg = await client.Teams[auth.TeamId!].Channels[auth.ChannelId!].Messages[id]
                .GetAsync(cancellationToken: cancellationToken);
        }
        else
        {
            return ToolResult.Fail("read_message for chats requires channel binding in this version; use list_messages.");
        }

        if (msg is null)
        {
            return ToolResult.Fail($"Message '{id}' not found.");
        }

        return ToolResult.Ok($"""
            Teams message {msg.Id}
            From: {msg.From?.User?.DisplayName}
            Created: {msg.CreatedDateTime:O}

            {msg.Body?.Content}
            """);
    }

    private static async Task<ToolResult> SummarizeAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var id = RequireString(context.Input, "messageId");
        if (id is null)
        {
            return ToolResult.Fail("summarize requires messageId");
        }

        if (!HasChannel(auth))
        {
            return ToolResult.Fail("summarize requires teamId+channelId configuration.");
        }

        var msg = await client.Teams[auth.TeamId!].Channels[auth.ChannelId!].Messages[id]
            .GetAsync(cancellationToken: cancellationToken);
        if (msg is null)
        {
            return ToolResult.Fail($"Message '{id}' not found.");
        }

        return ToolResult.Ok(WorkspaceHistory.ProposeTranslatedSummary(
            auth.TargetLanguage,
            msg.From?.User?.DisplayName ?? "(unknown)",
            msg.Body?.Content ?? ""));
    }

    private static async Task<ToolResult> DraftReplyAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var id = RequireString(context.Input, "messageId");
        if (id is null)
        {
            return ToolResult.Fail("draft_reply requires messageId");
        }

        if (!HasChannel(auth))
        {
            return ToolResult.Fail("draft_reply requires teamId+channelId configuration.");
        }

        var msg = await client.Teams[auth.TeamId!].Channels[auth.ChannelId!].Messages[id]
            .GetAsync(cancellationToken: cancellationToken);
        if (msg is null)
        {
            return ToolResult.Fail($"Message '{id}' not found.");
        }

        return ToolResult.Ok(WorkspaceHistory.ProposeReply(
            auth.TargetLanguage,
            WorkspaceHistory.LoadProjectContext(context.WorkspaceRoot),
            msg.Body?.Content ?? ""));
    }

    private static async Task<ToolResult> SendAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!ToolSendAuthorization.IsSendAuthorized(context))
        {
            return ToolResult.Fail(ToolSendAuthorization.DenyMessage("send"));
        }

        var body = RequireString(context.Input, "body");
        if (body is null)
        {
            return ToolResult.Fail("send requires body");
        }

        if (!HasChannel(auth))
        {
            return ToolResult.Fail("send requires teamId+channelId configuration.");
        }

        var message = new ChatMessage
        {
            Body = new ItemBody { ContentType = BodyType.Text, Content = body }
        };

        var created = await client.Teams[auth.TeamId!].Channels[auth.ChannelId!].Messages
            .PostAsync(message, cancellationToken: cancellationToken);

        return ToolResult.Ok($"Teams message sent (id={created?.Id}) — authorized Critical send.");
    }

    private static async Task<List<ChatMessage>> FetchRecentMessagesAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        int top,
        CancellationToken cancellationToken)
    {
        if (HasChannel(auth))
        {
            var page = await client.Teams[auth.TeamId!].Channels[auth.ChannelId!].Messages.GetAsync(r =>
            {
                r.QueryParameters.Top = top;
            }, cancellationToken);
            return page?.Value?.ToList() ?? [];
        }

        // Fallback: recent chats for user — list chat ids then last messages (best-effort)
        if (string.IsNullOrWhiteSpace(auth.UserId))
        {
            throw new InvalidOperationException("Configure teamId+channelId or userId for Teams polling.");
        }

        var chats = await client.Users[auth.UserId].Chats.GetAsync(r =>
        {
            r.QueryParameters.Top = 5;
        }, cancellationToken);

        var result = new List<ChatMessage>();
        foreach (var chat in chats?.Value ?? [])
        {
            if (string.IsNullOrEmpty(chat.Id))
            {
                continue;
            }

            var msgs = await client.Users[auth.UserId].Chats[chat.Id].Messages.GetAsync(r =>
            {
                r.QueryParameters.Top = Math.Max(1, top / 5);
            }, cancellationToken);
            if (msgs?.Value is not null)
            {
                result.AddRange(msgs.Value);
            }
        }

        return result
            .OrderByDescending(m => m.CreatedDateTime)
            .Take(top)
            .ToList();
    }

    private static string? RequireString(JsonElement input, string key)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty(key, out var prop) ||
            prop.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(prop.GetString()))
        {
            return null;
        }

        return prop.GetString()!.Trim();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
