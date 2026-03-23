using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed class ResolvedChatAgent
{
    public ResolvedChatAgent(AgentDefinition agent, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(model);

        Agent = agent;
        Model = model;
    }

    public ResolvedChatAgent(AgentDescription agent, ServerModel model)
        : this(AgentDefinitionMapper.ToDefinition(agent), model)
    {
    }

    public AgentDefinition Agent { get; }

    public ServerModel Model { get; }
}
