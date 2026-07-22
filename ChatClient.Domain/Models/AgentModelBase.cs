using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public abstract class AgentModelBase
{
    private string? _runtimeAgentId;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string AgentName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public string? AvatarText { get; set; }
    public string? ModelName { get; set; }
    public Guid? LlmId { get; set; }
    public double? Temperature { get; set; }
    public double? RepeatPenalty { get; set; }
    public FunctionSettings FunctionSettings { get; set; } = new();
    public List<McpServerSessionBinding> McpServerBindings { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string? RuntimeAgentId
    {
        get => _runtimeAgentId;
        set => _runtimeAgentId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    [JsonIgnore]
    public string StableAgentId => Id == Guid.Empty ? string.Empty : Id.ToString("N");

    [JsonIgnore]
    public string AgentId => RuntimeAgentId ?? StableAgentId;

    public override string ToString() => AgentName;
}
