using Devage.Core.Domain;

namespace Devage.Core.Planning;

public interface IPlanEngine
{
    Task<Plan> CreatePlanAsync(Agent agent, string goal, CancellationToken cancellationToken = default);
}

public interface IDecisionClassifier
{
    Task<(DecisionClass Class, string Justification)> ClassifyAsync(
        Agent agent,
        PlanStep step,
        CancellationToken cancellationToken = default);
}

public interface IUserConfirmationGate
{
    Task<bool> RequestConfirmationAsync(
        Agent agent,
        Plan plan,
        PlanStep step,
        string? viewpoint,
        CancellationToken cancellationToken = default);
}

public sealed record MasterPlanDecisionResult(
    MasterPlanDecisionKind Kind,
    string? Comment,
    string? ModifiedTitle,
    IReadOnlyList<MasterPlanStepProposal>? ModifiedSteps);

public sealed record MasterPlanStepProposal(
    string Title,
    string Description,
    string? ToolName,
    string? ToolInputJson,
    DecisionClass DecisionClass,
    string? Justification);

public interface IMasterApprovalGate
{
    Task<MasterPlanDecisionResult> RequestApprovalAsync(
        Agent agent,
        Plan plan,
        CancellationToken cancellationToken = default);
}
