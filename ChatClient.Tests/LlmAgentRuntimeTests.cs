using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class LlmAgentRuntimeTests
{
    [Fact]
    public async Task RunAsync_SendsPreviousMessagesAsHistoryAndLastUserAsCurrentMessage()
    {
        var orchestrator = new StubOrchestrator([new ChatEngineStreamChunk("Agent", "done", IsFinal: true)]);
        var runtime = CreateRuntime(orchestrator);

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

        Assert.Equal("user-2", orchestrator.LastRequest!.UserMessage);
        Assert.Equal(
            [AppChatRole.System, AppChatRole.User, AppChatRole.Assistant],
            orchestrator.LastRequest.Messages.Select(static message => message.Role).ToArray());
        Assert.Equal(["system", "user-1", "assistant-1"], orchestrator.LastRequest.Messages.Select(static message => message.Content).ToArray());
    }

    [Fact]
    public async Task RunAsync_UsesOneMessageIdForDeltasCompletionAndFinalResult()
    {
        var events = await CollectAsync(CreateRuntime(new StubOrchestrator([
            new ChatEngineStreamChunk("Agent", "hel"),
            new ChatEngineStreamChunk("Agent", "lo", IsFinal: true)
        ])).RunAsync(CreateRequest(), CreateContext()));

        var deltas = events.OfType<AgentTextDelta>().ToList();
        var completed = Assert.Single(events.OfType<AgentMessageCompleted>());
        var terminal = Assert.Single(events.OfType<AgentRunCompleted>());

        Assert.NotEmpty(deltas);
        Assert.All(deltas, delta => Assert.Equal(deltas[0].MessageId, delta.MessageId));
        Assert.Equal(deltas[0].MessageId, completed.MessageId);
        Assert.Equal(deltas[0].MessageId, terminal.Result.FinalMessageId);
        Assert.All(deltas, delta => Assert.Equal("Agent", delta.Author));
        Assert.Equal("Agent", completed.Message.Author);
    }

    [Fact]
    public async Task RunAsync_SuccessEmitsSingleCompletionAndNoEventsAfterTerminal()
    {
        var events = await CollectAsync(CreateRuntime(new StubOrchestrator([
            new ChatEngineStreamChunk("Agent", "done", IsFinal: true)
        ])).RunAsync(CreateRequest(), CreateContext()));

        Assert.Single(events.OfType<AgentRunCompleted>());
        Assert.DoesNotContain(events, static runEvent => runEvent is AgentRunFailed);
        Assert.IsType<AgentRunCompleted>(events.Last());
    }

    [Theory]
    [MemberData(nameof(FailureOrchestrators))]
    public async Task RunAsync_FailuresEmitSingleFailure(IChatEngineOrchestrator orchestrator)
    {
        var events = await CollectAsync(CreateRuntime(orchestrator).RunAsync(CreateRequest(), CreateContext()));

        Assert.Single(events.OfType<AgentRunFailed>());
        Assert.DoesNotContain(events, static runEvent => runEvent is AgentRunCompleted);
        Assert.IsType<AgentRunFailed>(events.Last());
    }

    [Fact]
    public async Task RunAsync_CancellationPropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var runtime = CreateRuntime(new CancelingOrchestrator(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CollectAsync(runtime.RunAsync(CreateRequest(), CreateContext(), cts.Token)));
    }

    [Fact]
    public async Task RunAsync_ForwardsAttachmentsWithoutLosingMetadataOrBytes()
    {
        var textBytes = "hello"u8.ToArray();
        var binaryBytes = new byte[] { 1, 2, 3 };
        var orchestrator = new StubOrchestrator([new ChatEngineStreamChunk("Agent", "done", IsFinal: true)]);
        var runtime = CreateRuntime(orchestrator);

        await CollectAsync(runtime.RunAsync(new AgentRuntimeRunRequest
        {
            Messages = [new AgentInputMessage(AgentMessageRole.User, "go")],
            Attachments =
            [
                new AgentInputAttachment("notes.txt", "text/plain", "hello") { Data = textBytes },
                new AgentInputAttachment("image.bin", "application/octet-stream", Convert.ToBase64String(binaryBytes)) { Data = binaryBytes }
            ]
        }, CreateContext()));

        Assert.Collection(
            orchestrator.LastRequest!.Files,
            file =>
            {
                Assert.Equal("notes.txt", file.Name);
                Assert.Equal("text/plain", file.ContentType);
                Assert.Equal(textBytes, file.Data);
            },
            file =>
            {
                Assert.Equal("image.bin", file.Name);
                Assert.Equal("application/octet-stream", file.ContentType);
                Assert.Equal(binaryBytes, file.Data);
            });
    }

    public static IEnumerable<object[]> FailureOrchestrators()
    {
        yield return [new StubOrchestrator([new ChatEngineStreamChunk("Agent", "", IsError: true)])];
        yield return [new StubOrchestrator([])];
        yield return [new ThrowingOrchestrator()];
    }

    private static LlmAgentRuntime CreateRuntime(IChatEngineOrchestrator orchestrator) =>
        new(
            new AgentRuntimeDescriptor("agent", "Agent", string.Empty, AgentRuntimeKind.LlmAgent),
            new ResolvedChatAgent(
                new AgentExecutionSpec { Id = Guid.NewGuid(), AgentName = "Agent" },
                new ServerModel(Guid.NewGuid(), "model")),
            new AppChatConfiguration("model", []),
            orchestrator,
            NullLogger<LlmAgentRuntime>.Instance);

    private static AgentRuntimeRunRequest CreateRequest() =>
        new()
        {
            Messages = [new AgentInputMessage(AgentMessageRole.User, "go")]
        };

    private static AgentRunContext CreateContext() =>
        new()
        {
            RunId = Guid.NewGuid().ToString("N"),
            DefinitionStack =
            [
                new AgentRunFrame
                {
                    Definition = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, "agent"),
                    DisplayName = "Agent"
                }
            ]
        };

    private static async Task<List<AgentRunEvent>> CollectAsync(IAsyncEnumerable<AgentRunEvent> events)
    {
        var result = new List<AgentRunEvent>();
        await foreach (var runEvent in events)
        {
            result.Add(runEvent);
        }

        return result;
    }

    private sealed class StubOrchestrator(IReadOnlyList<ChatEngineStreamChunk> chunks) : IChatEngineOrchestrator
    {
        public ChatEngineOrchestrationRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
            ChatEngineOrchestrationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed class ThrowingOrchestrator : IChatEngineOrchestrator
    {
        public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
            ChatEngineOrchestrationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class CancelingOrchestrator(CancellationTokenSource cancellationTokenSource) : IChatEngineOrchestrator
    {
        public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
            ChatEngineOrchestrationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationTokenSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
