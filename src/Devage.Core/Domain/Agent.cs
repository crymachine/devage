namespace Devage.Core.Domain;

public sealed class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public AgentRole Role { get; set; } = AgentRole.Agent;
    public Guid? MasterId { get; set; }
    public Agent? Master { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Born;
    public string WorkspaceRoot { get; set; } = string.Empty;
    public string? Goal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ToolBinding> ToolBindings { get; set; } = new List<ToolBinding>();
    public ICollection<Plan> Plans { get; set; } = new List<Plan>();
    public ICollection<Checkpoint> Checkpoints { get; set; } = new List<Checkpoint>();
    public ICollection<ActionLog> ActionLogs { get; set; } = new List<ActionLog>();
    public ICollection<Agent> Subordinates { get; set; } = new List<Agent>();
}
