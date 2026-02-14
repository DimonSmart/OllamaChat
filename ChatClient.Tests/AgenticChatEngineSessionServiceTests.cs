using System.Runtime.CompilerServices;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using ChatClient.Domain.Models.ChatStrategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class AgenticChatEngineSessionServiceTests
{
    private sealed class StubOrchestrator : IChatEngineOrchestrator
    {
        public required Func<ChatEngineOrchestrationRequest, CancellationToken, IAsyncEnumerable<ChatEngineStreamChunk>> Handler { get; init; }

        public IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
            ChatEngineOrchestrationRequest request,
            CancellationToken cancellationToken = default) => Handler(request, cancellationToken);
    }

    private sealed class StubModelCapabilityService : IModelCapabilityService
    {
        public Func<ServerModel, CancellationToken, Task>? EnsureModelSupportedByServerHandler { get; init; }
        public Func<ServerModel, CancellationToken, Task<bool>> SupportsFunctionCallingHandler { get; init; } =
            static (_, _) => Task.FromResult(true);

        public Task EnsureModelSupportedByServerAsync(ServerModel model, CancellationToken cancellationToken = default)
            => EnsureModelSupportedByServerHandler?.Invoke(model, cancellationToken) ?? Task.CompletedTask;

        public Task<bool> SupportsFunctionCallingAsync(ServerModel model, CancellationToken cancellationToken = default)
            => SupportsFunctionCallingHandler(model, cancellationToken);
    }

    [Fact]
    public async Task StartAsync_LoadsHistoryAndAgents()
    {
        var orchestrator = new StubOrchestrator
        {
            Handler = static (_, _) => EmptyStream()
        };
        var service = CreateService(orchestrator);

        var agent = CreateAgent("Agentic");
        var history = new List<IAppChatMessage>
        {
            new AppChatMessage("loaded", DateTime.UtcNow, ChatRole.User)
        };

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model-a", []),
            Agents = ResolveAgents(agent),
            History = history
        });

        Assert.Single(service.AgentDescriptions);
        Assert.Single(service.Messages);
        Assert.Equal("loaded", service.Messages.First().Content);
    }

    [Fact]
    public async Task SendAsync_ProducesFinalAssistantMessage()
    {
        var orchestrator = new StubOrchestrator
        {
            Handler = static (request, cancellationToken) => StreamChunks(
                request.Agent.AgentName,
                ["Hello", " world"],
                cancellationToken)
        };
        var service = CreateService(orchestrator);
        var agent = CreateAgent("Agentic");

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model-a", []),
            Agents = ResolveAgents(agent)
        });

        await service.SendAsync("ping");

        var assistant = service.Messages.Last(m => m.Role == ChatRole.Assistant);
        Assert.Equal("Hello world", assistant.Content);
        Assert.False(assistant.IsStreaming);
        Assert.False(string.IsNullOrWhiteSpace(assistant.Statistics));
    }

    [Fact]
    public async Task SendAsync_AddsRetrievedContextAndFunctionCalls()
    {
        var calls = new List<FunctionCallRecord>
        {
            new("srv", "tool", "{\"q\":\"ping\"}", "status=ok;attempt=1;durationMs=12;response={\"ok\":true}")
        };

        var orchestrator = new StubOrchestrator
        {
            Handler = (request, cancellationToken) => StreamWithContextAndCalls(
                request.Agent.AgentName,
                "Retrieved context:\n[1] file.md\ncontent",
                calls,
                cancellationToken)
        };
        var service = CreateService(orchestrator);
        var agent = CreateAgent("Agentic");

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model-a", []),
            Agents = ResolveAgents(agent)
        });

        await service.SendAsync("ping");

        var toolMessage = service.Messages.FirstOrDefault(m => m.Role == ChatRole.Tool);
        Assert.NotNull(toolMessage);
        Assert.Contains("Retrieved context:", toolMessage!.Content);

        var assistant = service.Messages.Last(m => m.Role == ChatRole.Assistant);
        Assert.Single(assistant.FunctionCalls);
        Assert.Equal("srv", assistant.FunctionCalls.First().Server);
    }

    [Fact]
    public async Task CancelAsync_MarksStreamingMessageAsCanceled()
    {
        var orchestrator = new StubOrchestrator
        {
            Handler = static (request, cancellationToken) => LongStream(request.Agent.AgentName, cancellationToken)
        };
        var service = CreateService(orchestrator);
        var agent = CreateAgent("Agentic");

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model-a", []),
            Agents = ResolveAgents(agent)
        });

        var sendTask = service.SendAsync("ping");
        await WaitUntilAsync(() => service.IsAnswering, TimeSpan.FromSeconds(2));

        await service.CancelAsync();
        await sendTask;

        var assistant = service.Messages.Last(m => m.Role == ChatRole.Assistant);
        Assert.True(assistant.IsCanceled);
    }

    [Fact]
    public async Task SendAsync_WithMultipleAgents_ProducesMessagesInRoundRobinOrder()
    {
        var orchestrator = new StubOrchestrator
        {
            Handler = static (request, cancellationToken) => StreamChunks(
                request.Agent.AgentName,
                [request.Agent.AgentName],
                cancellationToken)
        };

        var service = CreateService(orchestrator);
        var agentA = CreateAgent("Agent-A");
        var agentB = CreateAgent("Agent-B");

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model-a", []),
            Agents = ResolveAgents(agentA, agentB),
            ChatStrategyName = "RoundRobin",
            ChatStrategyOptions = new RoundRobinChatStrategyOptions { Rounds = 1 }
        });

        await service.SendAsync("ping");

        var assistants = service.Messages
            .Where(m => m.Role == ChatRole.Assistant && !m.IsCanceled)
            .ToList();

        Assert.Equal(2, assistants.Count);
        Assert.Equal("Agent-A", assistants[0].AgentName);
        Assert.Equal("Agent-A", assistants[0].Content);
        Assert.Equal("Agent-B", assistants[1].AgentName);
        Assert.Equal("Agent-B", assistants[1].Content);
    }

    [Fact]
    public async Task SendAsync_WithMultipleRounds_RepeatsAgentsPerRound()
    {
        var orchestrator = new StubOrchestrator
        {
            Handler = static (request, cancellationToken) => StreamChunks(
                request.Agent.AgentName,
                [request.Agent.AgentName],
                cancellationToken)
        };

        var service = CreateService(orchestrator);
        var agentA = CreateAgent("Agent-A");
        var agentB = CreateAgent("Agent-B");

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model-a", []),
            Agents = ResolveAgents(agentA, agentB),
            ChatStrategyName = "RoundRobin",
            ChatStrategyOptions = new RoundRobinChatStrategyOptions { Rounds = 2 }
        });

        await service.SendAsync("ping");

        var assistants = service.Messages
            .Where(m => m.Role == ChatRole.Assistant && !m.IsCanceled)
            .Select(m => m.AgentName)
            .ToList();

        Assert.Equal(["Agent-A", "Agent-B", "Agent-A", "Agent-B"], assistants);
    }

    [Fact]
    public async Task SendAsync_WithMultipleAgents_EnablesRagOnlyForFirstExecution()
    {
        var ragFlags = new List<bool>();
        var orchestrator = new StubOrchestrator
        {
            Handler = (request, cancellationToken) =>
            {
                ragFlags.Add(request.EnableRagContext);
                return StreamChunks(request.Agent.AgentName, ["ok"], cancellationToken);
            }
        };

        var service = CreateService(orchestrator);
        var agentA = CreateAgent("Agent-A");
        var agentB = CreateAgent("Agent-B");

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model-a", []),
            Agents = ResolveAgents(agentA, agentB),
            ChatStrategyName = "RoundRobin",
            ChatStrategyOptions = new RoundRobinChatStrategyOptions { Rounds = 2 }
        });

        await service.SendAsync("ping");

        Assert.Equal([true, false, false, false], ragFlags);
    }

    [Fact]
    public async Task SendAsync_WithRoundRobinSummary_AppendsSummaryAgentAtTheEnd()
    {
        var orchestrator = new StubOrchestrator
        {
            Handler = static (request, cancellationToken) => StreamChunks(
                request.Agent.AgentName,
                [request.Agent.AgentName],
                cancellationToken)
        };

        var service = CreateService(orchestrator);
        var agentA = CreateAgent("Agent-A");
        var agentB = CreateAgent("Agent-B");
        var summary = CreateAgent("Summary");

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model-a", []),
            Agents = ResolveAgents(agentA, agentB, summary),
            ChatStrategyName = "RoundRobinWithSummary",
            ChatStrategyOptions = new RoundRobinSummaryChatStrategyOptions
            {
                Rounds = 2,
                SummaryAgent = summary.AgentId
            }
        });

        await service.SendAsync("ping");

        var assistants = service.Messages
            .Where(m => m.Role == ChatRole.Assistant && !m.IsCanceled)
            .Select(m => m.AgentName)
            .ToList();

        Assert.Equal(["Agent-A", "Agent-B", "Agent-A", "Agent-B", "Summary"], assistants);
    }

    [Fact]
    public async Task StartAsync_Throws_WhenModelIsNotSupportedByServer()
    {
        var orchestrator = new StubOrchestrator
        {
            Handler = static (_, _) => EmptyStream()
        };

        var capability = new StubModelCapabilityService
        {
            EnsureModelSupportedByServerHandler = static (model, _) =>
                throw new InvalidOperationException($"Model '{model.ModelName}' is unavailable.")
        };

        var service = CreateService(orchestrator, capability);
        var agent = CreateAgent("Agentic");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(new ChatEngineSessionStartRequest
            {
                Configuration = new AppChatConfiguration("model-a", []),
                Agents = ResolveAgents(agent)
            }));
    }

    [Fact]
    public async Task SendAsync_UsesResolvedModelWithoutConfigurationFallback()
    {
        ServerModel? capturedModel = null;

        var orchestrator = new StubOrchestrator
        {
            Handler = (request, cancellationToken) =>
            {
                capturedModel = request.ResolvedModel;
                return StreamChunks(request.Agent.AgentName, ["ok"], cancellationToken);
            }
        };

        var service = CreateService(orchestrator);
        var agent = CreateAgent("Agentic");
        agent.ModelName = null;

        var resolvedModel = new ServerModel(Guid.NewGuid(), "resolved-model");

        await service.StartAsync(new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("config-model", []),
            Agents = [new ResolvedChatAgent(agent, resolvedModel)]
        });

        await service.SendAsync("ping");

        Assert.NotNull(capturedModel);
        Assert.Equal(resolvedModel.ServerId, capturedModel!.ServerId);
        Assert.Equal("resolved-model", capturedModel.ModelName);

        var assistant = service.Messages.Last(m => m.Role == ChatRole.Assistant);
        Assert.Contains("resolved-model", assistant.Statistics);
        Assert.DoesNotContain("config-model", assistant.Statistics);
    }

    private static AgenticChatEngineSessionService CreateService(
        IChatEngineOrchestrator orchestrator,
        IModelCapabilityService? capabilityService = null) =>
        new(
            new LoggerFactory().CreateLogger<AgenticChatEngineSessionService>(),
            capabilityService ?? new StubModelCapabilityService(),
            orchestrator,
            new AgenticChatEngineHistoryBuilder(),
            new AgenticChatEngineStreamingBridge());

    private static AgentDescription CreateAgent(string name) =>
        new()
        {
            AgentName = name,
            ShortName = $"{name}-id",
            Content = "You are helpful.",
            ModelName = "model-a"
        };

    private static IReadOnlyList<ResolvedChatAgent> ResolveAgents(params AgentDescription[] agents)
    {
        return agents
            .Select(agent => new ResolvedChatAgent(
                agent,
                new ServerModel(Guid.NewGuid(), agent.ModelName ?? "model-a")))
            .ToList();
    }

    private static async IAsyncEnumerable<ChatEngineStreamChunk> EmptyStream(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    private static async IAsyncEnumerable<ChatEngineStreamChunk> StreamChunks(
        string agentName,
        IReadOnlyList<string> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ChatEngineStreamChunk(agentName, chunk);
        }

        yield return new ChatEngineStreamChunk(agentName, string.Empty, IsFinal: true);
    }

    private static async IAsyncEnumerable<ChatEngineStreamChunk> LongStream(
        string agentName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < 100; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatEngineStreamChunk(agentName, "x");
            await Task.Delay(15, cancellationToken);
        }

        yield return new ChatEngineStreamChunk(agentName, string.Empty, IsFinal: true);
    }

    private static async IAsyncEnumerable<ChatEngineStreamChunk> StreamWithContextAndCalls(
        string agentName,
        string retrievedContext,
        IReadOnlyList<FunctionCallRecord> functionCalls,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new ChatEngineStreamChunk(agentName, "Answer");
        yield return new ChatEngineStreamChunk(
            agentName,
            string.Empty,
            IsFinal: true,
            FunctionCalls: functionCalls,
            RetrievedContext: retrievedContext);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition was not reached in time.");

            await Task.Delay(15);
        }
    }
}
