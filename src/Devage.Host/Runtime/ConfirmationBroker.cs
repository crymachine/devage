using System.Collections.Concurrent;
using System.Threading.Channels;
using Devage.Core.Contracts;
using Devage.Core.Domain;
using Devage.Core.Planning;

namespace Devage.Host.Runtime;

public sealed class ConfirmationBroker : IUserConfirmationGate
{
    private readonly ConcurrentDictionary<Guid, PendingConfirmation> _pending = new();

    public IReadOnlyList<PendingConfirmationDto> ListPending(Guid? agentId = null)
    {
        return _pending.Values
            .Where(p => agentId is null || p.AgentId == agentId)
            .Select(p => p.ToDto())
            .ToList();
    }

    public async Task<bool> RequestConfirmationAsync(
        Agent agent,
        Plan plan,
        PlanStep step,
        string? viewpoint,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<bool>(1);
        var pending = new PendingConfirmation(
            agent.Id,
            plan.Id,
            step.Id,
            step.Ordinal,
            step.Title,
            step.Description,
            step.DecisionClass.ToString(),
            step.Justification,
            viewpoint,
            channel);

        _pending[step.Id] = pending;
        try
        {
            return await channel.Reader.ReadAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(step.Id, out _);
        }
    }

    public bool TryResolve(Guid stepId, bool approved)
    {
        if (!_pending.TryGetValue(stepId, out var pending))
        {
            return false;
        }

        return pending.Channel.Writer.TryWrite(approved);
    }

    private sealed record PendingConfirmation(
        Guid AgentId,
        Guid PlanId,
        Guid StepId,
        int StepOrdinal,
        string StepTitle,
        string Description,
        string DecisionClass,
        string? Justification,
        string? Viewpoint,
        Channel<bool> Channel)
    {
        public PendingConfirmationDto ToDto() => new(
            AgentId,
            PlanId,
            StepId,
            StepOrdinal,
            StepTitle,
            Description,
            DecisionClass,
            Justification,
            Viewpoint);
    }
}
