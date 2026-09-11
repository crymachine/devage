namespace Devage.Core.Domain;

public sealed class ActionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public Guid? PlanId { get; set; }
    public Guid? StepId { get; set; }
    public string Category { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
    public string? DetailsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
