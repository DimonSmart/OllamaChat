using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.TaskSessions;
using ChatClient.Domain.Models;
using Moq;

namespace ChatClient.Tests;

public sealed class WorkflowExecutionStateTests
{
    [Theory]
    [InlineData("complete", null, "complete", null, true)]
    [InlineData("complete", "final", "running", "final", true)]
    [InlineData("complete", "final", "running", "draft", false)]
    [InlineData(null, null, "running", null, false)]
    public async Task IsCompletedAsync_UsesConfiguredPhaseOrSummary(
        string? completionPhase,
        string? completionSummaryLabel,
        string? currentPhase,
        string? summaryLabel,
        bool expected)
    {
        var execution = new AgentWorkflowExecutionDefinition
        {
            CompletionPhase = completionPhase ?? string.Empty,
            CompletionSummaryLabel = completionSummaryLabel
        };
        var state = CreateState(currentPhase, summaryLabel);

        var completed = await state.IsCompletedAsync("session", execution, TestContext.Current.CancellationToken);

        Assert.Equal(expected, completed);
    }

    [Fact]
    public async Task ResultResolver_GroupChatUsesCompletionSummaryFromWorkflowState()
    {
        var state = new StubWorkflowExecutionState { Summary = "judge conclusion" };
        var resolver = new WorkflowResultResolver(state);
        var workflow = new GroupChatWorkflowDefinition
        {
            Id = "debate",
            DisplayName = "Debate",
            Execution = new AgentWorkflowExecutionDefinition { CompletionSummaryLabel = "final" }
        };

        var result = await resolver.ResolveAsync(
            new WorkflowResultResolutionContext(
                new OrchestrationWorkflowSessionStartRequest
                {
                    Workflow = workflow,
                    Configuration = new AppChatConfiguration("test", [])
                },
                "session",
                [new OrchestrationCompletedAssistantMessage(
                    new AppChatMessage("participant fallback", DateTime.UtcNow, AppChatRole.Assistant),
                    "participant")]),
            TestContext.Current.CancellationToken);

        Assert.Equal("judge conclusion", result!.FinalMessage.Content);
        Assert.Equal(workflow.Id, result.FinalMessage.AgentId);
        Assert.Equal(["session", "final"], state.SummaryRequests);
    }

    [Fact]
    public async Task ResultResolver_PreservesIdenticalMessagesFromDifferentParticipants()
    {
        var resolver = new WorkflowResultResolver(new StubWorkflowExecutionState());
        var workflow = new SequentialWorkflowDefinition
        {
            Id = "workflow",
            DisplayName = "Workflow",
            ParticipantOrder = ["writer", "reviewer"]
        };
        var first = new AppChatMessage(
            "Same response", DateTime.UtcNow, AppChatRole.Assistant,
            agentId: "writer", agentName: "Writer")
        { Id = Guid.NewGuid() };
        var second = new AppChatMessage(
            "Same response", DateTime.UtcNow, AppChatRole.Assistant,
            agentId: "reviewer", agentName: "Reviewer")
        { Id = Guid.NewGuid() };

        var result = await resolver.ResolveAsync(
            new WorkflowResultResolutionContext(
                new OrchestrationWorkflowSessionStartRequest
                {
                    Workflow = workflow,
                    Configuration = new AppChatConfiguration("test", [])
                },
                "session",
                [
                    new OrchestrationCompletedAssistantMessage(first, "writer"),
                    new OrchestrationCompletedAssistantMessage(second, "reviewer")
                ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result!.Messages.Count);
        Assert.Equal(["writer", "reviewer"], result.Messages.Select(static message => message.AgentId));
        Assert.Equal("reviewer", result.FinalMessage.AgentId);
    }

    [Fact]
    public void WorkflowExecutionEngine_DoesNotDependOnTaskSessionStore()
    {
        var constructorParameters = typeof(WorkflowExecutionEngine)
            .GetConstructors()
            .Single()
            .GetParameters();

        Assert.DoesNotContain(constructorParameters, parameter => parameter.ParameterType == typeof(TaskSessionStore));
        Assert.Contains(constructorParameters, parameter => parameter.ParameterType == typeof(IWorkflowExecutionState));
    }

    private static WorkflowExecutionState CreateState(string? phase, string? summaryLabel)
    {
        var repository = new Mock<ITaskSessionRepository>();
        repository.Setup(store => store.GetSessionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskSessionSnapshot(
                "session", null, null, phase, "active", DateTime.UtcNow, DateTime.UtcNow,
                [], [],
                string.IsNullOrWhiteSpace(summaryLabel)
                    ? []
                    : [new TaskSessionSummaryInfo(summaryLabel, DateTime.UtcNow, DateTime.UtcNow)]));

        return new WorkflowExecutionState(
            new TaskSessionStore(new McpServerSessionContext(null), repository.Object));
    }

    private sealed class StubWorkflowExecutionState : IWorkflowExecutionState
    {
        public string? Summary { get; init; }

        public List<string> SummaryRequests { get; } = [];

        public Task<bool> IsCompletedAsync(
            string sessionId,
            AgentWorkflowExecutionDefinition execution,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<string?> TryGetSummaryAsync(
            string sessionId,
            string label,
            CancellationToken cancellationToken = default)
        {
            SummaryRequests.Add(sessionId);
            SummaryRequests.Add(label);
            return Task.FromResult(Summary);
        }
    }
}
