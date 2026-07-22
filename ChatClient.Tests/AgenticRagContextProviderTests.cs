using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services.Agentic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace ChatClient.Tests;

#pragma warning disable MAAI001
public sealed class AgenticRagContextProviderTests
{
    [Fact]
    public async Task InvokingAsync_UsesCurrentUserMessageAndReturnsTransientInstructions()
    {
        var rag = new FakeRagContextService();
        var provider = new AgenticRagContextProvider(Guid.NewGuid(), Guid.NewGuid(), rag);
        var agent = new StubAgent();

        var first = await provider.InvokingAsync(new AIContextProvider.InvokingContext(
            agent,
            null,
            new AIContext
            {
                Messages = [new ChatMessage(ChatRole.User, "first question")]
            }));
        var second = await provider.InvokingAsync(new AIContextProvider.InvokingContext(
            agent,
            null,
            new AIContext
            {
                Messages = [new ChatMessage(ChatRole.User, "second question")]
            }));

        Assert.Equal(["first question", "second question"], rag.Queries);
        Assert.Equal(["first question"], first.Messages!.Select(static message => message.Text));
        Assert.Equal(["second question"], second.Messages!.Select(static message => message.Text));
        Assert.Contains("untrusted reference data", first.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("context:first question", first.Instructions);
        Assert.DoesNotContain("context:first question", second.Instructions);
        Assert.Contains("context:second question", second.Instructions);
    }

    private sealed class FakeRagContextService : IAgenticRagContextService
    {
        public List<string> Queries { get; } = [];

        public Task<AgenticRagContextResult> TryBuildContextAsync(
            Guid agentId,
            string query,
            Guid? serverId = null,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(new AgenticRagContextResult
            {
                ContextText = $"context:{query}"
            });
        }
    }

    private sealed class StubAgent : AIAgent
    {
        public override string Name => "RAG test";
        public override string? Description => null;
        protected override string IdCore => "rag-test";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new StubSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionData,
            JsonSerializerOptions? options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubSession : AgentSession;
}
#pragma warning restore MAAI001
