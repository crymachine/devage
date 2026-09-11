using System.Text.Json;
using Devage.Core.Contracts;
using Devage.Core.Domain;
using Devage.Core.Logging;
using Devage.Core.Planning;
using Devage.Core.Sandbox;
using Devage.Host.Runtime;
using Devage.Persistence;
using Devage.Tools.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Devage.Host.Services;

public sealed class AgentService
{
    private readonly DevageDbContext _db;
    private readonly IToolRegistry _toolRegistry;
    private readonly AgentRuntimeServiceBridge _runtime;
    private readonly IActionLogger _actionLogger;
    private readonly MasterApprovalBroker _masterApprovalBroker;

    public AgentService(
        DevageDbContext db,
        IToolRegistry toolRegistry,
        AgentRuntimeServiceBridge runtime,
        IActionLogger actionLogger,
        MasterApprovalBroker masterApprovalBroker)
    {
        _db = db;
        _toolRegistry = toolRegistry;
        _runtime = runtime;
        _actionLogger = actionLogger;
        _masterApprovalBroker = masterApprovalBroker;
    }

    public IReadOnlyList<AvailableToolDto> ListAvailableTools()
    {
        return _toolRegistry.GetAvailableTools()
            .Select(t => new AvailableToolDto(
                t.Name,
                t.Description,
                t.ConfigFields.Select(f => new ToolConfigFieldDto(f.Key, f.Label, f.DefaultValue, f.Required)).ToList()))
            .ToList();
    }

    public async Task<AgentDto> BornAsync(BornAgentRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required.");
        }

        if (await _db.Agents.AnyAsync(a => a.Name == request.Name, cancellationToken))
        {
            throw new InvalidOperationException($"Agent name '{request.Name}' already exists.");
        }

        if (!Enum.TryParse<AgentRole>(request.Role, ignoreCase: true, out var role))
        {
            role = AgentRole.Agent;
        }

        Guid? masterId = null;
        if (request.MasterId is Guid requestedMasterId)
        {
            masterId = await ResolveMasterIdAsync(requestedMasterId.ToString(), cancellationToken);
        }

        if (role == AgentRole.Master && masterId is not null)
        {
            throw new InvalidOperationException("A Master agent cannot be assigned to another Master.");
        }

        var workspace = string.IsNullOrWhiteSpace(request.WorkspaceRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "devage-workspaces", request.Name)
            : request.WorkspaceRoot;

        var sandbox = new WorkspaceSandbox(workspace);

