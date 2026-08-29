using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Services.TaskSessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public sealed class OrchestrationWorkflowSessionBootstrapperTests
{
    [Fact]
    public async Task Bootstrap_RuntimeParticipantsReceiveWorkflowStateSessionBinding()
    {
        var workflowStateBinding = new McpServerSessionBinding
        {
            ServerId = BuiltInTaskSessionMcpServerTools.Descriptor.Id,
            ServerName = BuiltInTaskSessionMcpServerTools.Descriptor.Name,
            SelectedTools = ["session_get_document"],
            SelectAllTools = false
        };
        var unrelatedBinding = new McpServerSessionBinding
        {
            ServerName = "Unrelated MCP Server"
        };
        var agent = new AgentTemplateDefinition
        {
            AgentName = "Advocate",
            RuntimeAgentId = "advocate",
            McpServerBindings = [workflowStateBinding, unrelatedBinding]
        };
        var source = new MaterializedLlmParticipantSource(agent);
        var runtimeParticipant = new WorkflowRuntimeParticipant
        {
            Id = "advocate",
            DisplayName = "Advocate",
            Summary = "Debate participant",
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            Source = source
        };
        var workflow = new GroupChatWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            Participants =
            [
                new WorkflowParticipantDefinition
                {
                    Id = "advocate",
                    Source = new InlineAgentParticipantSource(agent)
                }
            ],
            ParticipantIds = ["advocate"]
        };
        var store = CreateTaskSessionStore();
        var bootstrapper = new OrchestrationWorkflowSessionBootstrapper(
            NullLogger<OrchestrationWorkflowSessionBootstrapper>.Instance,
            store,
            new MarkdownDocumentIntakeService(),
            Mock.Of<IWorkflowParticipantInvoker>());

        var result = await bootstrapper.BootstrapAsync(
            new OrchestrationWorkflowSessionStartRequest
            {
                Workflow = workflow,
                Participants = [runtimeParticipant],
                Configuration = new AppChatConfiguration("model", []),
                SessionTitle = "Workflow"
            },
            TestContext.Current.CancellationToken);

        var boundRuntimeAgent = Assert.IsType<MaterializedLlmParticipantSource>(
            Assert.Single(result.Request.Participants).Source).Agent;
        Assert.Equal(result.TaskSessionId, GetWorkflowStateBinding(boundRuntimeAgent).Parameters[TaskSessionStore.SessionIdParameter]);
        Assert.False(workflowStateBinding.Parameters.ContainsKey(TaskSessionStore.SessionIdParameter));
        Assert.False(unrelatedBinding.Parameters.ContainsKey(TaskSessionStore.SessionIdParameter));
    }

    private static McpServerSessionBinding GetWorkflowStateBinding(AgentTemplateDefinition agent) =>
        Assert.Single(agent.McpServerBindings, binding =>
            binding.ServerId == BuiltInTaskSessionMcpServerTools.Descriptor.Id);

    private static TaskSessionStore CreateTaskSessionStore()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "OllamaChat.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var binding = new McpServerSessionBinding();
        binding.Parameters[TaskSessionStore.DatabaseFileParameter] = Path.Combine(tempDirectory, "task-sessions.db");
        return new TaskSessionStore(
            new McpServerSessionContext(binding),
            new SqliteTaskSessionRepository());
    }
}
