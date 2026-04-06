using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public static class AgentExecutionSpecFactory
{
    public static AgentExecutionSpec FromTemplate(AgentTemplateDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CopyState(source, new AgentExecutionSpec());
    }

    public static AgentExecutionSpec FromTemplate(AgentTemplateDefinition source, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);

        var spec = FromTemplate(source);
        spec.LlmId = model.ServerId;
        spec.ModelName = model.ModelName;
        return spec;
    }

    public static AgentExecutionSpec WithResolvedModel(AgentExecutionSpec source, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);

        var spec = source.Clone();
        spec.LlmId = model.ServerId;
        spec.ModelName = model.ModelName;
        return spec;
    }

    private static TTarget CopyState<TTarget>(AgentModelBase source, TTarget target)
        where TTarget : AgentModelBase
    {
        target.Id = source.Id;
        target.AgentName = source.AgentName;
        target.Summary = source.Summary;
        target.Content = source.Content;
        target.ShortName = source.ShortName;
        target.AvatarText = source.AvatarText;
        target.ModelName = source.ModelName;
        target.LlmId = source.LlmId;
        target.Temperature = source.Temperature;
        target.RepeatPenalty = source.RepeatPenalty;
        target.RuntimeAgentId = source.RuntimeAgentId;
        target.FunctionSettings = new FunctionSettings
        {
            AutoSelectCount = source.FunctionSettings.AutoSelectCount
        };
        target.ExecutionSettings = new AgentExecutionSettings
        {
            MaxToolCalls = source.ExecutionSettings.MaxToolCalls,
            HistoryCompaction = new AgentHistoryCompactionSettings
            {
                Enabled = source.ExecutionSettings.HistoryCompaction.Enabled,
                Mode = source.ExecutionSettings.HistoryCompaction.Mode,
                KeepLastToolPairs = source.ExecutionSettings.HistoryCompaction.KeepLastToolPairs,
                ToolNames = source.ExecutionSettings.HistoryCompaction.ToolNames.ToList()
            }
        };
        target.McpServerBindings = source.McpServerBindings
            .Select(static binding => binding.Clone())
            .ToList();
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
        return target;
    }
}
