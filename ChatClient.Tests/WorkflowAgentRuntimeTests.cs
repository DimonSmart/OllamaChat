using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class WorkflowAgentRuntimeTests
{
    [Fact]
    public async Task RunAsync_SequentialSavedParticipants_RunThroughAgentRunnerAndPassFinalMessage()
    {
        var runner = new RecordingAgentRunner(new Dictionary<string, string>
        {
            ["SavedAgent:a"] = "first",
            ["SavedWorkflow:w"] = "nested",
            ["SavedAgent:b"] = "final"
        });
        var runtime = CreateSequentialRuntime(
            runner,
            [
                new ResolvedWorkflowParticipant
                {
                    ParticipantId = "a",
                    DisplayName = "A",
                    Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "a"),
                    RuntimeKind = AgentRuntimeKind.LlmAgent
                },
                new ResolvedWorkflowParticipant
                {
                    ParticipantId = "w",
                    DisplayName = "W",
                    Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "w"),
                    RuntimeKind = AgentRuntimeKind.WorkflowAgent
                },
                new ResolvedWorkflowParticipant
                {
                    ParticipantId = "b",
                    DisplayName = "B",
                    Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "b"),
                    RuntimeKind = AgentRuntimeKind.LlmAgent
                }
            ],
            ["a", "w", "b"]);
        var parentContext = CreateContext();

        var events = await CollectAsync(runtime.RunAsync(CreateRequest(), parentContext));

        Assert.Equal(
            [
                new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "a"),
                new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "w"),
                new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "b")
            ],
            runner.Calls.Select(static call => call.Reference).ToList());
        Assert.Contains("first", runner.Calls[1].Request.Messages.Last().Content);
        Assert.Contains("nested", runner.Calls[2].Request.Messages.Last().Content);
        Assert.All(runner.Calls, call => Assert.Equal(parentContext.RunId, call.Context.ParentRunId));
        Assert.Contains(runner.Calls[1].Context.DefinitionPath, reference =>
            reference.Kind == AgentDefinitionKind.SavedWorkflow &&
            reference.Id == "w");

        Assert.Single(events.OfType<AgentRunCompleted>());
        var completed = Assert.IsType<AgentRunCompleted>(events.Last());
        Assert.Equal("final", completed.Result.FinalMessage.Content);
        Assert.Equal("Workflow", completed.Result.FinalMessage.Author);
    }

    [Fact]
    public async Task RunAsync_SequentialSavedWorkflowParticipant_DetectsCycleBeforeRunningParticipant()
    {
        var runner = new RecordingAgentRunner(new Dictionary<string, string>());
        var runtime = CreateSequentialRuntime(
            runner,
            [
                new ResolvedWorkflowParticipant
                {
                    ParticipantId = "self",
                    DisplayName = "Self",
                    Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "workflow"),
                    RuntimeKind = AgentRuntimeKind.WorkflowAgent
                }
            ],
            ["self"]);

        var events = await CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext()));

        Assert.Empty(runner.Calls);
        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("workflow_cycle_detected", failed.Error.Code);
        Assert.False(failed.Error.IsRetryable);
    }

    [Fact]
    public async Task RunAsync_SequentialWorkflow_ReturnsNonRetryableFailureWhenNestingLimitExceeded()
    {
        var runner = new RecordingAgentRunner(new Dictionary<string, string>());
        var runtime = CreateSequentialRuntime(
            runner,
            [
                new ResolvedWorkflowParticipant
                {
                    ParticipantId = "next",
                    DisplayName = "Next",
                    Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "next"),
                    RuntimeKind = AgentRuntimeKind.LlmAgent
                }
            ],
            ["next"]);
        var context = CreateContext() with
        {
            DefinitionPath = Enumerable.Range(0, 8)
                .Select(index => new AgentDefinitionReference(
                    AgentDefinitionKind.SavedWorkflow,
                    $"workflow-{index}"))
                .ToList()
        };

        var events = await CollectAsync(runtime.RunAsync(CreateRequest(), context));

        Assert.Empty(runner.Calls);
        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("workflow_nesting_limit_exceeded", failed.Error.Code);
        Assert.False(failed.Error.IsRetryable);
    }

    [Fact]
    public async Task RunAsync_MapsHeadlessEventsToUnifiedEvents()
    {
        var runtime = CreateRuntime(new StubHeadlessWorkflowRunner([
            new HeadlessWorkflowStarted("session-1"),
            new HeadlessWorkflowTextDelta("m1", "Writer", "draft"),
            new HeadlessWorkflowMessageCompleted("m1", "writer", "Writer", "draft"),
            new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = "final",
                FinalAuthor = "Workflow",
                FinalContent = "summary",
                Messages = [new HeadlessWorkflowOutputMessage("m1", "writer", "Writer", "draft")],
                Metadata = new Dictionary<string, string> { ["workflowKind"] = "handoff" }
            })
        ]));

        var events = await CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext()));

        var delta = Assert.IsType<AgentTextDelta>(events[0]);
        Assert.Equal("m1", delta.MessageId);
        Assert.Equal("Writer", delta.Author);
        Assert.Equal("draft", delta.Text);
        var message = Assert.IsType<AgentMessageCompleted>(events[1]);
        Assert.Equal("m1", message.MessageId);
        Assert.Equal(new AgentOutputMessage("Writer", "draft"), message.Message);
        var completed = Assert.IsType<AgentRunCompleted>(events[2]);
        Assert.Equal("final", completed.Result.FinalMessageId);
        Assert.Equal(new AgentOutputMessage("Workflow", "summary"), completed.Result.FinalMessage);
        Assert.Equal("handoff", completed.Result.Metadata["workflowKind"]);
    }

    [Fact]
    public async Task WorkflowAgentRuntime_HandlesRealHeadlessLifecycle()
    {
        var runtime = CreateRuntime(new StubHeadlessWorkflowRunner([
            new HeadlessWorkflowStarted("session-1"),
            new HeadlessWorkflowTextDelta("m1", "Writer", "draft"),
            new HeadlessWorkflowMessageCompleted("m1", "writer", "Writer", "draft"),
            new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = "m1",
                FinalAuthor = "Workflow",
                FinalContent = "draft",
                Messages = [new HeadlessWorkflowOutputMessage("m1", "writer", "Writer", "draft")]
            })
        ]));

        var events = await CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext()));

        Assert.DoesNotContain(events, static runEvent => runEvent.GetType().Name.Contains("Started", StringComparison.Ordinal));
        Assert.Contains(events, static runEvent => runEvent is AgentTextDelta);
        Assert.Contains(events, static runEvent => runEvent is AgentMessageCompleted);
        Assert.DoesNotContain(events, static runEvent => runEvent is AgentRunFailed);
        Assert.Single(events, static runEvent => runEvent is AgentRunCompleted);
    }

    [Fact]
    public async Task RunAsync_FormatsHistoryAndKeepsLastUserOnlyInCurrentRequest()
    {
        var runner = new StubHeadlessWorkflowRunner([
            new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = "final",
                FinalAuthor = "Workflow",
                FinalContent = "done"
            })
        ]);
        var runtime = CreateRuntime(runner);

        await CollectAsync(runtime.RunAsync(new AgentRuntimeRunRequest
        {
            Messages =
            [
                new AgentInputMessage(AgentMessageRole.System, "system"),
                new AgentInputMessage(AgentMessageRole.User, "user-1"),
                new AgentInputMessage(AgentMessageRole.Assistant, "assistant-1"),
                new AgentInputMessage(AgentMessageRole.User, "user-2")
            ]
        }, CreateContext()));

        Assert.Equal(
            $"Previous conversation:{Environment.NewLine}{Environment.NewLine}" +
            $"System: system{Environment.NewLine}{Environment.NewLine}" +
            $"User: user-1{Environment.NewLine}{Environment.NewLine}" +
            $"Assistant: assistant-1{Environment.NewLine}{Environment.NewLine}" +
            $"Current request:{Environment.NewLine}{Environment.NewLine}" +
            "user-2",
            runner.LastTurnRequest!.UserMessage);
    }

    [Fact]
    public async Task RunAsync_MessagesAfterLastUserProduceOneInvalidInputFailure()
    {
        var events = await CollectAsync(CreateRuntime(new StubHeadlessWorkflowRunner([])).RunAsync(new AgentRuntimeRunRequest
        {
            Messages =
            [
                new AgentInputMessage(AgentMessageRole.User, "go"),
                new AgentInputMessage(AgentMessageRole.Assistant, "late")
            ]
        }, CreateContext()));

        var failure = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("invalid_input", failure.Error.Code);
    }

    [Fact]
    public async Task RunAsync_MapsOneMarkdownAttachmentToRequiredMarkdownInput()
    {
        var runner = new StubHeadlessWorkflowRunner([
            new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = "final",
                FinalAuthor = "Workflow",
                FinalContent = "done"
            })
        ]);
        var runtime = CreateRuntime(runner, [new WorkflowStartInputDefinition
        {
            Key = "document",
            DisplayName = "Document",
            Kind = WorkflowStartInputKind.MarkdownDocument,
            IsRequired = true
        }]);

        await CollectAsync(runtime.RunAsync(new AgentRuntimeRunRequest
        {
            Messages = [new AgentInputMessage(AgentMessageRole.User, "go")],
            Attachments = [new AgentInputAttachment("doc.md", "text/markdown", "# Doc")]
        }, CreateContext()));

        var input = Assert.Single(runner.LastStartRequest!.StartInputs);
        Assert.Equal("document", input.Key);
        Assert.Equal("# Doc", input.Value);
    }

    [Theory]
    [MemberData(nameof(InvalidAttachmentRequests))]
    public async Task RunAsync_InvalidAttachmentMappingProducesInvalidInput(
        AgentRuntimeRunRequest request,
        IReadOnlyList<WorkflowStartInputDefinition> inputs)
    {
        var events = await CollectAsync(CreateRuntime(new StubHeadlessWorkflowRunner([]), inputs)
            .RunAsync(request, CreateContext()));

        var failure = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("invalid_input", failure.Error.Code);
    }

    [Theory]
    [MemberData(nameof(ExceptionMappings))]
    public async Task RunAsync_MapsExceptions(Exception exception, string expectedCode)
    {
        var events = await CollectAsync(CreateRuntime(new ThrowingHeadlessWorkflowRunner(exception))
            .RunAsync(CreateRequest(), CreateContext()));

        var failure = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal(expectedCode, failure.Error.Code);
    }

    [Fact]
    public async Task RunAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var runtime = CreateRuntime(new CancelingHeadlessWorkflowRunner(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext(), cts.Token)));
    }

    [Fact]
    public async Task RunAsync_CreatesNewWorkflowSessionPerInvocation()
    {
        var runner = new StubHeadlessWorkflowRunner([
            new HeadlessWorkflowCompleted(new HeadlessWorkflowResult
            {
                FinalMessageId = "final",
                FinalAuthor = "Workflow",
                FinalContent = "done"
            })
        ]);
        var runtime = CreateRuntime(runner);

        await CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext()));
        await CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext()));

        Assert.Equal(2, runner.StartCount);
    }

    public static IEnumerable<object[]> InvalidAttachmentRequests()
    {
        yield return
        [
            new AgentRuntimeRunRequest
            {
                Messages = [new AgentInputMessage(AgentMessageRole.User, "go")],
                Attachments = [new AgentInputAttachment("doc.bin", "application/octet-stream", "AAAA")]
            },
            new[]
            {
                new WorkflowStartInputDefinition
                {
                    Key = "document",
                    DisplayName = "Document",
                    Kind = WorkflowStartInputKind.MarkdownDocument,
                    IsRequired = true
                }
            }
        ];
        yield return
        [
            new AgentRuntimeRunRequest
            {
                Messages = [new AgentInputMessage(AgentMessageRole.User, "go")],
                Attachments =
                [
                    new AgentInputAttachment("a.md", "text/markdown", "a"),
                    new AgentInputAttachment("b.md", "text/markdown", "b")
                ]
            },
            new[]
            {
                new WorkflowStartInputDefinition
                {
                    Key = "document",
                    DisplayName = "Document",
                    Kind = WorkflowStartInputKind.MarkdownDocument,
                    IsRequired = true
                }
            }
        ];
        yield return
        [
            new AgentRuntimeRunRequest
            {
                Messages = [new AgentInputMessage(AgentMessageRole.User, "go")],
                Inputs = new Dictionary<string, string> { ["document"] = "already" },
                Attachments = [new AgentInputAttachment("a.md", "text/markdown", "a")]
            },
            new[]
            {
                new WorkflowStartInputDefinition
                {
                    Key = "document",
                    DisplayName = "Document",
                    Kind = WorkflowStartInputKind.MarkdownDocument,
                    IsRequired = true
                }
            }
        ];
        yield return
        [
            new AgentRuntimeRunRequest
            {
                Messages = [new AgentInputMessage(AgentMessageRole.User, "go")],
                Attachments = [new AgentInputAttachment("a.md", "text/markdown", "a")]
            },
            Array.Empty<WorkflowStartInputDefinition>()
        ];
    }

    public static IEnumerable<object[]> ExceptionMappings()
    {
        yield return [new WorkflowProducedNoResultException(), "workflow_produced_no_result"];
        yield return [new WorkflowAssistantErrorException("assistant failed"), "execution_failed"];
        yield return [new InvalidOperationException("boom"), "execution_failed"];
    }

    private static WorkflowAgentRuntime CreateRuntime(
        IHeadlessWorkflowRunner runner,
        IReadOnlyList<WorkflowStartInputDefinition>? startInputs = null) =>
        new(
            new AgentRuntimeDescriptor("workflow", "Workflow", "Runs a workflow", AgentRuntimeKind.WorkflowAgent),
            new AgentWorkflowDefinition
            {
                Id = "workflow",
                DisplayName = "Workflow",
                StartAgentId = "agent",
                StartInputs = (startInputs ?? []).ToList(),
                Agents = [new AgentWorkflowAgentDefinition { Id = "agent", Role = "agent" }]
            },
            [new ResolvedChatAgent(new AgentExecutionSpec { Id = Guid.NewGuid(), AgentName = "Agent" }, new ServerModel(Guid.NewGuid(), "model"))],
            new AppChatConfiguration("model", []),
            runner,
            NullLogger<WorkflowAgentRuntime>.Instance);

    private static WorkflowAgentRuntime CreateSequentialRuntime(
        IAgentRunner runner,
        IReadOnlyList<ResolvedWorkflowParticipant> participants,
        IReadOnlyList<string> participantOrder)
    {
        var configuration = new AppChatConfiguration("model", []);
        return new WorkflowAgentRuntime(
            new AgentRuntimeDescriptor("workflow", "Workflow", "Runs a workflow", AgentRuntimeKind.WorkflowAgent),
            new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "workflow"),
            new SequentialWorkflowDefinition
            {
                Id = "workflow",
                DisplayName = "Workflow",
                Participants = participants.Select(static participant => new WorkflowParticipantDefinition
                {
                    Id = participant.ParticipantId,
                    Role = participant.DisplayName,
                    Source = participant.Reference is null
                        ? null
                        : new SavedDefinitionParticipantSource(participant.Reference)
                }).ToList(),
                ParticipantOrder = participantOrder.ToList()
            },
            participants,
            [],
            configuration,
            new AgentRuntimeCreationContext
            {
                Configuration = configuration
            },
            new StubHeadlessWorkflowRunner([]),
            new ThrowingChatEngineOrchestrator(),
            new AgentRunnerServiceProvider(runner),
            NullLogger<WorkflowAgentRuntime>.Instance);
    }

    private static AgentRuntimeRunRequest CreateRequest() =>
        new()
        {
            Messages = [new AgentInputMessage(AgentMessageRole.User, "go")]
        };

    private static AgentRunContext CreateContext() =>
        new() { RunId = Guid.NewGuid().ToString("N") };

    private static async Task<List<AgentRunEvent>> CollectAsync(IAsyncEnumerable<AgentRunEvent> events)
    {
        var result = new List<AgentRunEvent>();
        await foreach (var runEvent in events)
        {
            result.Add(runEvent);
        }

        return result;
    }

    private sealed class StubHeadlessWorkflowRunner(IReadOnlyList<HeadlessWorkflowEvent> events) : IHeadlessWorkflowRunner
    {
        public HeadlessWorkflowSessionStartRequest? LastStartRequest { get; private set; }

        public HeadlessWorkflowTurnRequest? LastTurnRequest { get; private set; }

        public int StartCount { get; private set; }

        public Task<IHeadlessWorkflowSession> StartAsync(
            HeadlessWorkflowSessionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            LastStartRequest = request;
            StartCount++;
            return Task.FromResult<IHeadlessWorkflowSession>(new StubHeadlessWorkflowSession(
                "session-" + StartCount.ToString(CultureInfo.InvariantCulture),
                events,
                turnRequest => LastTurnRequest = turnRequest));
        }
    }

    private sealed class StubHeadlessWorkflowSession(
        string taskSessionId,
        IReadOnlyList<HeadlessWorkflowEvent> events,
        Action<HeadlessWorkflowTurnRequest> captureTurnRequest) : IHeadlessWorkflowSession
    {
        public string TaskSessionId { get; } = taskSessionId;

        public async IAsyncEnumerable<HeadlessWorkflowEvent> RunTurnAsync(
            HeadlessWorkflowTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            captureTurnRequest(request);
            foreach (var runEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return runEvent;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingAgentRunner(
        IReadOnlyDictionary<string, string> results) : IAgentRunner
    {
        public List<Call> Calls { get; } = [];

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentDefinitionReference reference,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AgentRunContext runContext,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(reference, request, runContext));
            await Task.Yield();
            var key = $"{reference.Kind}:{reference.Id}";
            var content = results.TryGetValue(key, out var result)
                ? result
                : key;
            var message = new AgentOutputMessage(reference.Id, content);
            yield return new AgentTextDelta(Guid.NewGuid().ToString("N"), reference.Id, content);
            yield return new AgentMessageCompleted(Guid.NewGuid().ToString("N"), message);
            yield return new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = message,
                FinalMessageId = Guid.NewGuid().ToString("N"),
                Messages = [message]
            });
        }

        public sealed record Call(
            AgentDefinitionReference Reference,
            AgentRuntimeRunRequest Request,
            AgentRunContext Context);
    }

    private sealed class AgentRunnerServiceProvider(IAgentRunner runner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IAgentRunner) ? runner : null;
    }

    private sealed class ThrowingChatEngineOrchestrator : IChatEngineOrchestrator
    {
        public IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
            ChatEngineOrchestrationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingHeadlessWorkflowRunner(Exception exception) : IHeadlessWorkflowRunner
    {
        public Task<IHeadlessWorkflowSession> StartAsync(
            HeadlessWorkflowSessionStartRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IHeadlessWorkflowSession>(new ThrowingHeadlessWorkflowSession(exception));
    }

    private sealed class CancelingHeadlessWorkflowRunner(CancellationTokenSource cancellationTokenSource) : IHeadlessWorkflowRunner
    {
        public Task<IHeadlessWorkflowSession> StartAsync(
            HeadlessWorkflowSessionStartRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IHeadlessWorkflowSession>(new CancelingHeadlessWorkflowSession(cancellationTokenSource));
    }

    private sealed class ThrowingHeadlessWorkflowSession(Exception exception) : IHeadlessWorkflowSession
    {
        public string TaskSessionId => "session-1";

        public async IAsyncEnumerable<HeadlessWorkflowEvent> RunTurnAsync(
            HeadlessWorkflowTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw exception;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelingHeadlessWorkflowSession(CancellationTokenSource cancellationTokenSource) : IHeadlessWorkflowSession
    {
        public string TaskSessionId => "session-1";

        public async IAsyncEnumerable<HeadlessWorkflowEvent> RunTurnAsync(
            HeadlessWorkflowTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationTokenSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
