using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed class ResolvedChatAgent
{
    public ResolvedChatAgent(AgentDescription agent, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(model);

        Agent = agent;
        Model = model;
    }

    public AgentDescription Agent { get; }

    public ServerModel Model { get; }
}
