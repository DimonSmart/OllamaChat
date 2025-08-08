using System.Collections.Generic;

namespace ChatClient.Shared.Models;

public class AgentDescription
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? AgentName { get; set; }
    public string? ModelName { get; set; }

    // Legacy properties for backward compatibility
    public List<string> Functions { get; set; } = [];
    public int AutoSelectCount { get; set; }

    // New unified function settings
    public FunctionSettings FunctionSettings { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() => Name;
}
