using ChatClient.Api.Services;
using ChatClient.Api;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System.Runtime.CompilerServices;
#pragma warning disable MAAI001
using Microsoft.Agents.AI.Compaction;

namespace ChatClient.Tests;

public sealed class CompactionStrategyResolverTests
{
    [Fact]
    public void AddApplicationServices_RegistersCompactionStrategyResolverAsScoped()
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = Mock.Of<IHostEnvironment>();
        var services = new ServiceCollection();
        services.AddApplicationServices(configuration, environment);

        var descriptor = Assert.Single(services.Where(static service => service.ServiceType == typeof(ICompactionStrategyResolver)));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenProfileIsNotSelected()
    {
        var resolver = CreateResolver();
        Assert.Null(await resolver.ResolveAsync(null, new ServerModel(Guid.NewGuid(), "primary"), new StubChatClient()));
    }

    [Fact]
    public async Task ResolveAsync_MaterializesContextWindowAndOrderedPipeline_WithSeparateSummarizer()
    {
        var separateServer = Guid.NewGuid();
        var clients = new StubChatClientFactory();
        var resolver = CreateResolver(clients);
        var primary = new StubChatClient();
        var context = await resolver.ResolveAsync(new CompactionProfile { Name = "Context", Kind = CompactionProfileKinds.ContextWindow, ToolResultThreshold = .55, TruncationThreshold = .85 }, new ServerModel(Guid.NewGuid(), "primary"), primary);
        var contextStrategy = Assert.IsType<ContextWindowCompactionStrategy>(context!.Strategy);
        Assert.Equal(128_000, context.Budget.ContextWindowTokens);
        Assert.Equal(0.55, contextStrategy.ToolEvictionThreshold);
        Assert.Equal(0.85, contextStrategy.TruncationThreshold);
        Assert.Equal([CompactionStageKinds.ToolResult, CompactionStageKinds.Truncation], context.StageKinds);

        var profile = new CompactionProfile
        {
            Name = "Pipeline",
            Kind = CompactionProfileKinds.CustomPipeline,
            Stages =
            [
                CompactionStageDefaults.Create(CompactionStageKinds.ToolResult),
                CompactionStageDefaults.Create(CompactionStageKinds.Truncation),
                CompactionStageDefaults.Create(CompactionStageKinds.Summarization),
                CompactionStageDefaults.Create(CompactionStageKinds.SlidingWindow)
            ]
        };
        profile.Stages[2].SummarizerLlmId = separateServer;
        profile.Stages[2].SummarizerModelName = "summary-model";

        var resolved = await resolver.ResolveAsync(profile, new ServerModel(Guid.NewGuid(), "primary"), primary);
        var pipeline = Assert.IsType<PipelineCompactionStrategy>(resolved!.Strategy);
        Assert.Collection(pipeline.Strategies,
            strategy => Assert.IsType<ToolResultCompactionStrategy>(strategy),
            strategy => Assert.IsType<TruncationCompactionStrategy>(strategy),
            strategy => Assert.IsType<SummarizationCompactionStrategy>(strategy),
            strategy => Assert.IsType<SlidingWindowCompactionStrategy>(strategy));
        Assert.Equal([CompactionStageKinds.ToolResult, CompactionStageKinds.Truncation, CompactionStageKinds.Summarization, CompactionStageKinds.SlidingWindow], resolved.StageKinds);
        Assert.Equal(new ServerModel(separateServer, "summary-model"), Assert.Single(clients.Requests));
    }

    [Fact]
    public async Task ResolveAsync_RejectsInvalidStageBeforeInvocation()
    {
        var clients = new StubChatClientFactory();
        var resolver = CreateResolver(clients);
        var invalid = CompactionStageDefaults.Create(CompactionStageKinds.Summarization);
        invalid.Target.Value = invalid.Trigger.Value;
        invalid.SummarizerLlmId = Guid.NewGuid();
        invalid.SummarizerModelName = "summary";
        var profile = new CompactionProfile { Name = "Invalid", Kind = CompactionProfileKinds.CustomPipeline, Stages = [invalid] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(profile, new ServerModel(Guid.NewGuid(), "primary"), new StubChatClient()));
        Assert.Empty(clients.Requests);
    }

