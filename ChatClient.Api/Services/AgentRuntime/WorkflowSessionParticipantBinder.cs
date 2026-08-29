using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.AgentRuntime;

public interface IWorkflowSessionParticipantBinder
{
    WorkflowRuntimeParticipant Bind(WorkflowRuntimeParticipant participant, string sessionId);
}

public sealed class WorkflowSessionParticipantBinder : IWorkflowSessionParticipantBinder
{
    public WorkflowRuntimeParticipant Bind(WorkflowRuntimeParticipant participant, string sessionId) =>
        participant.Source is MaterializedLlmParticipantSource materialized
            ? participant with
            {
                Source = new MaterializedLlmParticipantSource(Bind(materialized.Agent, sessionId))
            }
            : participant;

    private static AgentTemplateDefinition Bind(AgentTemplateDefinition source, string sessionId)
    {
        var runtimeAgent = source.Clone();
        foreach (var binding in runtimeAgent.McpServerBindings)
        {
            if (string.Equals(binding.ServerName, BuiltInTaskSessionMcpServerTools.Descriptor.Name, StringComparison.OrdinalIgnoreCase) ||
                binding.ServerId == BuiltInTaskSessionMcpServerTools.Descriptor.Id)
            {
                binding.Parameters[TaskSessionStore.SessionIdParameter] = sessionId;
            }
        }

        return runtimeAgent;
    }
}
