namespace Devage.Core.Domain;

public sealed class PlanStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlanId { get; set; }
    public Plan? Plan { get; set; }
    public int Ordinal { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public string? ToolInputJson { get; set; }
    public DecisionClass DecisionClass { get; set; } = DecisionClass.Routine;
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;
    public string? Justification { get; set; }
    public string? ResultSummary { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
