using System.Runtime.CompilerServices;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
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
            Agents = [agent],
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
            Agents = [agent]
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
            Agents = [agent]
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
            Agents = [agent]
        });

        var sendTask = service.SendAsync("ping");
        await WaitUntilAsync(() => service.IsAnswering, TimeSpan.FromSeconds(2));

        await service.CancelAsync();
        await sendTask;

        var assistant = service.Messages.Last(m => m.Role == ChatRole.Assistant);
        Assert.True(assistant.IsCanceled);
    }

    private static AgenticChatEngineSessionService CreateService(IChatEngineOrchestrator orchestrator) =>
        new(
            new LoggerFactory().CreateLogger<AgenticChatEngineSessionService>(),
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
