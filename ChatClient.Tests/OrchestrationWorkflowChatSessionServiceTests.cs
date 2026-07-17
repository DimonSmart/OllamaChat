using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class OrchestrationWorkflowChatSessionServiceTests
{
    [Fact]
    public async Task SendAsync_ProjectsHeadlessStreamAndCompletionIntoSingleAssistantMessage()
    {
        var service = CreateService(new StubHeadlessWorkflowRunner([
            new HeadlessWorkflowStarted("task-1"),
            new HeadlessWorkflowTextDelta("m1", "Host", "partial"),
            new HeadlessWorkflowMessageCompleted("m1", "host", "Host", "final answer"),
            new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = "m1",
                FinalAuthor = "Host",
                FinalContent = "final answer",
                Messages = [new HeadlessWorkflowOutputMessage("m1", "host", "Host", "final answer")]
            })
        ]));
        await service.StartAsync(CreateStartRequest());

        await service.SendAsync("go");

        Assert.Equal("task-1", service.TaskSessionId);
        var assistant = Assert.Single(service.Messages, message => message.Role == AppChatRole.Assistant);
        Assert.Equal("final answer", assistant.Content);
        Assert.False(assistant.IsStreaming);
        Assert.Equal("host", assistant.AgentId);
        Assert.Equal("Host", assistant.AgentName);
    }

    [Fact]
    public async Task KickoffAsync_DoesNotAddUserMessageAndUsesHeadlessRunner()
    {
        var runner = new StubHeadlessWorkflowRunner([
            new HeadlessWorkflowStarted("task-1"),
            new HeadlessWorkflowMessageCompleted("m1", "host", "Host", "hello"),
            new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = "m1",
                FinalAuthor = "Host",
                FinalContent = "hello",
                Messages = [new HeadlessWorkflowOutputMessage("m1", "host", "Host", "hello")]
            })
        ]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());

        await service.KickoffAsync();

        Assert.Null(runner.LastTurnRequest!.UserMessage);
        Assert.DoesNotContain(service.Messages, message => message.Role == AppChatRole.User);
        Assert.Single(service.Messages, message => message.Role == AppChatRole.Assistant);
    }

    [Fact]
    public async Task SendAsync_ForwardsFilesToHeadlessRunner()
    {
        var runner = new StubHeadlessWorkflowRunner([
            new HeadlessWorkflowStarted("task-1"),
            new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = "final",
                FinalAuthor = "Workflow",
                FinalContent = "done"
            })
        ]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());
        var file = new AppChatMessageFile("notes.md", 5, "text/markdown", [1, 2, 3, 4, 5]);

        await service.SendAsync("go", [file]);

        Assert.Same(file, Assert.Single(runner.LastTurnRequest!.UserFiles));
    }

    [Fact]
    public async Task CancelAsync_CancelsActiveStreamsWithoutGenericError()
    {
        var runner = new BlockingHeadlessWorkflowRunner();
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());

        var sendTask = service.SendAsync("go");
        await runner.WaitUntilStreamingAsync();

        await service.CancelAsync();
        await sendTask;

        var assistant = Assert.Single(service.Messages, message => message.Role == AppChatRole.Assistant);
        Assert.True(assistant.IsCanceled);
        Assert.False(assistant.IsStreaming);
        Assert.DoesNotContain(service.Messages, message => message.Content.StartsWith("Workflow runtime error:", StringComparison.Ordinal));
        Assert.False(service.IsAnswering);
    }

    [Fact]
    public async Task SendAsync_FailureCancelsStreamsAndAddsOneErrorMessage()
    {
        var service = CreateService(new FailingHeadlessWorkflowRunner());
        await service.StartAsync(CreateStartRequest());

        await service.SendAsync("go");

        var assistants = service.Messages.Where(message => message.Role == AppChatRole.Assistant).ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Contains(assistants, message => message.IsCanceled);
        Assert.Single(assistants, message => message.Content.StartsWith("Workflow runtime error:", StringComparison.Ordinal));
        Assert.False(service.IsAnswering);
    }

    [Fact]
    public async Task SendAsync_UsesOneHeadlessSessionForMultipleTurns()
    {
        var runner = new StubHeadlessWorkflowRunner([]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());
        var taskSessionId = service.TaskSessionId;

        await service.SendAsync("first");
        await service.SendAsync("second");

        Assert.Equal(1, runner.StartCount);
        Assert.Equal(2, runner.RunTurnCount);
        Assert.Equal(taskSessionId, service.TaskSessionId);
        Assert.Equal(
            ["first", "second"],
            runner.TurnRequests.Select(static request => request.UserMessage ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task KickoffAsync_AndSendAsync_UseSameHeadlessSession()
    {
        var runner = new StubHeadlessWorkflowRunner([]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());
        var taskSessionId = service.TaskSessionId;

        await service.KickoffAsync();
        await service.SendAsync("continue");

        Assert.Equal(1, runner.StartCount);
        Assert.Equal(2, runner.RunTurnCount);
        Assert.Equal(taskSessionId, service.TaskSessionId);
    }

    [Fact]
    public async Task ResetChat_DisposesOldSessionAndCreatesNewSession()
    {
        var runner = new StubHeadlessWorkflowRunner([]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());
        await service.SendAsync("first");
        var firstSession = Assert.Single(runner.Sessions);

        service.ResetChat();
        await service.StartAsync(CreateStartRequest());
        await service.SendAsync("second");

        Assert.True(firstSession.IsDisposed);
        Assert.Equal(2, runner.StartCount);
        Assert.NotEqual(runner.Sessions[0].TaskSessionId, runner.Sessions[1].TaskSessionId);
    }

    [Fact]
    public async Task SendAsync_PreservesHeadlessSessionStateBetweenTurns()
    {
        var runner = new StubHeadlessWorkflowRunner([]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest());

        await service.SendAsync("first");
        await service.SendAsync("second");

        var session = Assert.Single(runner.Sessions);
        Assert.Equal(2, session.TurnCounter);
        Assert.Equal([1, 2], session.ObservedTurnCounters);
    }

    [Theory]
    [InlineData(RunStatus.NotStarted, 0, true)]
    [InlineData(RunStatus.Idle, 0, true)]
    [InlineData(RunStatus.Running, 0, true)]
    [InlineData(RunStatus.Idle, 1, false)]
    [InlineData(RunStatus.Ended, 0, false)]
    [InlineData(RunStatus.Ended, 2, false)]
    public void ShouldSendExplicitTurnToken_MatchesConversationBatchOutcome(
        RunStatus statusAfterConversationBatch,
        int completedAssistantMessagesFromConversationBatch,
        bool expected)
    {
        var shouldSend = OrchestrationWorkflowPassExecutor.ShouldSendExplicitTurnToken(
            statusAfterConversationBatch,
            completedAssistantMessagesFromConversationBatch);

        Assert.Equal(expected, shouldSend);
    }

    private static OrchestrationWorkflowChatSessionService CreateService(IHeadlessWorkflowRunner runner) =>
        new(
            runner,
            new AgenticChatEngineStreamingBridge(),
            NullLogger<OrchestrationWorkflowChatSessionService>.Instance);

    private static OrchestrationWorkflowSessionStartRequest CreateStartRequest() =>
        new()
        {
            Workflow = new AgentWorkflowDefinition
            {
                Id = "workflow",
                DisplayName = "Workflow",
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
            },
            Agents = [],
            Configuration = new AppChatConfiguration("model", [])
        };

    private sealed class StubHeadlessWorkflowRunner(IReadOnlyList<HeadlessWorkflowEvent> events) : IHeadlessWorkflowRunner
    {
        public HeadlessWorkflowSessionStartRequest? LastStartRequest { get; private set; }

        public HeadlessWorkflowTurnRequest? LastTurnRequest { get; private set; }

        public List<HeadlessWorkflowTurnRequest> TurnRequests { get; } = [];

        public List<StubHeadlessWorkflowSession> Sessions { get; } = [];

        public int StartCount { get; private set; }

        public int RunTurnCount => Sessions.Sum(static session => session.RunTurnCount);

        public Task<IHeadlessWorkflowSession> StartAsync(
            HeadlessWorkflowSessionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            LastStartRequest = request;
            StartCount++;
            var session = new StubHeadlessWorkflowSession(
                "task-" + StartCount.ToString(CultureInfo.InvariantCulture),
                events,
                turnRequest =>
                {
                    LastTurnRequest = turnRequest;
                    TurnRequests.Add(turnRequest);
                });
            Sessions.Add(session);
            return Task.FromResult<IHeadlessWorkflowSession>(session);
        }
    }

    private sealed class StubHeadlessWorkflowSession(
        string taskSessionId,
        IReadOnlyList<HeadlessWorkflowEvent> events,
        Action<HeadlessWorkflowTurnRequest> captureTurnRequest) : IHeadlessWorkflowSession
    {
        public string TaskSessionId { get; } = taskSessionId;

        public int RunTurnCount { get; private set; }

        public int TurnCounter { get; private set; }

        public List<int> ObservedTurnCounters { get; } = [];

        public bool IsDisposed { get; private set; }

        public async IAsyncEnumerable<HeadlessWorkflowEvent> RunTurnAsync(
            HeadlessWorkflowTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            captureTurnRequest(request);
            RunTurnCount++;
            TurnCounter++;
            ObservedTurnCounters.Add(TurnCounter);
            var turnEvents = events.Count == 0
                ?
                [
                    new HeadlessWorkflowMessageCompleted(
                        "m" + TurnCounter.ToString(CultureInfo.InvariantCulture),
                        "host",
                        "Host",
                        "turn " + TurnCounter.ToString(CultureInfo.InvariantCulture)),
                    new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
                    {
                        FinalMessageId = "m" + TurnCounter.ToString(CultureInfo.InvariantCulture),
                        FinalAuthor = "Host",
                        FinalContent = "turn " + TurnCounter.ToString(CultureInfo.InvariantCulture),
                        Messages =
                        [
                            new HeadlessWorkflowOutputMessage(
                                "m" + TurnCounter.ToString(CultureInfo.InvariantCulture),
                                "host",
                                "Host",
                                "turn " + TurnCounter.ToString(CultureInfo.InvariantCulture))
                        ]
                    })
                ]
                : events;

            foreach (var runEvent in turnEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return runEvent;
            }
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingHeadlessWorkflowRunner : IHeadlessWorkflowRunner
    {
        private readonly TaskCompletionSource _streaming =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStreamingAsync() => _streaming.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public Task<IHeadlessWorkflowSession> StartAsync(
            HeadlessWorkflowSessionStartRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IHeadlessWorkflowSession>(new BlockingHeadlessWorkflowSession(_streaming));
    }

    private sealed class FailingHeadlessWorkflowRunner : IHeadlessWorkflowRunner
    {
        public Task<IHeadlessWorkflowSession> StartAsync(
            HeadlessWorkflowSessionStartRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IHeadlessWorkflowSession>(new FailingHeadlessWorkflowSession());
    }

    private sealed class BlockingHeadlessWorkflowSession(TaskCompletionSource streaming) : IHeadlessWorkflowSession
    {
        public string TaskSessionId => "task-1";

        public async IAsyncEnumerable<HeadlessWorkflowEvent> RunTurnAsync(
            HeadlessWorkflowTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new HeadlessWorkflowStarted("task-1");
            yield return new HeadlessWorkflowTextDelta("m1", "Host", "hello");
            streaming.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingHeadlessWorkflowSession : IHeadlessWorkflowSession
    {
        public string TaskSessionId => "task-1";

        public async IAsyncEnumerable<HeadlessWorkflowEvent> RunTurnAsync(
            HeadlessWorkflowTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new HeadlessWorkflowStarted("task-1");
            yield return new HeadlessWorkflowTextDelta("m1", "Host", "hello");
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
