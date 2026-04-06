using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public static class ResolvedChatAgentFactory
{
    public static ResolvedChatAgent Resolve(AgentTemplateDefinition template, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(model);
        return new ResolvedChatAgent(AgentExecutionSpecFactory.FromTemplate(template, model), model);
    }

    public static ResolvedChatAgent Resolve(AgentExecutionSpec spec, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(model);
        return new ResolvedChatAgent(AgentExecutionSpecFactory.WithResolvedModel(spec, model), model);
    }
}
