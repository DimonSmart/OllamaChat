using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.TaskSessions;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Services.TaskSessions;
using Moq;

namespace ChatClient.Tests;

public sealed class WorkflowSessionStateTests
{
    [Fact]
    public async Task CreateAsync_PersistsIntakeInputsAndDocumentMetadata()
    {
        var store = CreatePersistentTaskSessionStore();
        var state = new WorkflowSessionState(store);

        var sessionId = await state.CreateAsync(new WorkflowSessionInitialization(
            "Workflow title", "Workflow source",
            [
                new WorkflowSessionInput("text", new WorkflowSessionParameter("text", "hello")),
                new WorkflowSessionInput("number", new WorkflowSessionParameter("number", "3.14")),
                new WorkflowSessionInput("boolean", new WorkflowSessionParameter("boolean", "true")),
                new WorkflowSessionInput("json", new WorkflowSessionParameter("json", "{\"ok\":true}")),
                new WorkflowSessionInput("brief", Document: new WorkflowSessionDocument("# Brief", "Brief", "brief.md"))
            ]), TestContext.Current.CancellationToken);

        var snapshot = await store.GetSessionAsync(sessionId, TestContext.Current.CancellationToken);
        var document = await store.GetDocumentAsync(sessionId, "brief", TestContext.Current.CancellationToken);
        var parameters = await Task.WhenAll(snapshot.Parameters.Select(parameter =>
            store.GetParameterAsync(sessionId, parameter.Key, TestContext.Current.CancellationToken)));

        Assert.Equal("intake", snapshot.Phase);
        Assert.Equal("Workflow title", snapshot.Title);
        Assert.Equal("Workflow source", snapshot.Description);
        Assert.Equal(4, parameters.Length);
        Assert.Contains(parameters, parameter => parameter is { Key: "text", ValueKind: "text", Value: "hello" });
        Assert.Contains(parameters, parameter => parameter is { Key: "number", ValueKind: "number", Value: "3.14" });
        Assert.Contains(parameters, parameter => parameter is { Key: "boolean", ValueKind: "boolean", Value: "true" });
        Assert.Contains(parameters, parameter => parameter is { Key: "json", ValueKind: "json", Value: "{\"ok\":true}" });
        Assert.Equal(("brief", "Brief", "# Brief", "brief.md"), (document.Kind, document.Title, document.Markdown, document.Source));
    }
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
        var state = new StubWorkflowSessionState { Summary = "judge conclusion" };
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
        var resolver = new WorkflowResultResolver(new StubWorkflowSessionState());
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
        Assert.Contains(constructorParameters, parameter => parameter.ParameterType == typeof(IWorkflowSessionState));
    }

    private static WorkflowSessionState CreateState(string? phase, string? summaryLabel)
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

        return new WorkflowSessionState(
            new TaskSessionStore(new McpServerSessionContext(null), repository.Object));
    }

    private static TaskSessionStore CreatePersistentTaskSessionStore()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "OllamaChat.Tests", Guid.NewGuid().ToString("N"), "task-sessions.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var binding = new McpServerSessionBinding();
        binding.Parameters[TaskSessionStore.DatabaseFileParameter] = databasePath;
        return new TaskSessionStore(new McpServerSessionContext(binding), new SqliteTaskSessionRepository());
    }

    private sealed class StubWorkflowSessionState : IWorkflowSessionState
    {
        public string? Summary { get; init; }

        public List<string> SummaryRequests { get; } = [];

        public Task<string> CreateAsync(
            WorkflowSessionInitialization initialization,
            CancellationToken cancellationToken = default) => Task.FromResult("session");

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
