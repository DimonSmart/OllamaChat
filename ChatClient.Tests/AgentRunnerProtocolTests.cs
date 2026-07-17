using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
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
    public async Task RunAsync_ThrowsProtocolExceptionForTerminalViolations(
        IReadOnlyList<AgentRunEvent> runtimeEvents)
    {
        var runner = CreateRunner(new StubRuntime(runtimeEvents));

        await Assert.ThrowsAsync<AgentRuntimeProtocolException>(() =>
            CollectAsync(runner.RunAsync(
                CreateReference(),
                CreateRequest(),
                CreateCreationContext(),
                CreateRunContext())));
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
    public async Task RunAsync_MapsRuntimeCreationNotFoundToStableCode()
    {
        var runner = new AgentRunner(
            new ThrowingRuntimeFactory(new KeyNotFoundException("internal path")),
            NullLogger<AgentRunner>.Instance);

        var events = await CollectAsync(runner.RunAsync(
            new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "missing"),
            CreateRequest(),
            CreateCreationContext(),
            CreateRunContext()));

        var failed = Assert.IsType<AgentRunFailed>(Assert.Single(events));
        Assert.Equal("workflow_not_found", failed.Error.Code);
        Assert.Equal("Saved workflow was not found.", failed.Error.Message);
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

    private static AgentRunner CreateRunner(IAgentRuntime runtime) =>
        new(new StubRuntimeFactory(runtime), NullLogger<AgentRunner>.Instance);

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
        new()
        {
            RunId = Guid.NewGuid().ToString("N")
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
}
