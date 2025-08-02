using ChatClient.Api.Services;
using ChatClient.Shared.LlmAgents;
using ChatClient.Shared.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Tests;

public class MultiLlmAgentCoordinatorTests
{
    private class DummyAgent(string name) : ILlmAgent
    {
        public string Name { get; } = name;
        public SystemPrompt? AgentDescription => null;
        public IAsyncEnumerable<StreamingChatMessageContent> GetResponseAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings promptExecutionSettings,
            Kernel kernel,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<StreamingChatMessageContent>();
    }

    [Fact]
    public void GetNextAgent_RotatesThroughWorkers()
    {
        var manager = new DummyAgent("manager");
        var worker1 = new DummyAgent("worker1");
        var worker2 = new DummyAgent("worker2");
        var coordinator = new MultiLlmAgentCoordinator(manager, new[] { worker1, worker2 });

        Assert.Same(worker1, coordinator.GetNextAgent());
        Assert.Same(worker2, coordinator.GetNextAgent());
        Assert.Same(worker1, coordinator.GetNextAgent());
    }

    [Fact]
    public void GetNextAgent_NoWorkers_ReturnsManager()
    {
        var manager = new DummyAgent("manager");
        var coordinator = new MultiLlmAgentCoordinator(manager, Enumerable.Empty<ILlmAgent>());

        Assert.Same(manager, coordinator.GetNextAgent());
    }
}
