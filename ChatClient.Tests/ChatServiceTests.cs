using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

public class ChatServiceTests
{
    private class DummyHistoryBuilder : IChatHistoryBuilder
    {
        public Task<ChatHistory> BuildChatHistoryAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, CancellationToken cancellationToken)
            => Task.FromResult(new ChatHistory());
    }

    [Fact]
    public void InitializeChat_NoAgents_Throws()
    {
        var chatService = new ChatService(
            kernelService: null!,
            historyBuilder: new DummyHistoryBuilder(),
            logger: new LoggerFactory().CreateLogger<ChatService>());

        Assert.Throws<ArgumentException>(() => chatService.InitializeChat([]));
    }

    [Fact]
    public void InitializeChat_SingleAgent_AddsSystemMessage()
    {
        var chatService = new ChatService(
            kernelService: null!,
            historyBuilder: new DummyHistoryBuilder(),
            logger: new LoggerFactory().CreateLogger<ChatService>());

        var prompt = new AgentDescription { Name = "Agent", Content = "Hello" };
        chatService.InitializeChat([prompt]);

        Assert.Single(chatService.Messages);
        Assert.Equal(ChatRole.System, chatService.Messages[0].Role);
        Assert.Equal("Hello", chatService.Messages[0].Content);
    }
}
