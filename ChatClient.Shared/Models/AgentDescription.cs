using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class AgentDescription
{
    /// <summary>Internal identifier not displayed in UI.</summary>
    public Guid Id { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public string? ModelName { get; set; }
    public double? Temperature { get; set; }
    public double? RepeatPenalty { get; set; }

    [JsonIgnore]
    public string AgentId => string.IsNullOrWhiteSpace(ShortName) ? AgentName : ShortName;

    public FunctionSettings FunctionSettings { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() => AgentName;
}
