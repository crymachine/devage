namespace Devage.Core.Domain;

public sealed class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public bool MasterApproved { get; set; }
    public MasterReviewStatus MasterReviewStatus { get; set; } = MasterReviewStatus.NotRequired;
    public string? MasterComment { get; set; }
    public DateTimeOffset? MasterDecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<PlanStep> Steps { get; set; } = new List<PlanStep>();
}
