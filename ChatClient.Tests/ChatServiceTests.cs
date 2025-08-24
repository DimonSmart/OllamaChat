using System;

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
    private class DummyHistoryBuilder : IAppChatHistoryBuilder
    {
        public Task<ChatHistory> BuildChatHistoryAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, Guid agentId, CancellationToken cancellationToken, Guid? serverId = null)
            => Task.FromResult(new ChatHistory());
    }

    [Fact]
    public void InitializeChat_NoAgents_Throws()
    {
        var chatService = new AppChatService(
            kernelService: null!,
            logger: new LoggerFactory().CreateLogger<AppChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new AppForceLastUserReducer());

        Assert.Throws<ArgumentException>(() => chatService.InitializeChat([]));
    }

    [Fact]
    public void InitializeChat_SingleAgent_NoSystemMessage()
    {
        var chatService = new AppChatService(
            kernelService: null!,
            logger: new LoggerFactory().CreateLogger<AppChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new AppForceLastUserReducer());

        var prompt = new AgentDescription { AgentName = "Agent", Content = "Hello" };
        chatService.InitializeChat([prompt]);

        Assert.Empty(chatService.Messages);
    }
}
