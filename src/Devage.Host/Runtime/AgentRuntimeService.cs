using System.Collections.Concurrent;
using System.Text.Json;
using Devage.Core.Domain;
using Devage.Core.Logging;
using Devage.Core.Planning;
using Devage.Core.Sandbox;
using Devage.Persistence;
using Devage.Tools.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Devage.Host.Runtime;

public sealed class AgentRuntimeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlanEngine _planEngine;
    private readonly IDecisionClassifier _decisionClassifier;
    private readonly ConfirmationBroker _confirmationBroker;
    private readonly MasterApprovalBroker _masterApprovalBroker;
    private readonly IToolRegistry _toolRegistry;
    private readonly IActionLogger _actionLogger;
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runs = new();

    public AgentRuntimeService(
        IServiceScopeFactory scopeFactory,
        IPlanEngine planEngine,
        IDecisionClassifier decisionClassifier,
        ConfirmationBroker confirmationBroker,
        MasterApprovalBroker masterApprovalBroker,
        IToolRegistry toolRegistry,
        IActionLogger actionLogger,
        ILogger<AgentRuntimeService> logger)
    {
        _scopeFactory = scopeFactory;
        _planEngine = planEngine;
        _decisionClassifier = decisionClassifier;
        _confirmationBroker = confirmationBroker;
        _masterApprovalBroker = masterApprovalBroker;
        _toolRegistry = toolRegistry;
        _actionLogger = actionLogger;
        _logger = logger;
    }

    public int RunningCount => _runs.Count;

    public async Task ResumeRunningAgentsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevageDbContext>();
        var running = await db.Agents
            .AsNoTracking()
            .Where(a => a.Status == AgentStatus.Running)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in running)
        {
            _logger.LogInformation("Resuming agent {AgentId} from checkpoint", id);
            StartInternal(id, resume: true);
        }
    }

    public void Start(Guid agentId) => StartInternal(agentId, resume: false);

    public async Task StopAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        if (_runs.TryRemove(agentId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevageDbContext>();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null || agent.Status == AgentStatus.Killed)
        {
            return;
        }

        agent.Status = AgentStatus.Stopped;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await _actionLogger.LogAsync(agentId, "lifecycle", "Agent stopped", cancellationToken: cancellationToken);
    }

    public async Task KillAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        if (_runs.TryRemove(agentId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevageDbContext>();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
        {
            return;
        }

        agent.Status = AgentStatus.Killed;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await _actionLogger.LogAsync(agentId, "lifecycle", "Agent killed (history retained)", cancellationToken: cancellationToken);
    }

    private void StartInternal(Guid agentId, bool resume)
    {
        if (_runs.ContainsKey(agentId))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        if (!_runs.TryAdd(agentId, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAgentLoopAsync(agentId, resume, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected on stop/kill
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent loop crashed for {AgentId}", agentId);
                await _actionLogger.LogAsync(agentId, "error", $"Agent loop crashed: {ex.Message}");
            }
            finally
            {
                if (_runs.TryRemove(agentId, out var removed))
                {
                    removed.Dispose();
                }
            }
        });
    }

    private async Task RunAgentLoopAsync(Guid agentId, bool resume, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DevageDbContext>();

        var agent = await db.Agents
            .Include(a => a.ToolBindings)
            .FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent {agentId} not found");

        if (agent.Status == AgentStatus.Killed)
        {
            return;
        }

        agent.Status = AgentStatus.Running;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await _actionLogger.LogAsync(agentId, "lifecycle", resume ? "Agent resumed" : "Agent started", cancellationToken: cancellationToken);

        var sandbox = new WorkspaceSandbox(agent.WorkspaceRoot);
        Plan? plan = null;
        Checkpoint? checkpoint = await db.Checkpoints.FirstOrDefaultAsync(c => c.AgentId == agentId, cancellationToken);

        if (checkpoint?.PlanId is Guid planId)
        {
            plan = await db.Plans
                .Include(p => p.Steps)
                .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);

            // Only reuse checkpointed plan when unfinished steps remain.
            if (plan is not null &&
                plan.Steps.All(s => s.Status is PlanStepStatus.Done or PlanStepStatus.Skipped or PlanStepStatus.Failed))
            {
                plan = null;
            }
            else if (plan is not null)
            {
                await _actionLogger.LogAsync(
                    agentId,
                    "lifecycle",
                    resume ? "Resuming plan from checkpoint" : "Continuing unfinished plan from checkpoint",
                    plan.Id,
                    cancellationToken: cancellationToken);
            }
        }

        if (plan is null)
        {
            var goal = agent.Goal ?? "Explore workspace and report status.";
            plan = await _planEngine.CreatePlanAsync(agent, goal, cancellationToken);
            db.Plans.Add(plan);
            await db.SaveChangesAsync(cancellationToken);
            await _actionLogger.LogAsync(agentId, "plan", $"Plan created: {plan.Title}", plan.Id, cancellationToken: cancellationToken);
        }

        if (!await EnsureMasterApprovalAsync(db, agent, plan, cancellationToken))
        {
            return;
        }

        // Reload steps in case Master modified the plan.
        plan = await db.Plans
            .Include(p => p.Steps)
            .FirstAsync(p => p.Id == plan.Id, cancellationToken);
        var steps = plan.Steps.OrderBy(s => s.Ordinal).ToList();
        var startIndex = 0;
        if (plan.MasterReviewStatus != MasterReviewStatus.Modified &&
            checkpoint?.CurrentStepId is Guid currentStepId)
        {
            var idx = steps.FindIndex(s => s.Id == currentStepId);
            if (idx >= 0)
            {
                startIndex = steps[idx].Status is PlanStepStatus.Done or PlanStepStatus.Skipped or PlanStepStatus.Failed
                    ? idx + 1
                    : idx;
            }
        }

        for (var i = startIndex; i < steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reload status in case of stop/kill from API
            await db.Entry(agent).ReloadAsync(cancellationToken);
            if (agent.Status is AgentStatus.Stopped or AgentStatus.Killed)
            {
                return;
            }

            var step = steps[i];
            step.Status = PlanStepStatus.Running;
            step.StartedAt = DateTimeOffset.UtcNow;
            await SaveCheckpointAsync(db, agentId, plan.Id, step.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await _actionLogger.LogAsync(agentId, "step", $"Running step {step.Ordinal}: {step.Title}", plan.Id, step.Id, cancellationToken: cancellationToken);

            var (decision, justification) = await _decisionClassifier.ClassifyAsync(agent, step, cancellationToken);
            step.DecisionClass = decision;
            step.Justification = justification;

            if (decision is DecisionClass.Important or DecisionClass.Critical)
            {
                step.Status = PlanStepStatus.WaitingApproval;
                await db.SaveChangesAsync(cancellationToken);
                await _actionLogger.LogAsync(
                    agentId,
                    "decision",
                    $"Awaiting user confirmation ({decision}): {step.Title}",
                    plan.Id,
                    step.Id,
                    new { justification, viewpoint = BuildViewpoint(step) },
                    cancellationToken);

                var approved = await _confirmationBroker.RequestConfirmationAsync(
                    agent, plan, step, BuildViewpoint(step), cancellationToken);

                if (!approved)
                {
                    step.Status = PlanStepStatus.Skipped;
                    step.CompletedAt = DateTimeOffset.UtcNow;
                    step.ResultSummary = "Denied by user.";
                    await db.SaveChangesAsync(cancellationToken);
                    await _actionLogger.LogAsync(agentId, "decision", "User denied step", plan.Id, step.Id, cancellationToken: cancellationToken);
                    continue;
                }

                step.Status = PlanStepStatus.Running;
                await db.SaveChangesAsync(cancellationToken);
            }

            try
            {
                var resultSummary = await ExecuteStepAsync(agent, step, sandbox, cancellationToken);
                step.Status = PlanStepStatus.Done;
                step.ResultSummary = resultSummary;
                step.CompletedAt = DateTimeOffset.UtcNow;
                await _actionLogger.LogAsync(agentId, "step", $"Completed step {step.Ordinal}", plan.Id, step.Id, new { resultSummary }, cancellationToken);
            }
            catch (Exception ex)
            {
                step.Status = PlanStepStatus.Failed;
                step.ResultSummary = ex.Message;
                step.CompletedAt = DateTimeOffset.UtcNow;
                await _actionLogger.LogAsync(agentId, "error", $"Step failed: {ex.Message}", plan.Id, step.Id, cancellationToken: cancellationToken);
            }

            await SaveCheckpointAsync(db, agentId, plan.Id, step.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await db.Entry(agent).ReloadAsync(cancellationToken);
        if (agent.Status == AgentStatus.Running)
        {
            agent.Status = AgentStatus.Stopped;
            agent.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await _actionLogger.LogAsync(agentId, "lifecycle", "Plan finished; agent stopped", plan.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task<bool> EnsureMasterApprovalAsync(
        DevageDbContext db,
        Agent agent,
        Plan plan,
        CancellationToken cancellationToken)
    {
        if (agent.MasterId is null)
        {
            if (plan.MasterReviewStatus == MasterReviewStatus.Pending)
            {
                plan.MasterReviewStatus = MasterReviewStatus.NotRequired;
                plan.MasterApproved = true;
                plan.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            return true;
        }

        if (plan.MasterApproved ||
            plan.MasterReviewStatus is MasterReviewStatus.Approved or MasterReviewStatus.Modified)
        {
            return true;
        }

        if (plan.MasterReviewStatus == MasterReviewStatus.Rejected)
        {
            await StopAfterMasterRejectAsync(db, agent, plan, cancellationToken);
            return false;
        }

        plan.MasterReviewStatus = MasterReviewStatus.Pending;
        plan.MasterApproved = false;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        string? masterName = null;
        var master = await db.Agents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == agent.MasterId.Value, cancellationToken);
        masterName = master?.Name;

        await _actionLogger.LogAsync(
            agent.Id,
            "master",
            $"Waiting for Master approval from '{masterName ?? agent.MasterId.ToString()}'",
            plan.Id,
            details: new
            {
                masterId = agent.MasterId,
                masterName,
                planTitle = plan.Title,
                stepCount = plan.Steps.Count
            },
            cancellationToken: cancellationToken);

        if (master is not null)
        {
            await _actionLogger.LogAsync(
                master.Id,
                "master",
                $"Plan pending review from agent '{agent.Name}': {plan.Title}",
                plan.Id,
                details: new { agentId = agent.Id, agentName = agent.Name },
                cancellationToken: cancellationToken);
        }

        var waitTask = _masterApprovalBroker.RequestApprovalAsync(agent, plan, cancellationToken);
        if (!string.IsNullOrWhiteSpace(masterName))
        {
            _masterApprovalBroker.UpdateMasterName(plan.Id, masterName);
        }

        var decision = await waitTask;

        await db.Entry(plan).ReloadAsync(cancellationToken);
        await db.Entry(plan).Collection(p => p.Steps).LoadAsync(cancellationToken);

        // Offline Master decisions may already be persisted before the broker unblocks.
        if (decision.Kind == MasterPlanDecisionKind.Approve &&
            plan.MasterReviewStatus == MasterReviewStatus.Approved)
        {
            return true;
        }

        if (decision.Kind == MasterPlanDecisionKind.Modify &&
            plan.MasterReviewStatus == MasterReviewStatus.Modified)
        {
            return true;
        }

        if (decision.Kind == MasterPlanDecisionKind.Reject &&
            plan.MasterReviewStatus == MasterReviewStatus.Rejected)
        {
            await StopAfterMasterRejectAsync(db, agent, plan, cancellationToken);
            return false;
        }

        switch (decision.Kind)
        {
            case MasterPlanDecisionKind.Approve:
                plan.MasterApproved = true;
                plan.MasterReviewStatus = MasterReviewStatus.Approved;
                plan.MasterComment = decision.Comment;
                plan.MasterDecidedAt = DateTimeOffset.UtcNow;
                plan.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await _actionLogger.LogAsync(
                    agent.Id,
                    "master",
                    "Master approved plan",
                    plan.Id,
                    details: new { decision.Comment },
                    cancellationToken: cancellationToken);
                return true;

            case MasterPlanDecisionKind.Reject:
                plan.MasterApproved = false;
                plan.MasterReviewStatus = MasterReviewStatus.Rejected;
                plan.MasterComment = decision.Comment;
                plan.MasterDecidedAt = DateTimeOffset.UtcNow;
                plan.UpdatedAt = DateTimeOffset.UtcNow;
                foreach (var step in plan.Steps.Where(s => s.Status is PlanStepStatus.Pending or PlanStepStatus.WaitingApproval or PlanStepStatus.Running))
                {
                    step.Status = PlanStepStatus.Skipped;
                    step.CompletedAt = DateTimeOffset.UtcNow;
                    step.ResultSummary = "Rejected by Master.";
                }

                await db.SaveChangesAsync(cancellationToken);
                await _actionLogger.LogAsync(
                    agent.Id,
                    "master",
                    "Master rejected plan",
                    plan.Id,
                    details: new { decision.Comment },
                    cancellationToken: cancellationToken);
                await StopAfterMasterRejectAsync(db, agent, plan, cancellationToken);
                return false;

            case MasterPlanDecisionKind.Modify:
                ApplyMasterModifications(db, plan, decision);
                plan.MasterApproved = true;
                plan.MasterReviewStatus = MasterReviewStatus.Modified;
                plan.MasterComment = decision.Comment;
                plan.MasterDecidedAt = DateTimeOffset.UtcNow;
                plan.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await _actionLogger.LogAsync(
                    agent.Id,
                    "master",
                    "Master modified and approved plan",
                    plan.Id,
                    details: new { decision.Comment, stepCount = plan.Steps.Count },
                    cancellationToken: cancellationToken);
                return true;

            default:
            {
                MasterPlanDecisionKind unexpected = decision.Kind;
                throw new InvalidOperationException($"Unhandled master decision: {unexpected}");
            }
        }
    }

    private static void ApplyMasterModifications(DevageDbContext db, Plan plan, MasterPlanDecisionResult decision)
    {
        if (!string.IsNullOrWhiteSpace(decision.ModifiedTitle))
        {
            plan.Title = decision.ModifiedTitle.Trim();
        }

        if (decision.ModifiedSteps is null || decision.ModifiedSteps.Count == 0)
        {
            return;
        }

        db.PlanSteps.RemoveRange(plan.Steps);
        plan.Steps.Clear();
        var ordinal = 0;
        foreach (var step in decision.ModifiedSteps)
        {
            ordinal++;
            plan.Steps.Add(new PlanStep
            {
                PlanId = plan.Id,
                Ordinal = ordinal,
                Title = step.Title,
                Description = step.Description,
                ToolName = step.ToolName,
                ToolInputJson = step.ToolInputJson,
                DecisionClass = step.DecisionClass,
                Justification = step.Justification,
                Status = PlanStepStatus.Pending
            });
        }
    }

    private async Task StopAfterMasterRejectAsync(
        DevageDbContext db,
        Agent agent,
        Plan plan,
        CancellationToken cancellationToken)
    {
        await db.Entry(agent).ReloadAsync(cancellationToken);
        if (agent.Status is AgentStatus.Killed)
        {
            return;
        }

        agent.Status = AgentStatus.Stopped;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await _actionLogger.LogAsync(
            agent.Id,
            "lifecycle",
            "Plan rejected by Master; agent stopped",
            plan.Id,
            cancellationToken: cancellationToken);
    }

    private async Task<string> ExecuteStepAsync(
        Agent agent,
        PlanStep step,
        WorkspaceSandbox sandbox,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.ToolName))
        {
            // Local workspace note write (sandboxed)
            var notesPath = sandbox.Resolve("devage-notes.md");
            var entry = $"## {DateTimeOffset.UtcNow:O} — {step.Title}{Environment.NewLine}{step.Description}{Environment.NewLine}{Environment.NewLine}";
            await File.AppendAllTextAsync(notesPath, entry, cancellationToken);
            return $"Wrote notes to {notesPath}";
        }

        var tool = _toolRegistry.GetTool(step.ToolName);
        if (tool is null)
        {
            throw new InvalidOperationException($"Tool '{step.ToolName}' is not available.");
        }

        var binding = agent.ToolBindings.FirstOrDefault(b =>
            b.Enabled && b.ToolName.Equals(step.ToolName, StringComparison.OrdinalIgnoreCase));
        if (binding is null)
        {
            throw new InvalidOperationException($"Tool '{step.ToolName}' is not bound/enabled for this agent.");
        }

        using var configDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(binding.ConfigJson) ? "{}" : binding.ConfigJson);
        using var inputDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(step.ToolInputJson) ? "{}" : step.ToolInputJson);
        await tool.ValidateConfigAsync(configDoc.RootElement, cancellationToken);

        var result = await tool.ExecuteAsync(new ToolExecutionContext
        {
            AgentId = agent.Id,
            WorkspaceRoot = sandbox.Root,
            Config = configDoc.RootElement.Clone(),
            Input = inputDoc.RootElement.Clone(),
            Metadata = BuildToolMetadata(step)
        }, cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "Tool failed");
        }

        var outPath = sandbox.Resolve(Path.Combine("tool-output", $"{step.Ordinal:00}-{tool.Name}.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, result.Output, cancellationToken);
        return result.Output.Length > 500 ? result.Output[..500] + "…" : result.Output;
    }

    private static async Task SaveCheckpointAsync(
        DevageDbContext db,
        Guid agentId,
        Guid planId,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        var checkpoint = await db.Checkpoints.FirstOrDefaultAsync(c => c.AgentId == agentId, cancellationToken);
        if (checkpoint is null)
        {
            checkpoint = new Checkpoint { AgentId = agentId };
            db.Checkpoints.Add(checkpoint);
        }

        checkpoint.PlanId = planId;
        checkpoint.CurrentStepId = stepId;
        checkpoint.ContextJson = JsonSerializer.Serialize(new
        {
            planId,
            stepId,
            updatedAt = DateTimeOffset.UtcNow
        });
        checkpoint.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string BuildViewpoint(PlanStep step) =>
        $"Proposed action: {step.Title}. {step.Description}. Class={step.DecisionClass}. {step.Justification}";

    private static Dictionary<string, string> BuildToolMetadata(PlanStep step)
    {
        var critical = step.DecisionClass == DecisionClass.Critical;
        // Reaching ExecuteStep after Important/Critical means the confirmation gate approved (or Routine).
        var approved = step.DecisionClass is DecisionClass.Routine or DecisionClass.Important or DecisionClass.Critical;
        return new Dictionary<string, string>
        {
            [ToolSendAuthorization.DecisionClassKey] = step.DecisionClass.ToString(),
            [ToolSendAuthorization.UserApprovedKey] = approved ? "true" : "false",
            [ToolSendAuthorization.SendAuthorizedKey] = critical ? "true" : "false"
        };
    }
}
