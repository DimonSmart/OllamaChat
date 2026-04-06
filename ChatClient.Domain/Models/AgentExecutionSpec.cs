namespace ChatClient.Domain.Models;

public sealed class AgentExecutionSpec : AgentModelBase
{
    public AgentExecutionSpec Clone()
    {
        return new AgentExecutionSpec
        {
            Id = Id,
            AgentName = AgentName,
            Summary = Summary,
            Content = Content,
            ShortName = ShortName,
            AvatarText = AvatarText,
            ModelName = ModelName,
            LlmId = LlmId,
            Temperature = Temperature,
            RepeatPenalty = RepeatPenalty,
            RuntimeAgentId = RuntimeAgentId,
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
}
