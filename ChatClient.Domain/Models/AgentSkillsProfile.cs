namespace ChatClient.Domain.Models;

public sealed class AgentSkillsProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IncludeClaudeSkills { get; set; }
    public List<SkillFileSource> FileSources { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class SkillFileSource
{
    public string Directory { get; set; } = string.Empty;
    public List<string> Patterns { get; set; } = [];
}

public enum AgentSkillSourceKind { Claude, WorkspaceFile, InstalledFile }
