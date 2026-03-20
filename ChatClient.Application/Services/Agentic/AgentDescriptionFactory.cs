using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public static class AgentDescriptionFactory
{
    public static AgentDescription CreateDraft(AgentDescription source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Clone(source, source.ModelName, source.LlmId);
    }

    public static AgentDescription CreateRuntime(AgentDescription source, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        return Clone(source, model.ModelName, model.ServerId);
    }

    public static ResolvedChatAgent CreateResolved(AgentDescription source, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        return new ResolvedChatAgent(CreateRuntime(source, model), model);
    }

    public static AgentDescription CreateTransient(string agentName, string? shortName = null)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new ArgumentException("Agent name is required.", nameof(agentName));
        }

        return new AgentDescription
        {
            AgentName = agentName.Trim(),
            ShortName = string.IsNullOrWhiteSpace(shortName) ? null : shortName.Trim()
        };
    }

    private static AgentDescription Clone(AgentDescription source, string? modelName, Guid? llmId)
    {
        return new AgentDescription
        {
            Id = source.Id,
            AgentName = source.AgentName,
            Content = source.Content,
            ShortName = source.ShortName,
            ModelName = modelName,
            LlmId = llmId,
            Temperature = source.Temperature,
            RepeatPenalty = source.RepeatPenalty,
            FunctionSettings = new FunctionSettings
            {
                AutoSelectCount = source.FunctionSettings.AutoSelectCount,
                SelectedFunctions = [.. source.FunctionSettings.SelectedFunctions]
            },
            McpServerBindings = source.McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList(),
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }
}
