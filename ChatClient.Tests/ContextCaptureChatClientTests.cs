using ChatClient.Api.Client.Services.Agentic;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class ContextCaptureChatClientTests
{
    [Fact]
    public async Task MetadataOnly_ObservesBoundaryAndUsage_WithoutRetainingAnyContent()
    {
        var store = new ContextCaptureStore();
        var fake = new FakeClient();
        using var client = Observe(fake, store);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "private message"),
            new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "calculate", new Dictionary<string, object?> { ["private"] = "argument" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "private result")])
        };
        var options = new ChatOptions
        {
            Instructions = "secret instructions",
            Tools = [AIFunctionFactory.Create((int x) => x + 1, "increment", "increment a number")]
        };
        using (ContextCaptureScope.Begin("run-1"))
        {
            var response = await client.GetResponseAsync(messages, options, TestContext.Current.CancellationToken);
            Assert.Same(fake.Response, response);
        }

        var invocation = Assert.Single(store.GetInvocations("run-1"));
        Assert.Equal("example", invocation.AgentName);
        Assert.Equal("main", invocation.Purpose);
        Assert.Equal(ContextInvocationStatus.Completed, invocation.Status);
        Assert.Equal(83L, invocation.ProviderInputTokens);
        Assert.Equal(7L, invocation.ProviderOutputTokens);
        Assert.True(invocation.EstimatedTokens > 0);
        Assert.Equal(4, invocation.CategoryTokens.Count);
        Assert.Same(options, fake.LastOptions);
        Assert.Equal(messages, fake.LastMessages);
        Assert.All(store.GetSegments(invocation.InvocationId), s =>
        {
            Assert.Null(s.BlobId);
            Assert.Null(store.GetContent(s.BlobId));
        });
    }

    [Fact]
    public async Task Streaming_StartsAtEnumeration_OneInvocation_AndPassesThroughUpdates()
    {
        var store = new ContextCaptureStore();
        var fake = new FakeClient();
        using var client = Observe(fake, store);
        var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        Assert.Empty(store.GetInvocations());

        var observed = new List<ChatResponseUpdate>();
        await foreach (var update in stream.WithCancellation(TestContext.Current.CancellationToken))
            observed.Add(update);

        Assert.Equal(2, observed.Count);
        Assert.Same(fake.Updates[0], observed[0]);
        Assert.Same(fake.Updates[1], observed[1]);
        var invocation = Assert.Single(store.GetInvocations());
        Assert.Equal(ContextInvocationStatus.Completed, invocation.Status);
        Assert.Equal(24L, invocation.ProviderInputTokens);
        Assert.Equal(3L, invocation.ProviderOutputTokens);
        Assert.NotNull(invocation.Duration);
    }

    [Fact]
    public async Task FullContext_IsProspective_AndCanBeCleared()
    {
        var store = new ContextCaptureStore();
        using var client = Observe(new FakeClient(), store);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "old secret")]);
        store.Mode = ContextCaptureMode.FullContext;
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "new secret")]);
        var calls = store.GetInvocations();
        Assert.Equal(2, calls.Count);
        Assert.All(store.GetSegments(calls[0].InvocationId), x => Assert.Null(x.BlobId));
        var newer = Assert.Single(store.GetSegments(calls[1].InvocationId));
        Assert.Contains("new secret", store.GetContent(newer.BlobId)!.Content);

        store.Mode = ContextCaptureMode.MetadataOnly;
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "later secret")]);
        Assert.All(store.GetSegments(store.GetInvocations()[2].InvocationId), x => Assert.Null(x.BlobId));
        store.ClearContent();
        Assert.Null(store.GetContent(newer.BlobId));
        Assert.All(store.GetSegments(calls[1].InvocationId), x => Assert.Null(x.BlobId));
    }

    [Fact]
    public async Task OffAndLimits_DoNotInterruptUnderlyingModel()
    {
        var store = new ContextCaptureStore(new ContextCaptureLimits(MaxInvocationsPerRun: 2, MaxBlobBytes: 3));
        var fake = new FakeClient();
        using var client = Observe(fake, store);
        store.Mode = ContextCaptureMode.Off;
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "uncaptured")]);
        Assert.Empty(store.GetInvocations());

        store.Mode = ContextCaptureMode.FullContext;
        for (int i = 0; i < 4; i++)
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "sensitive text")]);
        Assert.Equal(5, fake.NonStreamingCalls);
        var calls = store.GetInvocations();
        Assert.Equal(2, calls.Count);
        Assert.True(calls[0].IsTruncated);
        Assert.True(calls[1].IsTruncated);
        Assert.All(store.GetSegments(calls[0].InvocationId), s => Assert.Null(s.BlobId));
    }

    [Fact]
    public async Task SeparateConcurrentRunScopes_KeepOwnIdentity()
    {
        var store = new ContextCaptureStore();
        using var client = Observe(new FakeClient(), store);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async n =>
        {
            using (ContextCaptureScope.Begin("run-" + n))
                await client.GetResponseAsync([new ChatMessage(ChatRole.User, "message " + n)]);
        }));
        for (int i = 0; i < 8; i++)
            Assert.Single(store.GetInvocations("run-" + i));
    }

    [Fact]
    public async Task ModelFailure_IsPreserved_AndInvocationIsMarkedFailed()
    {
        var store = new ContextCaptureStore();
        var fake = new FakeClient { Failure = new InvalidOperationException("provider failed") };
        using var client = Observe(fake, store);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
        Assert.Same(fake.Failure, ex);
        Assert.Equal(ContextInvocationStatus.Failed, Assert.Single(store.GetInvocations()).Status);
    }

    private static ContextCaptureChatClient Observe(FakeClient fake, ContextCaptureStore store) =>
        new(fake, store, "example", "example-1", "main", "test-model");

    private sealed class FakeClient : IChatClient
    {
        public ChatResponse Response { get; } = new(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 83, OutputTokenCount = 7 }
        };
        public ChatResponseUpdate[] Updates { get; } =
        [
            new(ChatRole.Assistant, "stream"),
            new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 24, OutputTokenCount = 3 })])
        ];
        public Exception? Failure { get; set; }
        public int NonStreamingCalls { get; private set; }
        public ChatOptions? LastOptions { get; private set; }
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            NonStreamingCalls++;
            LastMessages = messages.ToArray();
            LastOptions = options;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var update in Updates) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