        foreach (var toolReq in request.Tools)
        {
            var tool = _toolRegistry.GetTool(toolReq.ToolName)
                ?? throw new InvalidOperationException($"Unknown tool '{toolReq.ToolName}'.");
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolReq.ConfigJson) ? "{}" : toolReq.ConfigJson);
            await tool.ValidateConfigAsync(doc.RootElement, cancellationToken);
        }

        var agent = new Agent
        {
            Name = request.Name.Trim(),
            Role = role,
            MasterId = masterId,
            Status = AgentStatus.Born,
            WorkspaceRoot = sandbox.Root,
            Goal = request.Goal,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ToolBindings = request.Tools.Select(t => new ToolBinding
            {
                ToolName = t.ToolName,
                ConfigJson = string.IsNullOrWhiteSpace(t.ConfigJson) ? "{}" : t.ConfigJson,
                Enabled = true
            }).ToList()
        };

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync(cancellationToken);
        await _actionLogger.LogAsync(agent.Id, "lifecycle", "Agent born", cancellationToken: cancellationToken);
        return await GetAsync(agent.Id, cancellationToken) ?? throw new InvalidOperationException("Agent disappeared after create.");
    }

    public async Task<IReadOnlyList<AgentDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var agents = await _db.Agents.AsNoTracking()
            .Include(a => a.ToolBindings)
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);
        return agents.Select(Map).ToList();
    }

    public async Task<AgentDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var agent = await _db.Agents.AsNoTracking()
            .Include(a => a.ToolBindings)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        return agent is null ? null : Map(agent);
    }

    public async Task<Agent?> ResolveAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        if (Guid.TryParse(idOrName, out var id))
        {
            return await _db.Agents.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        }

        return await _db.Agents.FirstOrDefaultAsync(a => a.Name == idOrName, cancellationToken);
    }

    public async Task StartAsync(string idOrName, string? goal, CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");

        if (agent.Status == AgentStatus.Killed)
        {
            throw new InvalidOperationException("Cannot start a killed agent.");
        }

        if (!string.IsNullOrWhiteSpace(goal))
        {
            agent.Goal = goal;
        }

        agent.Status = AgentStatus.Running;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _runtime.Start(agent.Id);
    }

    public async Task StopAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");
        await _runtime.StopAsync(agent.Id, cancellationToken);
    }

    public async Task KillAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");
        await _runtime.KillAsync(agent.Id, cancellationToken);
    }

    public async Task<AgentStatusDto> StatusAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");

        var checkpoint = await _db.Checkpoints.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentId == agent.Id, cancellationToken);

        Plan? plan = null;
        PlanStep? step = null;
        if (checkpoint?.PlanId is Guid planId)
        {
            plan = await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, cancellationToken);
            if (checkpoint.CurrentStepId is Guid stepId)
            {
                step = await _db.PlanSteps.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stepId, cancellationToken);
            }
        }

        return new AgentStatusDto(
            agent.Id,
            agent.Name,
            agent.Status.ToString(),
            agent.Goal,
            plan?.Id,
            plan?.Title,
            step?.Ordinal,
            step?.Title,
            step?.Status.ToString(),
            agent.UpdatedAt);
    }

    public async Task<IReadOnlyList<ActionLogDto>> LogsAsync(
        string idOrName,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");

        return await _db.ActionLogs.AsNoTracking()
            .Where(l => l.AgentId == agent.Id)
            .OrderByDescending(l => l.CreatedAt)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(l => new ActionLogDto(l.Id, l.Category, l.Message, l.CreatedAt, l.DetailsJson))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlanSummaryDto>> ListPlansAsync(
        string idOrName,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");

        var plans = await _db.Plans.AsNoTracking()
            .Where(p => p.AgentId == agent.Id)
            .Include(p => p.Steps)
            .OrderByDescending(p => p.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

        return plans.Select(p =>
        {
            var latest = p.Steps.OrderByDescending(s => s.Ordinal).FirstOrDefault();
            return new PlanSummaryDto(
                p.Id,
                p.Title,
                p.Goal,
                p.MasterApproved,
                p.MasterReviewStatus.ToString(),
                p.CreatedAt,
                p.UpdatedAt,
                p.Steps.Count,
                latest?.Status.ToString());
        }).ToList();
    }

    public async Task<PlanDto?> GetPlanAsync(
        string idOrName,
        Guid planId,
        CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");

        var plan = await _db.Plans.AsNoTracking()
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == planId && p.AgentId == agent.Id, cancellationToken);

        return plan is null ? null : MapPlan(plan);
    }

    public async Task<PlanDto?> GetActivePlanAsync(
        string idOrName,
        CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");

        var checkpoint = await _db.Checkpoints.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentId == agent.Id, cancellationToken);

        Guid? planId = checkpoint?.PlanId;
        Plan? plan = null;
        if (planId is Guid id)
        {
            plan = await _db.Plans.AsNoTracking()
                .Include(p => p.Steps)
                .FirstOrDefaultAsync(p => p.Id == id && p.AgentId == agent.Id, cancellationToken);
        }

        if (plan is null)
        {
            plan = await _db.Plans.AsNoTracking()
                .Include(p => p.Steps)
                .Where(p => p.AgentId == agent.Id)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return plan is null ? null : MapPlan(plan);
    }

    public async Task<AgentDto> AssignMasterAsync(
        string idOrName,
        AssignMasterRequest request,
        CancellationToken cancellationToken = default)
    {
        var agent = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Agent '{idOrName}' not found.");

        if (agent.Role == AgentRole.Master && !string.IsNullOrWhiteSpace(request.MasterIdOrName))
        {
            throw new InvalidOperationException("A Master agent cannot be assigned to another Master.");
        }

        if (string.IsNullOrWhiteSpace(request.MasterIdOrName))
        {
            agent.MasterId = null;
        }
        else
        {
            var masterId = await ResolveMasterIdAsync(request.MasterIdOrName, cancellationToken);
            if (masterId == agent.Id)
            {
                throw new InvalidOperationException("An agent cannot be its own Master.");
            }

            agent.MasterId = masterId;
        }

        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _actionLogger.LogAsync(
            agent.Id,
            "master",
            agent.MasterId is null
                ? "Master assignment cleared (autonomous Agent)"
                : $"Assigned to Master {agent.MasterId}",
            cancellationToken: cancellationToken);

        return await GetAsync(agent.Id, cancellationToken)
            ?? throw new InvalidOperationException("Agent disappeared after master assign.");
    }

    public async Task<IReadOnlyList<AgentDto>> ListSubordinatesAsync(
        string masterIdOrName,
        CancellationToken cancellationToken = default)
    {
        var master = await ResolveAsync(masterIdOrName, cancellationToken)
            ?? throw new KeyNotFoundException($"Master '{masterIdOrName}' not found.");

        if (master.Role != AgentRole.Master)
        {
            throw new InvalidOperationException($"Agent '{master.Name}' is not a Master.");
        }

        var agents = await _db.Agents.AsNoTracking()
            .Include(a => a.ToolBindings)
            .Where(a => a.MasterId == master.Id)
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);

        return agents.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<PendingMasterPlanDto>> ListPendingMasterPlansAsync(
        string? masterIdOrName,
        CancellationToken cancellationToken = default)
    {
        Guid? masterId = null;
        string? masterName = null;
        if (!string.IsNullOrWhiteSpace(masterIdOrName))
        {
            var master = await ResolveAsync(masterIdOrName, cancellationToken)
                ?? throw new KeyNotFoundException($"Master '{masterIdOrName}' not found.");
            if (master.Role != AgentRole.Master)
            {
                throw new InvalidOperationException($"Agent '{master.Name}' is not a Master.");
            }

            masterId = master.Id;
            masterName = master.Name;
        }

        var inMemory = _masterApprovalBroker.ListPending(masterId);
        var pendingFromDb = await _db.Plans.AsNoTracking()
            .Include(p => p.Steps)
            .Include(p => p.Agent)
            .Where(p => p.MasterReviewStatus == MasterReviewStatus.Pending)
            .Where(p => p.Agent != null && p.Agent.MasterId != null)
            .Where(p => masterId == null || p.Agent!.MasterId == masterId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        var masterNames = await _db.Agents.AsNoTracking()
            .Where(a => a.Role == AgentRole.Master)
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        var merged = new Dictionary<Guid, PendingMasterPlanDto>();
        foreach (var item in inMemory)
        {
            var name = string.IsNullOrWhiteSpace(item.MasterName) && masterNames.TryGetValue(item.MasterId, out var n)
                ? n
                : item.MasterName;
            merged[item.PlanId] = item with { MasterName = name };
        }

        foreach (var plan in pendingFromDb)
        {
            if (merged.ContainsKey(plan.Id))
            {
                continue;
            }

            var mId = plan.Agent!.MasterId!.Value;
            merged[plan.Id] = new PendingMasterPlanDto(
                plan.Id,
                plan.AgentId,
                plan.Agent.Name,
                mId,
                masterNames.GetValueOrDefault(mId) ?? masterName ?? mId.ToString(),
                plan.Title,
                plan.Goal,
                plan.MasterReviewStatus.ToString(),
                plan.CreatedAt,
                plan.Steps.OrderBy(s => s.Ordinal).Select(s => new PendingMasterPlanStepDto(
                    s.Ordinal,
                    s.Title,
                    s.Description,
                    s.ToolName,
                    s.DecisionClass.ToString(),
                    s.Justification)).ToList());
        }

        return merged.Values.OrderBy(p => p.CreatedAt).ToList();
    }

    public async Task<object> DecideMasterPlanAsync(
        Guid planId,
        MasterPlanDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<MasterPlanDecisionKind>(request.Decision, ignoreCase: true, out var kind))
        {
            throw new ArgumentException("Decision must be Approve, Reject, or Modify.");
        }

        var plan = await _db.Plans
            .Include(p => p.Agent)
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
            ?? throw new KeyNotFoundException($"Plan '{planId}' not found.");

        if (plan.Agent?.MasterId is null)
        {
            throw new InvalidOperationException("Plan does not belong to an agent with a Master.");
        }

        if (plan.MasterReviewStatus is not MasterReviewStatus.Pending && !_masterApprovalBroker.IsPending(planId))
        {
            throw new InvalidOperationException($"Plan is not awaiting Master review (status={plan.MasterReviewStatus}).");
        }

        IReadOnlyList<MasterPlanStepProposal>? modifiedSteps = null;
        if (kind == MasterPlanDecisionKind.Modify)
        {
            if (request.ModifiedSteps is null || request.ModifiedSteps.Count == 0)
            {
                throw new ArgumentException("Modify requires ModifiedSteps.");
            }

            modifiedSteps = request.ModifiedSteps.Select(s =>
            {
                if (!Enum.TryParse<DecisionClass>(s.DecisionClass, ignoreCase: true, out var dc))
                {
                    dc = DecisionClass.Routine;
                }

                return new MasterPlanStepProposal(
                    s.Title,
                    s.Description,
                    s.ToolName,
                    s.ToolInputJson,
                    dc,
                    s.Justification);
            }).ToList();
        }

        var result = new MasterPlanDecisionResult(kind, request.Comment, request.ModifiedTitle, modifiedSteps);
        if (_masterApprovalBroker.TryResolve(planId, result))
        {
            return new
            {
                planId,
                decision = kind.ToString(),
                appliedOffline = false,
                comment = request.Comment
            };
        }

        await ApplyOfflineMasterDecisionAsync(plan, result, cancellationToken);
        _masterApprovalBroker.SeedDecision(planId, result);
        return new
        {
            planId,
            decision = kind.ToString(),
            appliedOffline = true,
            comment = request.Comment
        };
    }

    private async Task ApplyOfflineMasterDecisionAsync(
        Plan plan,
        MasterPlanDecisionResult decision,
        CancellationToken cancellationToken)
    {
        switch (decision.Kind)
        {
            case MasterPlanDecisionKind.Approve:
                plan.MasterApproved = true;
                plan.MasterReviewStatus = MasterReviewStatus.Approved;
                break;
            case MasterPlanDecisionKind.Reject:
                plan.MasterApproved = false;
                plan.MasterReviewStatus = MasterReviewStatus.Rejected;
                foreach (var step in plan.Steps.Where(s =>
                             s.Status is PlanStepStatus.Pending or PlanStepStatus.WaitingApproval or PlanStepStatus.Running))
                {
                    step.Status = PlanStepStatus.Skipped;
                    step.CompletedAt = DateTimeOffset.UtcNow;
                    step.ResultSummary = "Rejected by Master.";
                }

                break;
            case MasterPlanDecisionKind.Modify:
                if (!string.IsNullOrWhiteSpace(decision.ModifiedTitle))
                {
                    plan.Title = decision.ModifiedTitle.Trim();
                }

                if (decision.ModifiedSteps is { Count: > 0 })
                {
                    _db.PlanSteps.RemoveRange(plan.Steps);
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

                plan.MasterApproved = true;
                plan.MasterReviewStatus = MasterReviewStatus.Modified;
                break;
            default:
            {
                MasterPlanDecisionKind unexpected = decision.Kind;
                throw new InvalidOperationException($"Unhandled master decision: {unexpected}");
            }
        }

        plan.MasterComment = decision.Comment;
        plan.MasterDecidedAt = DateTimeOffset.UtcNow;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (plan.Agent is not null)
        {
            await _actionLogger.LogAsync(
                plan.Agent.Id,
                "master",
                $"Master decision applied offline: {decision.Kind}",
                plan.Id,
                details: new { decision.Comment },
                cancellationToken: cancellationToken);

            if (decision.Kind == MasterPlanDecisionKind.Reject && plan.Agent.Status == AgentStatus.Running)
            {
                plan.Agent.Status = AgentStatus.Stopped;
                plan.Agent.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task<Guid> ResolveMasterIdAsync(string idOrName, CancellationToken cancellationToken)
    {
        var master = await ResolveAsync(idOrName, cancellationToken)
            ?? throw new InvalidOperationException($"Master '{idOrName}' not found.");

        if (master.Role != AgentRole.Master)
        {
            throw new InvalidOperationException($"Agent '{master.Name}' is not a Master (role={master.Role}).");
        }

        if (master.Status == AgentStatus.Killed)
        {
            throw new InvalidOperationException($"Master '{master.Name}' is killed.");
        }

        return master.Id;
    }

    private static PlanDto MapPlan(Plan plan) => new(
        plan.Id,
        plan.AgentId,
        plan.Title,
        plan.Goal,
        plan.MasterApproved,
        plan.MasterReviewStatus.ToString(),
        plan.MasterComment,
        plan.MasterDecidedAt,
        plan.CreatedAt,
        plan.UpdatedAt,
        plan.Steps
            .OrderBy(s => s.Ordinal)
            .Select(s => new PlanStepDto(
                s.Id,
                s.Ordinal,
                s.Title,
                s.Description,
                s.ToolName,
                s.DecisionClass.ToString(),
                s.Status.ToString(),
                s.Justification,
                s.ResultSummary,
                s.StartedAt,
                s.CompletedAt))
            .ToList());

    private static AgentDto Map(Agent agent) => new(
        agent.Id,
        agent.Name,
        agent.Role.ToString(),
        agent.MasterId,
        agent.Status.ToString(),
        agent.WorkspaceRoot,
        agent.Goal,
        agent.CreatedAt,
        agent.ToolBindings.Select(b => new ToolBindingDto(b.ToolName, b.Enabled, b.ConfigJson)).ToList());
}

/// <summary>
/// Thin bridge so scoped AgentService can talk to singleton runtime without circular DI issues.
/// </summary>
public sealed class AgentRuntimeServiceBridge
{
    private readonly AgentRuntimeService _runtime;

    public AgentRuntimeServiceBridge(AgentRuntimeService runtime) => _runtime = runtime;

    public void Start(Guid agentId) => _runtime.Start(agentId);
    public Task StopAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        _runtime.StopAsync(agentId, cancellationToken);
    public Task KillAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        _runtime.KillAsync(agentId, cancellationToken);
    public int RunningCount => _runtime.RunningCount;
}
