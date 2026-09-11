namespace Devage.Core.Domain;

public sealed class ToolBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
