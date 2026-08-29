using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.TaskSessions;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class WorkflowSessionParticipantBinderTests
{
    [Fact]
    public void Bind_AddsSessionIdOnlyToWorkflowStateBindingWithoutMutatingTemplate()
    {
        var workflowBinding = new McpServerSessionBinding
        {
            ServerId = BuiltInTaskSessionMcpServerTools.Descriptor.Id
        };
        var unrelatedBinding = new McpServerSessionBinding { ServerName = "Unrelated MCP Server" };
        unrelatedBinding.Parameters["scope"] = "unrelated";
        var sourceAgent = new AgentTemplateDefinition
        {
            McpServerBindings = [workflowBinding, unrelatedBinding]
        };
        var participant = Materialized(sourceAgent);

        var bound = new WorkflowSessionParticipantBinder().Bind(participant, "session-42");

        var boundAgent = Assert.IsType<MaterializedLlmParticipantSource>(bound.Source).Agent;
        Assert.NotSame(sourceAgent, boundAgent);
        Assert.Equal("session-42", GetWorkflowStateBinding(boundAgent).Parameters[TaskSessionStore.SessionIdParameter]);
        var boundUnrelatedBinding = Assert.Single(boundAgent.McpServerBindings, binding =>
            string.Equals(binding.ServerName, unrelatedBinding.ServerName, StringComparison.Ordinal));
        Assert.Equal("unrelated", boundUnrelatedBinding.Parameters["scope"]);
        Assert.False(boundUnrelatedBinding.Parameters.ContainsKey(TaskSessionStore.SessionIdParameter));
        Assert.False(unrelatedBinding.Parameters.ContainsKey(TaskSessionStore.SessionIdParameter));
        Assert.False(workflowBinding.Parameters.ContainsKey(TaskSessionStore.SessionIdParameter));
    }

    [Fact]
    public void Bind_ReturnsNonMaterializedParticipantWithoutTransformation()
    {
        var participant = Materialized(new AgentTemplateDefinition()) with
        {
            Source = new ReferencedParticipantSource(new AgentDefinitionReference(
                AgentDefinitionKind.SavedWorkflow,
                Guid.NewGuid().ToString()))
        };

        var bound = new WorkflowSessionParticipantBinder().Bind(participant, "session-42");

        Assert.Same(participant, bound);
    }

    private static WorkflowRuntimeParticipant Materialized(AgentTemplateDefinition agent) => new()
    {
        Id = "advocate",
        DisplayName = "Advocate",
        Summary = "",
        RuntimeKind = AgentRuntimeKind.LlmAgent,
        Source = new MaterializedLlmParticipantSource(agent)
    };

    private static McpServerSessionBinding GetWorkflowStateBinding(AgentTemplateDefinition agent) =>
        Assert.Single(agent.McpServerBindings, binding =>
            binding.ServerId == BuiltInTaskSessionMcpServerTools.Descriptor.Id);
}
