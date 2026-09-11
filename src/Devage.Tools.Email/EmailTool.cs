using System.Text;
using System.Text.Json;
using Devage.Tools.Abstractions;
using Devage.Tools.Email.Graph;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace Devage.Tools.Email;

/// <summary>
/// Microsoft Graph mailbox + calendar tool. Poll/read/summarize/draft are routine;
/// send/reply require Critical decision class and explicit user approval.
/// </summary>
public sealed class EmailTool : IDevageTool
{
    public string Name => "email";

    public string Description =>
        "Microsoft Graph email & calendar: poll inbox/events, summarize, draft replies from project history. " +
        "Send/reply only with Critical user authorization.";

    public IReadOnlyList<ToolConfigField> ConfigFields { get; } =
    [
        new("tenantId", "Azure AD Tenant ID (blank → DEVAGE_GRAPH_TENANT_ID)", null, false),
        new("clientId", "App (client) ID (blank → DEVAGE_GRAPH_CLIENT_ID)", null, false),
        new("clientSecret", "Client secret (blank → DEVAGE_GRAPH_CLIENT_SECRET)", null, false),
        new("mailboxUserId", "Mailbox UPN/object ID (blank → DEVAGE_GRAPH_MAILBOX)", null, false),
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
              "enum": ["poll", "list_inbox", "read_message", "summarize", "draft_reply", "send", "reply"]
            },
            "messageId": { "type": "string" },
            "to": { "type": "string", "description": "Recipient for send" },
            "subject": { "type": "string" },
            "body": { "type": "string" },
            "authorized": { "type": "boolean", "description": "Must be true with Critical approval for send/reply" },
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

