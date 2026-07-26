using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace ChatClient.Tests;

#pragma warning disable MAAI001
public sealed class AgentRagTextSearchProviderTests
{
    [Fact]
    public void Auto_WithFunctionCalling_UsesOnDemandTextSearchProvider()
    {
        var providers = BuildProviders(hasRagContent: true, supportsFunctions: true);

        var provider = Assert.IsType<TextSearchProvider>(Assert.Single(providers));

        Assert.Equal(
            TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling,
            AgenticRuntimeAgentFactory.ResolveRagSearchBehavior(supportsFunctions: true));
        Assert.NotNull(provider);
    }

    [Fact]
    public async Task OnDemandProvider_ExposesNamedToolWithoutRetrievingDuringInvocation()
    {
        var search = new Mock<IAgentRagSearchService>(MockBehavior.Strict);
        var provider = AgenticRuntimeAgentFactory.CreateRagProvider(
            Guid.NewGuid(),
            search.Object,
            supportsFunctions: true,
            NullLoggerFactory.Instance);

        var context = await provider.InvokingAsync(new AIContextProvider.InvokingContext(
            new StubAgent(),
            null,
            new AIContext { Messages = [new ChatMessage(ChatRole.User, "Hello")] }));

        Assert.Contains(context.Tools!, tool => tool.Name == "search_agent_knowledge");
        search.VerifyNoOtherCalls();
    }

    [Fact]
    public void Auto_WithoutFunctionCalling_UsesBeforeInvokeTextSearchProvider()
    {
        var providers = BuildProviders(hasRagContent: true, supportsFunctions: false);

        Assert.IsType<TextSearchProvider>(Assert.Single(providers));
        Assert.Equal(
            TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
            AgenticRuntimeAgentFactory.ResolveRagSearchBehavior(supportsFunctions: false));
    }

    [Fact]
    public async Task BeforeInvokeProvider_AddsRetrievedMessageInsteadOfInstructions()
    {
        var agentId = Guid.NewGuid();
        var search = new Mock<IAgentRagSearchService>(MockBehavior.Strict);
        search.Setup(service => service.SearchAsync(
                agentId,
                "What is the codename?",
                5,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RagSearchResponse
            {
                Results = [new RagSearchResult { FileName = "knowledge.md", Content = "ALPHA-47" }]
            });
        var provider = AgenticRuntimeAgentFactory.CreateRagProvider(
            agentId,
            search.Object,
            supportsFunctions: false,
            NullLoggerFactory.Instance);

        var context = await provider.InvokingAsync(new AIContextProvider.InvokingContext(
            new StubAgent(),
            null,
            new AIContext { Messages = [new ChatMessage(ChatRole.User, "What is the codename?")] }));

        Assert.Null(context.Instructions);
        Assert.Contains(context.Messages!, message => message.Text?.Contains("ALPHA-47", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void WithoutIndexedKnowledge_DoesNotAddTextSearchProvider()
    {
        var providers = BuildProviders(hasRagContent: false, supportsFunctions: true);

        Assert.Empty(providers);
    }

    private static List<AIContextProvider> BuildProviders(bool hasRagContent, bool supportsFunctions)
    {
        var agentId = Guid.NewGuid();
        var request = new AgentRunRequest
        {
            Agent = new AgentExecutionSpec { Id = agentId },
            ResolvedModel = new ServerModel(Guid.NewGuid(), "test-model"),
            Configuration = new AppChatConfiguration("test-model", []),
            Conversation = [],
            UserMessage = "Hello"
        };

        return AgenticRuntimeAgentFactory.BuildContextProviders(
            request,
            Mock.Of<IAgentRagSearchService>(MockBehavior.Strict),
            hasRagContent,
            supportsFunctions,
            NullLoggerFactory.Instance,
            todoProfile: null);
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
