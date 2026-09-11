using System.Collections.Concurrent;
using System.Threading.Channels;
using Devage.Core.Contracts;
using Devage.Core.Domain;
using Devage.Core.Planning;

namespace Devage.Host.Runtime;

public sealed class MasterApprovalBroker : IMasterApprovalGate
{
    private readonly ConcurrentDictionary<Guid, PendingMasterReview> _pending = new();
    private readonly ConcurrentDictionary<Guid, MasterPlanDecisionResult> _seeded = new();

    public IReadOnlyList<PendingMasterPlanDto> ListPending(Guid? masterId = null, Guid? agentId = null)
    {
        return _pending.Values
            .Where(p => masterId is null || p.MasterId == masterId)
            .Where(p => agentId is null || p.AgentId == agentId)
            .Select(p => p.ToDto())
            .OrderBy(p => p.CreatedAt)
            .ToList();
    }

    public bool IsPending(Guid planId) => _pending.ContainsKey(planId);

    public void SeedDecision(Guid planId, MasterPlanDecisionResult decision) =>
        _seeded[planId] = decision;

    public async Task<MasterPlanDecisionResult> RequestApprovalAsync(
        Agent agent,
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        if (agent.MasterId is null)
        {
            return new MasterPlanDecisionResult(MasterPlanDecisionKind.Approve, null, null, null);
        }

        if (_seeded.TryRemove(plan.Id, out var seeded))
        {
            return seeded;
        }

        if (_pending.TryGetValue(plan.Id, out var existing))
        {
            return await existing.Channel.Reader.ReadAsync(cancellationToken);
        }

        var channel = Channel.CreateBounded<MasterPlanDecisionResult>(1);
        var pending = new PendingMasterReview(
            plan.Id,
            agent.Id,
            agent.Name,
            agent.MasterId.Value,
            string.Empty,
            plan.Title,
            plan.Goal,
            plan.CreatedAt,
            plan.Steps.OrderBy(s => s.Ordinal).Select(s => new PendingMasterPlanStepDto(
                s.Ordinal,
                s.Title,
                s.Description,
                s.ToolName,
                s.DecisionClass.ToString(),
                s.Justification)).ToList(),
            channel);

        _pending[plan.Id] = pending;
        try
        {
            if (_seeded.TryRemove(plan.Id, out var raceSeeded))
            {
                return raceSeeded;
            }

            return await channel.Reader.ReadAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(plan.Id, out _);
        }
    }

    public void UpdateMasterName(Guid planId, string masterName)
    {
        if (_pending.TryGetValue(planId, out var pending))
        {
            pending.MasterName = masterName;
        }
    }

    public bool TryResolve(Guid planId, MasterPlanDecisionResult decision)
    {
        if (_pending.TryGetValue(planId, out var pending))
        {
            return pending.Channel.Writer.TryWrite(decision);
        }

        SeedDecision(planId, decision);
        return false;
    }

    private sealed class PendingMasterReview
    {
        public PendingMasterReview(
            Guid planId,
            Guid agentId,
            string agentName,
            Guid masterId,
            string masterName,
            string title,
            string goal,
            DateTimeOffset createdAt,
            IReadOnlyList<PendingMasterPlanStepDto> steps,
            Channel<MasterPlanDecisionResult> channel)
        {
            PlanId = planId;
            AgentId = agentId;
            AgentName = agentName;
            MasterId = masterId;
            MasterName = masterName;
            Title = title;
            Goal = goal;
            CreatedAt = createdAt;
            Steps = steps;
            Channel = channel;
        }

        public Guid PlanId { get; }
        public Guid AgentId { get; }
        public string AgentName { get; }
        public Guid MasterId { get; }
        public string MasterName { get; set; }
        public string Title { get; }
        public string Goal { get; }
        public DateTimeOffset CreatedAt { get; }
        public IReadOnlyList<PendingMasterPlanStepDto> Steps { get; }
        public Channel<MasterPlanDecisionResult> Channel { get; }

        public PendingMasterPlanDto ToDto() => new(
            PlanId,
            AgentId,
            AgentName,
            MasterId,
            MasterName,
            Title,
            Goal,
            MasterReviewStatus.Pending.ToString(),
            CreatedAt,
            Steps);
    }
}
