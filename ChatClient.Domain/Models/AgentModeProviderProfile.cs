namespace ChatClient.Domain.Models;

public sealed class AgentModeProviderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Instructions { get; set; }
    public List<AgentModeProfile> Modes { get; set; } = [];
    public string? DefaultMode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
