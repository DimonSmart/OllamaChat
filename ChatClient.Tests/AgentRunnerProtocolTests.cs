using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class AgentRunnerProtocolTests
{
    [Fact]
    public async Task RunAsync_AllowsSingleCompletion()
    {
        var events = await CollectAsync(CreateRunner(new StubRuntime([
            new AgentRunCompleted(CreateResult("final", "m1"))
        ])).RunAsync(CreateReference(), CreateRequest(), CreateCreationContext(), CreateRunContext()));

        Assert.Single(events);
        Assert.IsType<AgentRunCompleted>(events[0]);
    }

    [Fact]
    public async Task RunAsync_AllowsSingleFailure()
    {
        var events = await CollectAsync(CreateRunner(new StubRuntime([
            new AgentRunFailed(new AgentRunError("execution_failed", "failed", true))
        ])).RunAsync(CreateReference(), CreateRequest(), CreateCreationContext(), CreateRunContext()));

        Assert.Single(events);
        Assert.IsType<AgentRunFailed>(events[0]);
    }

    [Theory]
    [MemberData(nameof(ProtocolViolationSequences))]
    public async Task RunAsync_ReturnsProtocolFailureForTerminalViolations(
        IReadOnlyList<AgentRunEvent> runtimeEvents)
    {
        var runner = CreateRunner(new StubRuntime(runtimeEvents));

        var events = await CollectAsync(runner.RunAsync(
            CreateReference(),
            CreateRequest(),
            CreateCreationContext(),
            CreateRunContext()));

        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("runtime_protocol_violation", failed.Error.Code);
        Assert.False(failed.Error.IsRetryable);
        Assert.IsType<AgentRuntimeProtocolException>(failed.Error.Exception);
    }

    [Fact]
    public async Task ProtocolExecutor_ReturnsEventsWhenRuntimeCompletesOnce()
    {
        var executor = CreateProtocolExecutor();
        var runtime = new StubRuntime([
            new AgentTextDelta("m1", "assistant", "hello"),
            new AgentRunCompleted(CreateResult("final", "m1"))
        ]);

        var events = await CollectAsync(executor.ExecuteAsync(
            runtime,
            CreateRequest(),
            CreateRunContext()));

        Assert.Equal(2, events.Count);
        Assert.IsType<AgentTextDelta>(events[0]);
        Assert.IsType<AgentRunCompleted>(events[1]);
    }

    [Fact]
    public async Task ProtocolExecutor_ReturnsFailureAsLastEvent()
    {
        var executor = CreateProtocolExecutor();
        var runtime = new StubRuntime([
            new AgentTextDelta("m1", "assistant", "hello"),
            new AgentRunFailed(new AgentRunError("execution_failed", "failed", true))
        ]);

        var events = await CollectAsync(executor.ExecuteAsync(
            runtime,
            CreateRequest(),
            CreateRunContext()));

        Assert.Equal(2, events.Count);
        Assert.IsType<AgentTextDelta>(events[0]);
        Assert.IsType<AgentRunFailed>(events[1]);
    }

    [Theory]
    [MemberData(nameof(ProtocolViolationSequences))]
    public async Task ProtocolExecutor_ReturnsProtocolFailureForTerminalViolations(
        IReadOnlyList<AgentRunEvent> runtimeEvents)
    {
        var executor = CreateProtocolExecutor();

        var events = await CollectAsync(executor.ExecuteAsync(
            new StubRuntime(runtimeEvents),
            CreateRequest(),
            CreateRunContext()));

        Assert.Equal("runtime_protocol_violation", Assert.IsType<AgentRunFailed>(Assert.Single(events)).Error.Code);
    }

    [Fact]
    public async Task ProtocolExecutor_MapsRuntimeExceptionToFailure()
    {
        var exception = new InvalidOperationException("runtime failed");
        var executor = CreateProtocolExecutor();

        var events = await CollectAsync(executor.ExecuteAsync(
            new ThrowingRuntime(exception),
            CreateRequest(),
            CreateRunContext()));

        var failure = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("runtime_execution_failed", failure.Error.Code);
        Assert.Same(exception, failure.Error.Exception);
    }

    [Fact]
    public async Task RunAsync_DoesNotEmitCompletionWhenRuntimeEmitsEventAfterCompletion()
    {
        var runner = CreateRunner(new StubRuntime([
            new AgentRunCompleted(CreateResult("final", "m1")),
            new AgentTextDelta("m2", "assistant", "late")
        ]));
        var events = new List<AgentRunEvent>();

        await foreach (var runEvent in runner.RunAsync(
                           CreateReference(),
                           CreateRequest(),
                           CreateCreationContext(),
                           CreateRunContext()))
        {
            events.Add(runEvent);
        }

        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("runtime_protocol_violation", failed.Error.Code);
    }

    [Fact]
    public async Task RunAsync_DoesNotEmitFailureWhenRuntimeEmitsEventAfterFailure()
    {
        var runner = CreateRunner(new StubRuntime([
            new AgentRunFailed(new AgentRunError("execution_failed", "failed", true)),
            new AgentTextDelta("m2", "assistant", "late")
        ]));
        var events = new List<AgentRunEvent>();

        await foreach (var runEvent in runner.RunAsync(
                           CreateReference(),
                           CreateRequest(),
                           CreateCreationContext(),
                           CreateRunContext()))
        {
            events.Add(runEvent);
        }

        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("runtime_protocol_violation", failed.Error.Code);
    }

    [Fact]
    public async Task RunAsync_ConvertsUnexpectedRuntimeExceptionToFailure()
    {
        var exception = new InvalidOperationException("internal detail");
        var runner = CreateRunner(new ThrowingRuntime(exception));

        var events = await CollectAsync(runner.RunAsync(
            CreateReference(),
            CreateRequest(),
            CreateCreationContext(),
            CreateRunContext()));

        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("runtime_execution_failed", failed.Error.Code);
        Assert.Equal("Agent runtime execution failed.", failed.Error.Message);
        Assert.Same(exception, failed.Error.Exception);
    }

    [Fact]
    public async Task RunAsync_PreservesAgentRunFailedExceptionError()
    {
        var exception = new InvalidOperationException("source");
        var error = new AgentRunError("model_resolution_failed", "Could not resolve model.", true, exception)
        {
            Metadata = new Dictionary<string, string> { ["model"] = "missing" }
        };
        var runner = CreateRunner(new ThrowingRuntime(new AgentRunFailedException(error)));

        var events = await CollectAsync(runner.RunAsync(
            CreateReference(),
            CreateRequest(),
            CreateCreationContext(),
            CreateRunContext()));

        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Same(error, failed.Error);
    }

    [Fact]
    public async Task RunAsync_PreservesDeltasBeforeUnexpectedRuntimeException()
    {
        var runner = CreateRunner(new ThrowingRuntime(
            new InvalidOperationException("boom"),
            [new AgentTextDelta("m1", "assistant", "partial")]));

        var events = await CollectAsync(runner.RunAsync(
            CreateReference(),
            CreateRequest(),
            CreateCreationContext(),
            CreateRunContext()));

        Assert.IsType<AgentTextDelta>(events[0]);
        var failed = Assert.IsType<AgentRunFailed>(events[1]);
        Assert.Equal("runtime_execution_failed", failed.Error.Code);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var runner = CreateRunner(new CancelingRuntime(cts));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CollectAsync(runner.RunAsync(
                CreateReference(),
                CreateRequest(),
                CreateCreationContext(),
                CreateRunContext(),
                cts.Token)));
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellationAfterPendingTerminal()
    {
        using var cts = new CancellationTokenSource();
        var runner = CreateRunner(new CancelingAfterTerminalRuntime(cts));
        var events = new List<AgentRunEvent>();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var runEvent in runner.RunAsync(
                               CreateReference(),
                               CreateRequest(),
                               CreateCreationContext(),
                               CreateRunContext(),
                               cts.Token))
            {
                events.Add(runEvent);
            }
        });

        Assert.Empty(events);
    }

    [Fact]
    public async Task RunAsync_MapsRuntimeCreationNotFoundToStableCode()
    {
        var runner = CreateRunner(new ThrowingRuntimeFactory(new KeyNotFoundException("internal path")));

        var events = await CollectAsync(runner.RunAsync(
            new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "missing"),
            CreateRequest(),
            CreateCreationContext(),
            CreateRunContext(new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "missing"))));

        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("workflow_not_found", failed.Error.Code);
        Assert.Equal("Saved workflow was not found.", failed.Error.Message);
    }

    [Theory]
    [InlineData("completed", "Completed", null, null)]
    [InlineData("failed", "Failed", "execution_failed", true)]
    [InlineData("protocol", "ProtocolViolation", "runtime_protocol_violation", false)]
    public async Task RunAsync_LogsActualTerminalOutcome(
        string scenario,
        string expectedOutcome,
        string? expectedFailureCode,
        bool? expectedRetryable)
    {
        var events = scenario switch
        {
            "completed" => new AgentRunEvent[] { new AgentRunCompleted(CreateResult("final", "m1")) },
            "failed" => new AgentRunEvent[] { new AgentRunFailed(new AgentRunError("execution_failed", "failed", true)) },
            _ => new AgentRunEvent[] { new AgentRunFailed(new AgentRunError("runtime_protocol_violation", "protocol", false)) }
        };
        var logger = new CapturingLogger<AgentRunner>();

        await CollectAsync(CreateRunner(new StubRuntime(events), logger).RunAsync(
            CreateReference(), CreateRequest(), CreateCreationContext(), CreateRunContext()));

        var properties = logger.Entries.Single(static entry => entry.Message.Contains("Agent run finished.", StringComparison.Ordinal)).Properties;
        Assert.Equal(expectedOutcome, properties["Outcome"]);
        Assert.Equal(expectedFailureCode, properties.GetValueOrDefault("FailureCode"));
        Assert.Equal(expectedRetryable, properties.GetValueOrDefault("FailureRetryable"));
    }

    [Fact]
    public async Task RunAsync_LogsCanceledOutcome()
    {
        using var cts = new CancellationTokenSource();
        var canceledLogger = new CapturingLogger<AgentRunner>();
        var canceledRunner = CreateRunner(new CancelingRuntime(cts), canceledLogger);
        await Assert.ThrowsAsync<OperationCanceledException>(() => CollectAsync(canceledRunner.RunAsync(
            CreateReference(), CreateRequest(), CreateCreationContext(), CreateRunContext(), cts.Token)));
        Assert.Equal("Canceled", canceledLogger.FinishedProperties()["Outcome"]);
        Assert.Null(canceledLogger.FinishedProperties().GetValueOrDefault("FailureCode"));
    }

    [Fact]
    public async Task RunAsync_LogsAbandonedOutcomeWhenConsumerStopsBeforeTerminalEvent()
    {
        var abandonedLogger = new CapturingLogger<AgentRunner>();
        var abandonedRunner = CreateRunner(new StubRuntime([
            new AgentTextDelta("m1", "assistant", "partial"),
            new AgentRunCompleted(CreateResult("final", "m1"))
        ]), abandonedLogger);
        await using (var enumerator = abandonedRunner.RunAsync(
                         CreateReference(), CreateRequest(), CreateCreationContext(), CreateRunContext()).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
        }

        var properties = abandonedLogger.FinishedProperties();
        Assert.Equal("Abandoned", properties["Outcome"]);
        Assert.Null(properties.GetValueOrDefault("FailureCode"));
    }

    [Fact]
    public async Task RunAsync_LogsRuntimeCreationFailureOutcome()
    {
        var logger = new CapturingLogger<AgentRunner>();
        var runner = CreateRunner(new ThrowingRuntimeFactory(new InvalidOperationException("factory failed")), logger);

        var events = await CollectAsync(runner.RunAsync(
            CreateReference(), CreateRequest(), CreateCreationContext(), CreateRunContext()));

        var failure = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("runtime_creation_failed", failure.Error.Code);
        var properties = logger.FinishedProperties();
        Assert.Equal("Failed", properties["Outcome"]);
        Assert.Equal("runtime_creation_failed", properties["FailureCode"]);
        Assert.Equal(false, properties["FailureRetryable"]);
    }

    public static IEnumerable<object[]> ProtocolViolationSequences()
    {
        yield return
        [
            new AgentRunEvent[]
            {
                new AgentRunCompleted(CreateResult("first", "m1")),
                new AgentRunFailed(new AgentRunError("execution_failed", "second", true))
            }
        ];
        yield return
        [
            new AgentRunEvent[]
            {
                new AgentRunFailed(new AgentRunError("execution_failed", "first", true)),
                new AgentRunCompleted(CreateResult("second", "m2"))
            }
        ];
        yield return
        [
            new AgentRunEvent[]
            {
                new AgentRunCompleted(CreateResult("first", "m1")),
                new AgentTextDelta("m2", "assistant", "late")
            }
        ];
        yield return [Array.Empty<AgentRunEvent>()];
    }

    private static AgentRunner CreateRunner(IAgentRuntime runtime, ILogger<AgentRunner>? logger = null) =>
        CreateRunner(new StubRuntimeFactory(runtime), logger);

    private static AgentRunner CreateRunner(IAgentRuntimeFactory runtimeFactory, ILogger<AgentRunner>? logger = null) =>
        new(
            new StubDefinitionCatalog(),
            new AgentRunNestingValidator(new AgentRuntimeOptions()),
            runtimeFactory,
            CreateProtocolExecutor(),
            logger ?? NullLogger<AgentRunner>.Instance);

    private static AgentRuntimeProtocolExecutor CreateProtocolExecutor() =>
        new(NullLogger<AgentRuntimeProtocolExecutor>.Instance);

    private static AgentDefinitionReference CreateReference() =>
        new(AgentDefinitionKind.SavedAgent, "agent");

    private static AgentRuntimeRunRequest CreateRequest() =>
        new()
        {
            Messages =
            [
                new AgentInputMessage(AgentMessageRole.User, "hello")
            ]
        };

    private static AgentRuntimeCreationContext CreateCreationContext() =>
        new()
        {
            Configuration = new AppChatConfiguration("model", [])
        };

    private static AgentRunContext CreateRunContext() =>
        CreateRunContext(CreateReference());

    private static AgentRunContext CreateRunContext(AgentDefinitionReference reference) =>
        new()
        {
            RunId = Guid.NewGuid().ToString("N"),
            DefinitionStack =
            [
                new AgentRunFrame
                {
                    Definition = reference,
                    DisplayName = reference.Id
                }
            ]
        };

    private static AgentRunResult CreateResult(string content, string messageId)
    {
        var message = new AgentOutputMessage("assistant", content);
        return new AgentRunResult
        {
            FinalMessage = message,
            FinalMessageId = messageId,
            Messages = [message]
        };
    }

    private static async Task<List<AgentRunEvent>> CollectAsync(
        IAsyncEnumerable<AgentRunEvent> events)
    {
        var result = new List<AgentRunEvent>();
        await foreach (var runEvent in events)
        {
            result.Add(runEvent);
        }

        return result;
    }

    private sealed class StubRuntimeFactory(IAgentRuntime runtime) : IAgentRuntimeFactory
    {
        public Task<IAgentRuntime> CreateAsync(
            AgentDefinitionReference reference,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(runtime);
    }

    private sealed class StubDefinitionCatalog : IAgentDefinitionCatalog
    {
        public Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentDefinitionDescriptor>>([]);

        public Task<AgentDefinitionDescriptor?> FindAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentDefinitionDescriptor?>(new AgentDefinitionDescriptor
            {
                Reference = reference,
                Name = reference.Id,
                RuntimeKind = reference.Kind == AgentDefinitionKind.SavedWorkflow
                    ? AgentRuntimeKind.WorkflowAgent
                    : AgentRuntimeKind.LlmAgent,
                ModelRequirement = AgentModelRequirement.Required
            });

        public async Task<AgentDefinitionDescriptor> GetRequiredAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            await FindAsync(reference, cancellationToken) ?? throw new KeyNotFoundException();
    }

    private sealed class ThrowingRuntimeFactory(Exception exception) : IAgentRuntimeFactory
    {
        public Task<IAgentRuntime> CreateAsync(
            AgentDefinitionReference reference,
            AgentRuntimeCreationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IAgentRuntime>(exception);
    }

    private sealed class StubRuntime(IReadOnlyList<AgentRunEvent> events) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } =
            new("runtime", "Runtime", string.Empty, AgentRuntimeKind.LlmAgent);

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var runEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return runEvent;
            }
        }
    }

    private sealed class ThrowingRuntime(
        Exception exception,
        IReadOnlyList<AgentRunEvent>? eventsBeforeException = null) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } =
            new("runtime", "Runtime", string.Empty, AgentRuntimeKind.LlmAgent);

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var runEvent in eventsBeforeException ?? [])
            {
                await Task.Yield();
                yield return runEvent;
            }

            await Task.Yield();
            throw exception;
        }
    }

    private sealed class CancelingRuntime(CancellationTokenSource cancellationTokenSource) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } =
            new("runtime", "Runtime", string.Empty, AgentRuntimeKind.LlmAgent);

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationTokenSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class CancelingAfterTerminalRuntime(CancellationTokenSource cancellationTokenSource) : IAgentRuntime
    {
        public AgentRuntimeDescriptor Descriptor { get; } =
            new("runtime", "Runtime", string.Empty, AgentRuntimeKind.LlmAgent);

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentRuntimeRunRequest request,
            AgentRunContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AgentRunCompleted(CreateResult("final", "m1"));
            cancellationTokenSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IReadOnlyDictionary<string, object?> FinishedProperties() =>
            Entries.Single(static entry => entry.Message.Contains("Agent run finished.", StringComparison.Ordinal)).Properties;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(static pair => pair.Key, static pair => pair.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new LogEntry(formatter(state, exception), properties));
        }
    }

    private sealed record LogEntry(string Message, IReadOnlyDictionary<string, object?> Properties);
}
