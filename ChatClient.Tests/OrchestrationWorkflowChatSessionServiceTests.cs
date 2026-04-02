using System.Reflection;
using System.Runtime.CompilerServices;
using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public sealed class OrchestrationWorkflowChatSessionServiceTests
{
    private sealed class StubModelCapabilityService : IModelCapabilityService
    {
        public Task EnsureModelSupportedByServerAsync(ServerModel model, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> SupportsFunctionCallingAsync(ServerModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubRuntimeWorkflowBuilder(
        IOrchestrationWorkflowDefinition workflowDefinition,
        Workflow runtimeWorkflow) : IOrchestrationRuntimeWorkflowBuilder
    {
        public bool CanBuild(IOrchestrationWorkflowDefinition workflow) =>
            ReferenceEquals(workflow, workflowDefinition);

        public Workflow Build(
            IOrchestrationWorkflowDefinition workflow,
            IReadOnlyDictionary<string, AIAgent> agentsById,
            OrchestrationRuntimeBuildContext context) =>
            runtimeWorkflow;
    }

    private sealed class GatedStreamingChatExecutor(Task releaseFinalOutput)
        : ChatProtocolExecutor(
            "runtime://host",
            new ChatProtocolExecutorOptions
            {
                StringMessageChatRole = ChatRole.User,
                AutoSendTurnToken = false
            })
    {
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
            base.ConfigureProtocol(protocolBuilder)
                .SendsMessage<ChatMessage>()
                .YieldsOutput<AgentResponseUpdate>()
                .YieldsOutput<ChatMessage>();

        protected override async ValueTask TakeTurnAsync(
            List<ChatMessage> messages,
            IWorkflowContext context,
            bool? emitEvents,
            CancellationToken cancellationToken = default)
        {
            await context.YieldOutputAsync(CreateUpdate("Host", "Hello"), cancellationToken);
            await releaseFinalOutput.WaitAsync(cancellationToken);
            await context.YieldOutputAsync(CreateUpdate("Host", " world"), cancellationToken);
            await context.YieldOutputAsync(CreateOutputMessage("Host", "Hello world"), cancellationToken);
        }
    }

    private sealed class CancelableStreamingChatExecutor()
        : ChatProtocolExecutor(
            "runtime://host",
            new ChatProtocolExecutorOptions
            {
                StringMessageChatRole = ChatRole.User,
                AutoSendTurnToken = false
            })
    {
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
            base.ConfigureProtocol(protocolBuilder)
                .SendsMessage<ChatMessage>()
                .YieldsOutput<AgentResponseUpdate>();

        protected override async ValueTask TakeTurnAsync(
            List<ChatMessage> messages,
            IWorkflowContext context,
            bool? emitEvents,
            CancellationToken cancellationToken = default)
        {
            await context.YieldOutputAsync(CreateUpdate("Host", "Hello"), cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    [Fact]
    public async Task DrainWorkflowEventsAsync_MergesUpdatesAndFinalOutputIntoSingleAssistantMessage()
    {
        var service = CreateService();
        service.RegisterAgentIdentity("host", "Host", "runtime://host");

        await service.DrainWorkflowEventsAsync(
            StreamEvents(
            [
                CreateUpdateEvent("runtime://host", "Host", "Hello"),
                CreateUpdateEvent("runtime://host", "Host", " world"),
                CreateOutputEvent("runtime://host", "Host", "Hello world")
            ]),
            "model-a");

        var assistants = service.Messages.Where(message => message.Role == ChatRole.Assistant).ToList();

        var assistant = Assert.Single(assistants);
        Assert.Equal("Hello world", assistant.Content);
        Assert.False(assistant.IsStreaming);
        Assert.Equal("host", assistant.AgentId);
        Assert.Equal("Host", assistant.AgentName);
    }

    [Fact]
    public async Task DrainWorkflowEventsAsync_UsesAgentNameForSlotBasedWorkflowAgentIds()
    {
        var service = CreateService();
        service.RegisterAgentIdentity("debater_a", "Immanuel Kant", "runtime://debater-a");

        await service.DrainWorkflowEventsAsync(
            StreamEvents(
            [
                CreateUpdateEvent("runtime://debater-a", "Immanuel Kant", "Duty first."),
                CreateOutputEvent("runtime://debater-a", "Immanuel Kant", "Duty first.")
            ]),
            "model-a");

        var assistant = Assert.Single(service.Messages, message => message.Role == ChatRole.Assistant);
        Assert.Equal("debater_a", assistant.AgentId);
        Assert.Equal("Immanuel Kant", assistant.AgentName);
    }

    [Fact]
    public async Task DrainWorkflowEventsAsync_FinalizesPreviousSpeakerWhenNextSpeakerStartsStreaming()
    {
        var service = CreateService();
        service.RegisterAgentIdentity("host", "Host", "runtime://host");
        service.RegisterAgentIdentity("judge", "Judge", "runtime://judge");

        await service.DrainWorkflowEventsAsync(
            StreamEvents(
            [
                CreateUpdateEvent("runtime://host", "Host", "Host intro"),
                CreateUpdateEvent("runtime://judge", "Judge", "Judge review"),
                CreateOutputEvent("runtime://judge", "Judge", "Judge review")
            ]),
            "model-a");

        var assistants = service.Messages.Where(message => message.Role == ChatRole.Assistant).ToList();

        Assert.Equal(2, assistants.Count);
        Assert.Equal("Host intro", assistants[0].Content);
        Assert.False(assistants[0].IsStreaming);
        Assert.Equal("host", assistants[0].AgentId);
        Assert.Equal("Judge review", assistants[1].Content);
        Assert.False(assistants[1].IsStreaming);
        Assert.Equal("judge", assistants[1].AgentId);
    }

    [Fact]
    public async Task DrainWorkflowEventsAsync_SkipsTranscriptMessagesAlreadyShownViaStreaming()
    {
        var service = CreateService();
        service.RegisterAgentIdentity("host", "Host", "runtime://host");
        service.RegisterAgentIdentity("judge", "Judge", "runtime://judge");

        await service.DrainWorkflowEventsAsync(
            StreamEvents(
            [
                CreateUpdateEvent("runtime://host", "Host", "Host intro"),
                CreateUpdateEvent("runtime://judge", "Judge", "Judge review"),
                CreateOutputListEvent(
                    "runtime://group-chat",
                    CreateOutputMessage("Host", "Host intro"),
                    CreateOutputMessage("Judge", "Judge review"))
            ]),
            "model-a");

        var assistants = service.Messages.Where(message => message.Role == ChatRole.Assistant).ToList();

        Assert.Equal(2, assistants.Count);
        Assert.Equal("Host intro", assistants[0].Content);
        Assert.Equal("host", assistants[0].AgentId);
        Assert.Equal("Judge review", assistants[1].Content);
        Assert.Equal("judge", assistants[1].AgentId);
    }

    [Fact]
    public async Task DrainWorkflowEventsAsync_FinalizesCurrentStreamBeforeRethrowingExecutorFailure()
    {
        var service = CreateService();
        service.RegisterAgentIdentity("host", "Host", "runtime://host");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DrainWorkflowEventsAsync(
                StreamEvents(
                [
                    CreateUpdateEvent("runtime://host", "Host", "Hello"),
                    new ExecutorFailedEvent("runtime://host", new InvalidOperationException("boom"))
                ]),
                "model-a"));

        Assert.Equal("boom", exception.Message);

        var assistant = Assert.Single(service.Messages, message => message.Role == ChatRole.Assistant);
        Assert.Equal("Hello", assistant.Content);
        Assert.False(assistant.IsStreaming);
    }

    [Fact]
    public async Task KickoffAsync_StreamsFirstWorkflowMessageBeforeRunCompletes()
    {
        var releaseFinalOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = CreateWorkflowDefinition();
        var runtimeWorkflow = new WorkflowBuilder(new GatedStreamingChatExecutor(releaseFinalOutput.Task))
            .WithOutputFrom("runtime://host")
            .Build();
        var taskSessionStore = CreateTaskSessionStore(CreateTaskStorePath());
        var session = await taskSessionStore.CreateSessionAsync("Workflow stream", null, CancellationToken.None);
        var service = CreateService(
            taskSessionStore,
            [new StubRuntimeWorkflowBuilder(workflow, runtimeWorkflow)]);

        service.RegisterAgentIdentity("host", "Host", "runtime://host");
        InitializeStartedWorkflowSession(service, workflow, session.SessionId);

        var kickoffTask = service.KickoffAsync();

        await WaitUntilAsync(
            () => service.Messages.Any(message =>
                message.Role == ChatRole.Assistant &&
                message.IsStreaming &&
                string.Equals(message.Content, "Hello", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3));

        Assert.False(kickoffTask.IsCompleted);

        releaseFinalOutput.SetResult();
        await kickoffTask;

        var assistant = Assert.Single(service.Messages, message => message.Role == ChatRole.Assistant);
        Assert.Equal("Hello world", assistant.Content);
        Assert.False(assistant.IsStreaming);
    }

    [Fact]
    public async Task CancelAsync_DuringKickoff_MarksStreamingWorkflowMessageAsCanceled()
    {
        var workflow = CreateWorkflowDefinition();
        var runtimeWorkflow = new WorkflowBuilder(new CancelableStreamingChatExecutor())
            .WithOutputFrom("runtime://host")
            .Build();
        var taskSessionStore = CreateTaskSessionStore(CreateTaskStorePath());
        var session = await taskSessionStore.CreateSessionAsync("Workflow cancel", null, CancellationToken.None);
        var service = CreateService(
            taskSessionStore,
            [new StubRuntimeWorkflowBuilder(workflow, runtimeWorkflow)]);

        service.RegisterAgentIdentity("host", "Host", "runtime://host");
        InitializeStartedWorkflowSession(service, workflow, session.SessionId);

        var kickoffTask = service.KickoffAsync();

        await WaitUntilAsync(
            () => service.Messages.Any(message =>
                message.Role == ChatRole.Assistant &&
                message.IsStreaming),
            TimeSpan.FromSeconds(3));

        await service.CancelAsync();
        await kickoffTask;

        var assistant = Assert.Single(service.Messages, message => message.Role == ChatRole.Assistant);
        Assert.True(assistant.IsCanceled);
        Assert.False(service.IsAnswering);
    }

    private static OrchestrationWorkflowChatSessionService CreateService(
        TaskSessionStore? taskSessionStore = null,
        IReadOnlyList<IOrchestrationRuntimeWorkflowBuilder>? runtimeWorkflowBuilders = null) =>
        new(
            new LoggerFactory().CreateLogger<OrchestrationWorkflowChatSessionService>(),
            new StubModelCapabilityService(),
            taskSessionStore ?? CreateTaskSessionStore(CreateTaskStorePath()),
            new MarkdownDocumentIntakeService(),
            null!,
            runtimeWorkflowBuilders ?? [],
            new AgenticChatEngineStreamingBridge());

    private static AgentWorkflowDefinition CreateWorkflowDefinition() =>
        new()
        {
            Id = "workflow-stream-test",
            DisplayName = "Workflow Stream Test",
            StartAgentId = "host",
            Execution = new AgentWorkflowExecutionDefinition
            {
                Mode = AgentWorkflowExecutionMode.Interactive
            },
            Agents =
            [
                new AgentWorkflowAgentDefinition
                {
                    Id = "host",
                    Role = "host"
                }
            ]
        };


    private static TaskSessionStore CreateTaskSessionStore(string databasePath)
    {
        var binding = new McpServerSessionBinding();
        binding.Parameters[TaskSessionStore.DatabaseFileParameter] = databasePath;
        return new TaskSessionStore(new McpServerSessionContext(binding));
    }

    private static string CreateTaskStorePath()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "OllamaChat.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        return Path.Combine(tempDirectory, "task-sessions.db");
    }

    private static void InitializeStartedWorkflowSession(
        OrchestrationWorkflowChatSessionService service,
        IOrchestrationWorkflowDefinition workflow,
        string taskSessionId)
    {
        var request = new OrchestrationWorkflowSessionStartRequest
        {
            Workflow = workflow,
            Agents = [],
            Configuration = new AppChatConfiguration("model-a", [])
        };

        var serviceType = typeof(OrchestrationWorkflowChatSessionService);
        serviceType
            .GetField("_parameters", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, request);
        serviceType
            .GetProperty(
                nameof(OrchestrationWorkflowChatSessionService.TaskSessionId),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(service, taskSessionId);
    }

    private static AgentResponseUpdateEvent CreateUpdateEvent(
        string executorId,
        string authorName,
        string text) =>
        new(executorId, CreateUpdate(authorName, text));

    private static AgentResponseUpdate CreateUpdate(string authorName, string text) =>
        new(ChatRole.Assistant, text)
        {
            AuthorName = authorName
        };

    private static WorkflowOutputEvent CreateOutputEvent(
        string executorId,
        string authorName,
        string text) =>
        new(CreateOutputMessage(authorName, text), executorId);

    private static WorkflowOutputEvent CreateOutputListEvent(
        string executorId,
        params ChatMessage[] messages) =>
        new(messages.ToList(), executorId);

    private static ChatMessage CreateOutputMessage(string authorName, string text) =>
        new(ChatRole.Assistant, text)
        {
            AuthorName = authorName
        };

    private static async IAsyncEnumerable<WorkflowEvent> StreamEvents(
        IReadOnlyList<WorkflowEvent> workflowEvents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var workflowEvent in workflowEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return workflowEvent;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - startedAt > timeout)
            {
                throw new TimeoutException("Condition was not reached in time.");
            }

            await Task.Delay(15);
        }
    }
}
