using System.Runtime.CompilerServices;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Tests.LlmCassettes;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

public sealed class LlmCassetteTests
{
    [Fact]
    public async Task RecordAndReplay_PreservesCallsAndRejectsChangedToolResult()
    {
        using var files = new Fixture();
        var call = new FunctionCallContent("call-42", "read_file", new Dictionary<string, object?>
        {
            ["path"] = "README.md", ["limit"] = 10, ["includeMetadata"] = true
        });
        var scripted = new ScriptedClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]))
            {
                ResponseId = "response-one", FinishReason = ChatFinishReason.ToolCalls
            },
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Blue Parrot"))
            {
                ResponseId = "response-two", FinishReason = ChatFinishReason.Stop
            });
        var options = new ChatOptions { ModelId = "test-model", Temperature = 0.25f };
        var user = new ChatMessage(ChatRole.User, "Read README.md");
        var toolResult = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call-42", new { text = "Project: Blue Parrot", count = 2 })]);

        var record = new LlmCassette(files.Path, record: true);
        using (var client = await new CassetteChatClientFactory(new FakeFactory(scripted), record)
                   .CreateAsync(new ServerModel(Guid.NewGuid(), "test-model")))
        {
            var first = await client.GetResponseAsync([user], options);
            Assert.Equal("call-42", Assert.IsType<FunctionCallContent>(first.Messages[0].Contents[0]).CallId);
            var second = await client.GetResponseAsync([user, first.Messages[0], toolResult], options);
            Assert.Equal("Blue Parrot", second.Text);
        }
        await record.CompleteAsync();

        var replay = new LlmCassette(files.Path);
        using (var client = await new CassetteChatClientFactory(new ThrowingFactory(), replay)
                   .CreateAsync(new ServerModel(Guid.NewGuid(), "test-model")))
        {
            var first = await client.GetResponseAsync([user], options);
            var restoredCall = Assert.IsType<FunctionCallContent>(first.Messages[0].Contents[0]);
            Assert.Equal("call-42", restoredCall.CallId);
            Assert.Equal("read_file", restoredCall.Name);
            var second = await client.GetResponseAsync([user, first.Messages[0], toolResult], options);
            Assert.Equal("Blue Parrot", second.Text);
        }
        await replay.CompleteAsync();

        var mismatch = new LlmCassette(files.Path);
        using (var client = await new CassetteChatClientFactory(new ThrowingFactory(), mismatch)
                   .CreateAsync(new ServerModel(Guid.NewGuid(), "test-model")))
        {
            var first = await client.GetResponseAsync([user], options);
            var changed = new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("call-42", new { text = "Project: Red Parrot", count = 2 })]);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GetResponseAsync([user, first.Messages[0], changed], options));
            Assert.Contains("Exchange: 2", error.Message);
            Assert.Contains("request.messages[2].contents[0].result", error.Message);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => mismatch.CompleteAsync());
    }

    [Fact]
    public async Task StreamingPreservesChunksAndIds()
    {
        using var files = new Fixture();
        var client = new ScriptedClient();
        var record = new LlmCassette(files.Path, record: true);
        using (var wrapped = new RecordingChatClient(client, record))
        {
            record.ClaimClient();
            var recorded = await Collect(wrapped.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "stream")]));
            Assert.Equal(3, recorded.Count);
        }
        await record.CompleteAsync();

        var replay = new LlmCassette(files.Path);
        replay.ClaimClient();
        using var playback = new ReplayChatClient(replay);
        var updates = await Collect(playback.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "stream")]));
        Assert.Equal(3, updates.Count);
        Assert.Equal("hello ", updates[0].Text);
        Assert.Equal("call-7", Assert.IsType<FunctionCallContent>(updates[1].Contents[0]).CallId);
        Assert.Equal("world", updates[2].Text);
        await replay.CompleteAsync();
    }

    [Fact]
    public async Task AbortedStreamCannotOverwriteExistingCassette()
    {
        using var files = new Fixture();
        await File.WriteAllTextAsync(files.Path, "previous contents");
        var record = new LlmCassette(files.Path, record: true);
        record.ClaimClient();
        using (var client = new RecordingChatClient(new ScriptedClient(), record))
        {
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "stream")]))
            {
                break;
            }
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => record.CompleteAsync());
        Assert.Equal("previous contents", await File.ReadAllTextAsync(files.Path));
    }

    [Fact]
    public async Task WorkspacePathsAreNormalizedAndRestoredInCalls()
    {
        using var files = new Fixture();
        var oldRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cassette-old");
        var newRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cassette-new");
        var record = new LlmCassette(files.Path, record: true, workspaceRoot: oldRoot);
        record.ClaimClient();
        using (var client = new RecordingChatClient(new FixedFunctionClient(oldRoot), record))
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, oldRoot + "/README.md")]);
        await record.CompleteAsync();
        Assert.Contains("{{WORKSPACE_ROOT}}", await File.ReadAllTextAsync(files.Path));

        var replay = new LlmCassette(files.Path, workspaceRoot: newRoot);
        replay.ClaimClient();
        using var playback = new ReplayChatClient(replay);
        var result = await playback.GetResponseAsync([new ChatMessage(ChatRole.User, newRoot + "/README.md")]);
        var call = Assert.IsType<FunctionCallContent>(result.Messages[0].Contents[0]);
        Assert.Contains("cassette-new", call.Arguments!["path"]!.ToString());
        await replay.CompleteAsync();
    }

    [Fact]
    public async Task AdditionalExchangesAndSecondClientAreRejected()
    {
        using var files = new Fixture();
        var record = new LlmCassette(files.Path, record: true);
        var factory = new CassetteChatClientFactory(new FakeFactory(new ScriptedClient()), record);
        using var client = await factory.CreateAsync(new ServerModel(Guid.NewGuid(), "model"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateAsync(new ServerModel(Guid.NewGuid(), "model")));
        using var active = record.Enter();
        Assert.Throws<InvalidOperationException>(() => record.Enter());
    }

    private static async Task<List<ChatResponseUpdate>> Collect(IAsyncEnumerable<ChatResponseUpdate> source)
    {
        var result = new List<ChatResponseUpdate>();
        await foreach (var update in source) result.Add(update);
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ollamachat-cassette-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "cassette.json");
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private sealed class FakeFactory(IChatClient client) : ILlmChatClientFactory
    {
        public Task<IChatClient> CreateAsync(ServerModel model, CancellationToken cancellationToken = default)
            => Task.FromResult(client);
    }

    private sealed class ThrowingFactory : ILlmChatClientFactory
    {
        public Task<IChatClient> CreateAsync(ServerModel model, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Real factory must not be accessed in Replay.");
    }

    private sealed class FixedFunctionClient(string root) : IChatClient
    {
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call", "read", new Dictionary<string, object?>
                { ["path"] = root + "/README.md" })
            ])));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }

    private sealed class ScriptedClient(params ChatResponse[] responses) : IChatClient
    {
        private int _index;
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(responses[_index++]);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hello ") { MessageId = "message-1" };
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call-7", "read", new Dictionary<string, object?> { ["path"] = "README.md" })])
            { MessageId = "message-1", FinishReason = ChatFinishReason.ToolCalls };
            yield return new ChatResponseUpdate(ChatRole.Assistant, "world") { MessageId = "message-2" };
        }
    }
}
