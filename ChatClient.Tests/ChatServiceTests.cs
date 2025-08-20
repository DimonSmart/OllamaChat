using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

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
            logger: new LoggerFactory().CreateLogger<ChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new ForceLastUserReducer());

        Assert.Throws<ArgumentException>(() => chatService.InitializeChat([]));
    }

    [Fact]
    public void InitializeChat_SingleAgent_NoSystemMessage()
    {
        var chatService = new ChatService(
            kernelService: null!,
            logger: new LoggerFactory().CreateLogger<ChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new ForceLastUserReducer());

        var prompt = new AgentDescription { AgentName = "Agent", Content = "Hello" };
        chatService.InitializeChat([prompt]);

        Assert.Empty(chatService.Messages);
    }
}
