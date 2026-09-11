using System.Text.Json;
using Devage.Core.Domain;
using Devage.Core.Planning;
using Devage.Llm;
using Devage.Tools.Abstractions;
using Microsoft.Extensions.AI;

namespace Devage.Host.Runtime;

public sealed class LlmPlanEngine : IPlanEngine
{
    private readonly ILlmRouter _llmRouter;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<LlmPlanEngine> _logger;

    public LlmPlanEngine(ILlmRouter llmRouter, IToolRegistry toolRegistry, ILogger<LlmPlanEngine> logger)
    {
        _llmRouter = llmRouter;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public async Task<Plan> CreatePlanAsync(Agent agent, string goal, CancellationToken cancellationToken = default)
    {
        var enabledTools = agent.ToolBindings
            .Where(b => b.Enabled)
            .Select(b => b.ToolName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tools = _toolRegistry.GetAvailableTools()
            .Where(t => enabledTools.Contains(t.Name))
            .ToList();

        var toolCatalog = string.Join("\n", tools.Select(t =>
            $"- {t.Name}: {t.Description}\n  schema: {t.DescribeSchema()}"));

        var prompt = $$"""
            You are Devage plan engine. Produce a concise ordered plan as JSON only.
            Agent: {{agent.Name}}
            Role: {{agent.Role}}
            Workspace: {{agent.WorkspaceRoot}}
            Goal: {{goal}}

            Available tools:
            {{toolCatalog}}

            Return JSON of the form:
            {
              "title": "short title",
              "steps": [
                {
                  "title": "...",
                  "description": "...",
                  "toolName": "web" | "email" | "teams" | null,
                  "toolInput": { } | null,
                  "decisionClass": "Routine" | "Important" | "Critical",
                  "justification": "why this class"
                }
              ]
            }
            Prefer plan-first: research with web when useful before conclusions.
            For email/teams: use poll/summarize/draft_reply as Routine; any send/reply MUST be Critical.
            Important/Critical for irreversible, external-facing, or high-impact actions.
            """;

        try
        {
            var client = _llmRouter.GetProvider().ChatClient;
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cancellationToken);

            var text = response.Text?.Trim() ?? string.Empty;
            var plan = ParsePlan(agent, goal, text);
            if (plan.Steps.Count > 0)
            {
                return plan;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM plan generation failed; using fallback plan.");
        }

        return BuildFallbackPlan(agent, goal, tools);
    }

    private static Plan ParsePlan(Agent agent, string goal, string llmText)
    {
        var json = ExtractJson(llmText);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Plan" : "Plan";
        var plan = new Plan
        {
            AgentId = agent.Id,
            Title = title,
            Goal = goal,
            MasterApproved = agent.MasterId is null,
            MasterReviewStatus = agent.MasterId is null
                ? MasterReviewStatus.NotRequired
                : MasterReviewStatus.Pending
        };

        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return plan;
        }

        var ordinal = 0;
        foreach (var step in steps.EnumerateArray())
        {
            ordinal++;
            var decision = DecisionClass.Routine;
            if (step.TryGetProperty("decisionClass", out var dc) &&
                Enum.TryParse<DecisionClass>(dc.GetString(), ignoreCase: true, out var parsed))
            {
                decision = parsed;
            }

            string? toolInput = null;
            if (step.TryGetProperty("toolInput", out var ti) && ti.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                toolInput = ti.GetRawText();
            }

            plan.Steps.Add(new PlanStep
            {
                Ordinal = ordinal,
                Title = step.TryGetProperty("title", out var st) ? st.GetString() ?? $"Step {ordinal}" : $"Step {ordinal}",
                Description = step.TryGetProperty("description", out var sd) ? sd.GetString() ?? string.Empty : string.Empty,
                ToolName = step.TryGetProperty("toolName", out var tn) && tn.ValueKind == JsonValueKind.String
                    ? tn.GetString()
                    : null,
                ToolInputJson = toolInput,
                DecisionClass = decision,
                Justification = step.TryGetProperty("justification", out var j) ? j.GetString() : null,
                Status = PlanStepStatus.Pending
            });
        }

        return plan;
    }

    private static Plan BuildFallbackPlan(Agent agent, string goal, IReadOnlyList<IDevageTool> tools)
    {
        var plan = new Plan
        {
            AgentId = agent.Id,
            Title = $"Plan for {agent.Name}",
            Goal = goal,
            MasterApproved = agent.MasterId is null,
            MasterReviewStatus = agent.MasterId is null
                ? MasterReviewStatus.NotRequired
                : MasterReviewStatus.Pending
        };

        var ordinal = 0;
        if (tools.Any(t => t.Name.Equals("web", StringComparison.OrdinalIgnoreCase)))
        {
            ordinal++;
            plan.Steps.Add(new PlanStep
            {
                Ordinal = ordinal,
                Title = "Research goal online",
                Description = $"Fetch public documentation relevant to: {goal}",
                ToolName = "web",
                ToolInputJson = JsonSerializer.Serialize(new
                {
                    url = "https://learn.microsoft.com/en-us/dotnet/",
                    query = goal
                }),
                DecisionClass = DecisionClass.Routine,
                Justification = "Read-only research.",
                Status = PlanStepStatus.Pending
            });
        }

        ordinal++;
        plan.Steps.Add(new PlanStep
        {
            Ordinal = ordinal,
            Title = "Summarize findings in workspace",
            Description = $"Write a summary of the goal '{goal}' into the agent workspace notes.",
            ToolName = null,
            DecisionClass = DecisionClass.Important,
            Justification = "Writes into the agent workspace and defines next actions.",
            Status = PlanStepStatus.Pending
        });

        return plan;
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)];
        }

        return """{"title":"Empty","steps":[]}""";
    }
}