        throw new ArgumentException("email tool config must be a JSON object.");
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
            return ToolResult.Fail("Missing required input: action (poll|list_inbox|read_message|summarize|draft_reply|send|reply)");
        }

        var action = actionProp.GetString()!.Trim().ToLowerInvariant();

        try
        {
            var client = Graph.GraphClientFactory.Create(auth);
            return action switch
            {
                "poll" => await PollAsync(client, auth, context, cancellationToken),
                "list_inbox" => await ListInboxAsync(client, auth, context, cancellationToken),
                "read_message" => await ReadMessageAsync(client, auth, context, cancellationToken),
                "summarize" => await SummarizeAsync(client, auth, context, cancellationToken),
                "draft_reply" => await DraftReplyAsync(client, auth, context, cancellationToken),
                "send" => await SendAsync(client, auth, context, cancellationToken),
                "reply" => await ReplyAsync(client, auth, context, cancellationToken),
                _ => ToolResult.Fail($"Unknown email action '{action}'.")
            };
        }
        catch (Exception ex) when (ex is ServiceException or InvalidOperationException or UnauthorizedAccessException or Azure.Identity.AuthenticationFailedException)
        {
            return ToolResult.Fail($"Graph email error: {ex.Message}");
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Fail($"Graph authentication failed: {ex.Message}. Check Azure app registration, secret, and application permissions.");
        }
    }

    private static async Task<ToolResult> PollAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var store = new PollStateStore(context.WorkspaceRoot, "email-poll-state.json");
        var state = await store.LoadAsync(cancellationToken);
        var since = state.LastMailCheckUtc ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        var sb = new StringBuilder();
        sb.AppendLine($"Email poll @ {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Mailbox: {auth.MailboxUserId}");

        var messages = await client.Users[auth.MailboxUserId].MailFolders["Inbox"].Messages.GetAsync(r =>
        {
            r.QueryParameters.Top = 25;
            r.QueryParameters.Orderby = ["receivedDateTime desc"];
            r.QueryParameters.Select = ["id", "subject", "from", "receivedDateTime", "bodyPreview", "isRead"];
            r.QueryParameters.Filter = $"receivedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        }, cancellationToken);

        var newMail = 0;
        foreach (var msg in messages?.Value ?? [])
        {
            if (string.IsNullOrEmpty(msg.Id) || state.SeenMessageIds.Contains(msg.Id))
            {
                continue;
            }

            newMail++;
            state.SeenMessageIds.Add(msg.Id);
            var from = msg.From?.EmailAddress?.Address ?? "(unknown)";
            var subject = msg.Subject ?? "(no subject)";
            var preview = msg.BodyPreview ?? "";
            sb.AppendLine();
            sb.AppendLine($"[NEW MAIL] id={msg.Id}");
            sb.AppendLine(WorkspaceHistory.ProposeTranslatedSummary(auth.TargetLanguage, subject, from, preview));
            sb.AppendLine(WorkspaceHistory.ProposeReply(
                auth.TargetLanguage,
                subject,
                WorkspaceHistory.LoadProjectContext(context.WorkspaceRoot),
                preview));
        }

        var calSince = state.LastCalendarCheckUtc ?? DateTimeOffset.UtcNow.AddHours(-1);
        var calEnd = DateTimeOffset.UtcNow.AddDays(7);
        var events = await client.Users[auth.MailboxUserId].CalendarView.GetAsync(r =>
        {
            r.QueryParameters.StartDateTime = calSince.UtcDateTime.ToString("o");
            r.QueryParameters.EndDateTime = calEnd.UtcDateTime.ToString("o");
            r.QueryParameters.Top = 25;
            r.QueryParameters.Select = ["id", "subject", "start", "end", "organizer", "isCancelled"];
        }, cancellationToken);

        var newEvents = 0;
        foreach (var ev in events?.Value ?? [])
        {
            if (string.IsNullOrEmpty(ev.Id) || state.SeenEventIds.Contains(ev.Id))
            {
                continue;
            }

            newEvents++;
            state.SeenEventIds.Add(ev.Id);
            sb.AppendLine();
            sb.AppendLine($"[NEW/UPDATED EVENT] id={ev.Id}");
            sb.AppendLine(WorkspaceHistory.ProposeTranslatedSummary(
                auth.TargetLanguage,
                ev.Subject ?? "(no subject)",
                ev.Organizer?.EmailAddress?.Address ?? "(organizer)",
                $"{ev.Start?.DateTime} → {ev.End?.DateTime}"));
        }

        // Cap seen ids
        if (state.SeenMessageIds.Count > 500)
        {
            state.SeenMessageIds = state.SeenMessageIds.TakeLast(300).ToList();
        }

        if (state.SeenEventIds.Count > 500)
        {
            state.SeenEventIds = state.SeenEventIds.TakeLast(300).ToList();
        }

        state.LastMailCheckUtc = DateTimeOffset.UtcNow;
        state.LastCalendarCheckUtc = DateTimeOffset.UtcNow;
        await store.SaveAsync(state, cancellationToken);

        if (newMail == 0 && newEvents == 0)
        {
            sb.AppendLine("No new messages or calendar events since last poll.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine($"Novelties: {newMail} message(s), {newEvents} event(s). Summaries/drafts proposed above — send/reply still gated.");
        }

        return ToolResult.Ok(sb.ToString(), new Dictionary<string, object?>
        {
            ["newMessages"] = newMail,
            ["newEvents"] = newEvents
        });
    }

    private static async Task<ToolResult> ListInboxAsync(
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

        var messages = await client.Users[auth.MailboxUserId].MailFolders["Inbox"].Messages.GetAsync(r =>
        {
            r.QueryParameters.Top = top;
            r.QueryParameters.Orderby = ["receivedDateTime desc"];
            r.QueryParameters.Select = ["id", "subject", "from", "receivedDateTime", "bodyPreview", "isRead"];
        }, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"Inbox (top {top}) for {auth.MailboxUserId}:");
        foreach (var msg in messages?.Value ?? [])
        {
            sb.AppendLine($"- [{msg.ReceivedDateTime:O}] {msg.Subject} | from={msg.From?.EmailAddress?.Address} | id={msg.Id}");
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

        var msg = await client.Users[auth.MailboxUserId].Messages[id].GetAsync(cancellationToken: cancellationToken);
        if (msg is null)
        {
            return ToolResult.Fail($"Message '{id}' not found.");
        }

        var body = msg.Body?.Content ?? msg.BodyPreview ?? "";
        var output = $"""
            Message {msg.Id}
            From: {msg.From?.EmailAddress?.Address}
            Subject: {msg.Subject}
            Received: {msg.ReceivedDateTime:O}

            {body}
            """;
        return ToolResult.Ok(output);
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

        var msg = await client.Users[auth.MailboxUserId].Messages[id].GetAsync(cancellationToken: cancellationToken);
        if (msg is null)
        {
            return ToolResult.Fail($"Message '{id}' not found.");
        }

        var summary = WorkspaceHistory.ProposeTranslatedSummary(
            auth.TargetLanguage,
            msg.Subject ?? "(no subject)",
            msg.From?.EmailAddress?.Address ?? "(unknown)",
            msg.BodyPreview ?? msg.Body?.Content ?? "");
        return ToolResult.Ok(summary);
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

        var msg = await client.Users[auth.MailboxUserId].Messages[id].GetAsync(cancellationToken: cancellationToken);
        if (msg is null)
        {
            return ToolResult.Fail($"Message '{id}' not found.");
        }

        var draft = WorkspaceHistory.ProposeReply(
            auth.TargetLanguage,
            msg.Subject ?? "(no subject)",
            WorkspaceHistory.LoadProjectContext(context.WorkspaceRoot),
            msg.BodyPreview ?? "");
        return ToolResult.Ok(draft);
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

        var to = RequireString(context.Input, "to");
        var subject = RequireString(context.Input, "subject");
        var body = RequireString(context.Input, "body");
        if (to is null || subject is null || body is null)
        {
            return ToolResult.Fail("send requires to, subject, and body");
        }

        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Text, Content = body },
            ToRecipients =
            [
                new Recipient { EmailAddress = new EmailAddress { Address = to } }
            ]
        };

        await client.Users[auth.MailboxUserId].SendMail.PostAsync(new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = true
        }, cancellationToken: cancellationToken);

        return ToolResult.Ok($"Email sent to {to} with subject '{subject}' (authorized Critical send).");
    }

    private static async Task<ToolResult> ReplyAsync(
        GraphServiceClient client,
        GraphAuthConfig auth,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!ToolSendAuthorization.IsSendAuthorized(context))
        {
            return ToolResult.Fail(ToolSendAuthorization.DenyMessage("reply"));
        }

        var id = RequireString(context.Input, "messageId");
        var body = RequireString(context.Input, "body");
        if (id is null || body is null)
        {
            return ToolResult.Fail("reply requires messageId and body");
        }

        await client.Users[auth.MailboxUserId].Messages[id].Reply.PostAsync(
            new Microsoft.Graph.Users.Item.Messages.Item.Reply.ReplyPostRequestBody
            {
                Comment = body
            },
            cancellationToken: cancellationToken);

        return ToolResult.Ok($"Reply sent to message {id} (authorized Critical send).");
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
}
