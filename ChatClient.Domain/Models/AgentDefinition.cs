using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public sealed class AgentDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string AgentName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string? ShortName { get; set; }

    public string? ModelName { get; set; }

    public Guid? LlmId { get; set; }

    public double? Temperature { get; set; }

    public double? RepeatPenalty { get; set; }

    [JsonIgnore]
    public string AgentId => string.IsNullOrWhiteSpace(ShortName) ? AgentName : ShortName;

    public FunctionSettings FunctionSettings { get; set; } = new();

    public AgentExecutionSettings ExecutionSettings { get; set; } = new();

    public List<McpServerSessionBinding> McpServerBindings { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AgentDefinition Clone()
    {
        return new AgentDefinition
        {
            Id = Id,
            AgentName = AgentName,
            Content = Content,
            ShortName = ShortName,
            ModelName = ModelName,
            LlmId = LlmId,
            Temperature = Temperature,
            RepeatPenalty = RepeatPenalty,
            FunctionSettings = new FunctionSettings
            {
                AutoSelectCount = FunctionSettings.AutoSelectCount
            },
            ExecutionSettings = new AgentExecutionSettings
            {
                MaxToolCalls = ExecutionSettings.MaxToolCalls,
                HistoryCompaction = new AgentHistoryCompactionSettings
                {
                    Enabled = ExecutionSettings.HistoryCompaction.Enabled,
                    Mode = ExecutionSettings.HistoryCompaction.Mode,
                    KeepLastToolPairs = ExecutionSettings.HistoryCompaction.KeepLastToolPairs,
                    ToolNames = ExecutionSettings.HistoryCompaction.ToolNames.ToList()
                }
            },
            McpServerBindings = McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList(),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }

    public override string ToString() => AgentName;
}