    [Fact]
    public async Task ResolveAsync_RejectsInvalidContextWindowThresholdsBeforeInvocation()
    {
        var resolver = CreateResolver();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(
            new CompactionProfile { Name = "Invalid", Kind = CompactionProfileKinds.ContextWindow, ToolResultThreshold = .90, TruncationThreshold = .80 },
            new ServerModel(Guid.NewGuid(), "primary"),
            new StubChatClient()));

        Assert.Contains("invalid context-window thresholds", exception.Message);
    }

    [Fact]
    public async Task ResolveAsync_RejectsZeroInputBudgetPercentTargetBeforeInvocation()
    {
        var clients = new StubChatClientFactory();
        var stage = CompactionStageDefaults.Create(CompactionStageKinds.ToolResult);
        stage.Target.Value = 0;
        var profile = new CompactionProfile { Name = "Invalid", Kind = CompactionProfileKinds.CustomPipeline, Stages = [stage] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateResolver(clients).ResolveAsync(profile, new ServerModel(Guid.NewGuid(), "primary"), new StubChatClient()));

        Assert.Empty(clients.Requests);
    }

    [Fact]
    public async Task ResolveAsync_PrevalidatesLaterInvalidStageBeforeRequestingEarlierSeparateSummarizer()
    {
        var clients = new StubChatClientFactory();
        var resolver = CreateResolver(clients);
        var profile = CreatePipelineWithEarlierSeparateSummarizer(new()
        {
            Kind = CompactionStageKinds.Truncation,
            Trigger = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 4_000 },
            Target = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 4_000 }
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(profile, new ServerModel(Guid.NewGuid(), "primary"), new StubChatClient()));

        Assert.Contains("invalid limits", exception.Message);
        Assert.Empty(clients.Requests);
    }

    [Fact]
    public async Task ResolveAsync_PrevalidatesLaterUnknownStageBeforeRequestingEarlierSeparateSummarizer()
    {
        var clients = new StubChatClientFactory();
        var resolver = CreateResolver(clients);
        var profile = CreatePipelineWithEarlierSeparateSummarizer(new()
        {
            Kind = "unknown",
            Trigger = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 4_000 },
            Target = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 2_000 }
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(profile, new ServerModel(Guid.NewGuid(), "primary"), new StubChatClient()));

        Assert.Contains("unknown stage", exception.Message);
        Assert.Empty(clients.Requests);
    }

    [Fact]
    public async Task ResolveAsync_UsesPrimaryClientForPrimarySummarizer()
    {
        var clients = new StubChatClientFactory();
        var resolver = CreateResolver(clients);
        var primary = new StubChatClient();
        var profile = new CompactionProfile
        {
            Name = "Primary",
            Kind = CompactionProfileKinds.CustomPipeline,
            Stages = [CompactionStageDefaults.Create(CompactionStageKinds.Summarization)]
        };

        var resolved = await resolver.ResolveAsync(profile, new ServerModel(Guid.NewGuid(), "primary"), primary);
        var summary = Assert.IsType<SummarizationCompactionStrategy>(Assert.Single(Assert.IsType<PipelineCompactionStrategy>(resolved!.Strategy).Strategies));
        Assert.Same(primary, summary.ChatClient);
        Assert.Empty(clients.Requests);
    }

    [Fact]
    public async Task PreflightAsync_ValidatesSeparateSummarizerAvailabilityBeforeClientCreation()
    {
        var serverId = Guid.NewGuid();
        var clients = new StubChatClientFactory();
        var models = new StubModelCapabilityService();
        var resolver = CreateResolver(clients, models);
        var stage = CompactionStageDefaults.Create(CompactionStageKinds.Summarization);
        stage.SummarizerLlmId = serverId;
        stage.SummarizerModelName = "summary-model";
        var profile = new CompactionProfile { Name = "Separate", Kind = CompactionProfileKinds.CustomPipeline, Stages = [stage] };

        await resolver.PreflightAsync(profile);

        Assert.Equal([new ServerModel(serverId, "summary-model")], models.Requests);
        Assert.Empty(clients.Requests);
    }

    [Fact]
    public async Task PreflightAsync_ReportsProfileStageAndModelWhenSeparateSummarizerIsUnavailable()
    {
        var serverId = Guid.NewGuid();
        var models = new StubModelCapabilityService { Failure = new InvalidOperationException("Model endpoint did not respond.") };
        var resolver = CreateResolver(models: models);
        var stage = CompactionStageDefaults.Create(CompactionStageKinds.Summarization);
        stage.SummarizerLlmId = serverId;
        stage.SummarizerModelName = "summary-model";
        var profile = new CompactionProfile { Name = "Separate", Kind = CompactionProfileKinds.CustomPipeline, Stages = [stage] };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.PreflightAsync(profile));

        Assert.Contains("Separate", exception.Message);
        Assert.Contains("stage 1", exception.Message);
        Assert.Contains(serverId.ToString(), exception.Message);
        Assert.Contains("summary-model", exception.Message);
        Assert.Contains("Model endpoint did not respond", exception.Message);
    }

    [Fact]
    public async Task ResolvedContextWindowProfile_ReachesHarnessOptions()
    {
        var resolver = CreateResolver();
        var resolved = await resolver.ResolveAsync(
            new CompactionProfile { Name = "Context", Kind = CompactionProfileKinds.ContextWindow },
            new ServerModel(Guid.NewGuid(), "primary"),
            new StubChatClient());

        var options = AgenticRuntimeAgentFactory.BuildHarnessAgentOptions(
            new AgentRunRequest
            {
                Agent = new AgentExecutionSpec(),
                ResolvedModel = new ServerModel(Guid.NewGuid(), "primary"),
                Configuration = new AppChatConfiguration("primary", []),
                Conversation = [],
                UserMessage = "Hello"
            },
            AgenticToolSet.Empty,
            null!,
            hasRagContent: false,
            supportsFunctions: false,
            todoProfile: null,
            agentModeProfile: null,
            fileAccessProfile: null,
            workspaceStore: null,
            shellExecutor: null,
            NullLoggerFactory.Instance,
            resolved);

        Assert.False(options.DisableCompaction);
        Assert.Same(resolved!.Strategy, options.CompactionStrategy);
        Assert.Equal(resolved.Budget.ContextWindowTokens, options.MaxContextWindowTokens);
        Assert.Equal(resolved.Budget.MaxOutputTokens, options.MaxOutputTokens);
    }

    private sealed class StubBudgetResolver : ICompactionBudgetResolver
    {
        public Task<CompactionBudget> ResolveAsync(CompactionProfile profile, ServerModel model) => Task.FromResult(new CompactionBudget(128_000, 8_000, 120_000));
    }

    private static CompactionStrategyResolver CreateResolver(StubChatClientFactory? clients = null, StubModelCapabilityService? models = null) =>
        new(new StubBudgetResolver(), clients ?? new StubChatClientFactory(), models ?? new StubModelCapabilityService());

    private static CompactionProfile CreatePipelineWithEarlierSeparateSummarizer(CompactionStage laterStage)
    {
        return new CompactionProfile
        {
            Name = "Pipeline",
            Kind = CompactionProfileKinds.CustomPipeline,
            Stages =
            [
                new()
                {
                    Kind = CompactionStageKinds.Summarization,
                    Trigger = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 8_000 },
                    Target = new CompactionLimit { Kind = CompactionLimitKinds.Tokens, Value = 4_000 },
                    SummarizerLlmId = Guid.NewGuid(),
                    SummarizerModelName = "summary"
                },
                laterStage
            ]
        };
    }

    private sealed class StubChatClientFactory : ILlmChatClientFactory
    {
        public List<ServerModel> Requests { get; } = [];
        public Task<IChatClient> CreateAsync(ServerModel model, CancellationToken cancellationToken = default)
        {
            Requests.Add(model);
            return Task.FromResult<IChatClient>(new StubChatClient());
        }
    }

    private sealed class StubModelCapabilityService : IModelCapabilityService
    {
        public List<ServerModel> Requests { get; } = [];
        public Exception? Failure { get; init; }

        public Task EnsureModelSupportedByServerAsync(ServerModel model, CancellationToken cancellationToken = default)
        {
            Requests.Add(model);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task<bool> SupportsFunctionCallingAsync(ServerModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public void Dispose() { }
    }
}
#pragma warning restore MAAI001
