using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public static class AgentDefinitionMapper
{
    public static AgentDefinition ToDefinition(AgentDescription source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AgentDefinition
        {
            Id = source.Id,
            AgentName = source.AgentName,
            Content = source.Content,
            ShortName = source.ShortName,
            ModelName = source.ModelName,
            LlmId = source.LlmId,
            Temperature = source.Temperature,
            RepeatPenalty = source.RepeatPenalty,
            FunctionSettings = new FunctionSettings
            {
                AutoSelectCount = source.FunctionSettings.AutoSelectCount
            },
            ExecutionSettings = new AgentExecutionSettings
            {
                MaxToolCalls = source.ExecutionSettings.MaxToolCalls,
                HistoryCompaction = new AgentHistoryCompactionSettings
                {
                    Enabled = source.ExecutionSettings.HistoryCompaction.Enabled,
                    Mode = source.ExecutionSettings.HistoryCompaction.Mode,
                    KeepLastToolPairs = source.ExecutionSettings.HistoryCompaction.KeepLastToolPairs,
                    ToolNames = source.ExecutionSettings.HistoryCompaction.ToolNames.ToList()
                }
            },
            McpServerBindings = source.McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList(),
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    public static AgentDescription ToDescription(AgentDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AgentDescription
        {
            Id = source.Id,
            AgentName = source.AgentName,
            Content = source.Content,
            ShortName = source.ShortName,
            ModelName = source.ModelName,
            LlmId = source.LlmId,
            Temperature = source.Temperature,
            RepeatPenalty = source.RepeatPenalty,
            FunctionSettings = new FunctionSettings
            {
                AutoSelectCount = source.FunctionSettings.AutoSelectCount
            },
            ExecutionSettings = new AgentExecutionSettings
            {
                MaxToolCalls = source.ExecutionSettings.MaxToolCalls,
                HistoryCompaction = new AgentHistoryCompactionSettings
                {
                    Enabled = source.ExecutionSettings.HistoryCompaction.Enabled,
                    Mode = source.ExecutionSettings.HistoryCompaction.Mode,
                    KeepLastToolPairs = source.ExecutionSettings.HistoryCompaction.KeepLastToolPairs,
                    ToolNames = source.ExecutionSettings.HistoryCompaction.ToolNames.ToList()
                }
            },
            McpServerBindings = source.McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList(),
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }
}
