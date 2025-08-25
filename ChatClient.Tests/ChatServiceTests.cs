using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp;
using System;

namespace ChatClient.Tests;

public class ChatServiceTests
{
    private class DummyHistoryBuilder : IAppChatHistoryBuilder
    {
        public Task<ChatHistory> BuildChatHistoryAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, Guid agentId, CancellationToken cancellationToken, Guid? serverId = null)
            => Task.FromResult(new ChatHistory());
    }

    private class MockOllamaClientService : IOllamaClientService
    {
        public bool EmbeddingsAvailable => false;
        public Task<OllamaApiClient> GetClientAsync(Guid serverId) => Task.FromResult(new OllamaApiClient(new HttpClient()));
        public Task<float[]> GenerateEmbeddingAsync(string input, ServerModel model, CancellationToken cancellationToken = default) => Task.FromResult(new float[0]);
        public Task<IReadOnlyList<OllamaModel>> GetModelsAsync(Guid serverId) => Task.FromResult<IReadOnlyList<OllamaModel>>(new List<OllamaModel>());
        public Task<IReadOnlyList<OllamaModel>> GetModelsAsync(ServerModel serverModel) => Task.FromResult<IReadOnlyList<OllamaModel>>(new List<OllamaModel>());
    }

    [Fact]
    public void InitializeChat_NoAgents_Throws()
    {
        var chatService = new AppChatService(
            kernelService: null!,
            logger: new LoggerFactory().CreateLogger<AppChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new AppForceLastUserReducer(),
            ollamaClientService: new MockOllamaClientService());

        Assert.Throws<ArgumentException>(() => chatService.InitializeChat([]));
    }

    [Fact]
    public void InitializeChat_SingleAgent_NoSystemMessage()
    {
        var chatService = new AppChatService(
            kernelService: null!,
            logger: new LoggerFactory().CreateLogger<AppChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new AppForceLastUserReducer(),
            ollamaClientService: new MockOllamaClientService());

        var prompt = new AgentDescription { AgentName = "Agent", Content = "Hello" };
        chatService.InitializeChat([prompt]);

        Assert.Empty(chatService.Messages);
    }
}
