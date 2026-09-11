namespace Devage.Core.Domain;

public sealed class Checkpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public Guid? PlanId { get; set; }
    public Guid? CurrentStepId { get; set; }
    public string ContextJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
