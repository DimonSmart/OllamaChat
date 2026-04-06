using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed class ResolvedChatAgent
{
    public ResolvedChatAgent(AgentExecutionSpec spec, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(model);

        Agent = spec;
        Model = model;
    }

    public AgentExecutionSpec Agent { get; }

    public ServerModel Model { get; }
}
