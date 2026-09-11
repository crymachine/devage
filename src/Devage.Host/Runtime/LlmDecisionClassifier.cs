using System.Text.Json;
using Devage.Core.Domain;
using Devage.Core.Planning;
using Devage.Llm;
using Microsoft.Extensions.AI;

namespace Devage.Host.Runtime;

public sealed class LlmDecisionClassifier : IDecisionClassifier
{
    private readonly ILlmRouter _llmRouter;
    private readonly ILogger<LlmDecisionClassifier> _logger;

    public LlmDecisionClassifier(ILlmRouter llmRouter, ILogger<LlmDecisionClassifier> logger)
    {
        _llmRouter = llmRouter;
        _logger = logger;
    }

    public async Task<(DecisionClass Class, string Justification)> ClassifyAsync(
        Agent agent,
        PlanStep step,
        CancellationToken cancellationToken = default)
    {
        // Honor planner hint when already non-routine.
        if (step.DecisionClass is DecisionClass.Important or DecisionClass.Critical)
        {
            return (step.DecisionClass, step.Justification ?? step.DecisionClass.ToString());
        }

        if (IsGraphSendAction(step))
        {
            return (DecisionClass.Critical, "External Email/Teams send requires explicit authorization.");
        }

        try
        {
            var client = _llmRouter.GetProvider().ChatClient;
            var prompt = $$"""
                Classify this agent step. Reply JSON only: {"class":"Routine|Important|Critical","justification":"..."}
                Rules:
                - Routine: read-only, reversible, local analysis
                - Important: writes, config changes, user-visible outcomes needing confirmation
                - Critical: irreversible, external send, destructive, security-sensitive

                Agent role: {{agent.Role}}
                Step title: {{step.Title}}
                Description: {{step.Description}}
                Tool: {{step.ToolName ?? "none"}}
                """;

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cancellationToken);
            var text = response.Text ?? string.Empty;
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                var cls = DecisionClass.Routine;
                if (doc.RootElement.TryGetProperty("class", out var c) &&
                    Enum.TryParse<DecisionClass>(c.GetString(), ignoreCase: true, out var parsed))
                {
                    cls = parsed;
                }

                var justification = doc.RootElement.TryGetProperty("justification", out var j)
                    ? j.GetString() ?? cls.ToString()
                    : cls.ToString();
                return (cls, justification);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decision classification failed; defaulting to planner class.");
        }

        return Heuristic(step);
    }

    private static (DecisionClass, string) Heuristic(PlanStep step)
    {
        if (string.Equals(step.ToolName, "web", StringComparison.OrdinalIgnoreCase))
        {
            return (DecisionClass.Routine, "Read-only web fetch.");
        }

        if (IsGraphSendAction(step))
        {
            return (DecisionClass.Critical, "External Email/Teams send requires explicit authorization.");
        }

        if (string.Equals(step.ToolName, "email", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(step.ToolName, "teams", StringComparison.OrdinalIgnoreCase))
        {
            return (DecisionClass.Routine, "Graph poll/read/summarize/draft (no send).");
        }

        if (step.ToolName is null &&
            step.Description.Contains("write", StringComparison.OrdinalIgnoreCase))
        {
            return (DecisionClass.Important, "Workspace write requires confirmation.");
        }

        return (step.DecisionClass, step.Justification ?? "Planner default.");
    }

    private static bool IsGraphSendAction(PlanStep step)
    {
        if (!string.Equals(step.ToolName, "email", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(step.ToolName, "teams", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hay = $"{step.Title} {step.Description} {step.ToolInputJson}".ToLowerInvariant();
        return hay.Contains("\"send\"") ||
               hay.Contains("\"reply\"") ||
               hay.Contains("action\": \"send") ||
               hay.Contains("action\":\"send") ||
               hay.Contains("action\": \"reply") ||
               hay.Contains("action\":\"reply") ||
               hay.Contains(" send ") ||
               hay.Contains("reply to");
    }
}
