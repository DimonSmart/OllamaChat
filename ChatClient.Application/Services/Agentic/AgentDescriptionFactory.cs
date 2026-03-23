using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public static class AgentDescriptionFactory
{
    public static AgentDefinition CreateDefinition(AgentDescription source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return AgentDefinitionMapper.ToDefinition(source);
    }

    public static AgentDescription CreateDraft(AgentDescription source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return AgentDefinitionMapper.ToDescription(CreateDefinition(source));
    }

    public static AgentDescription CreateDraft(AgentDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return AgentDefinitionMapper.ToDescription(source.Clone());
    }

    public static AgentDescription CreateRuntime(AgentDescription source, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        return CreateRuntime(CreateDefinition(source), model);
    }

    public static AgentDescription CreateRuntime(AgentDefinition source, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);

        var runtime = source.Clone();
        runtime.ModelName = model.ModelName;
        runtime.LlmId = model.ServerId;
        return AgentDefinitionMapper.ToDescription(runtime);
    }

    public static ResolvedChatAgent CreateResolved(AgentDescription source, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        return CreateResolved(CreateDefinition(source), model);
    }

    public static ResolvedChatAgent CreateResolved(AgentDefinition source, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);
        return new ResolvedChatAgent(AgentDefinitionMapper.ToDefinition(CreateRuntime(source, model)), model);
    }

    public static AgentDescription CreateTransient(string agentName, string? shortName = null)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new ArgumentException("Agent name is required.", nameof(agentName));
        }

        return AgentDefinitionBuilder.New(agentName, shortName).BuildDescription();
    }
}
