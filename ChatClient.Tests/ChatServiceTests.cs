using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
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

    private class MockOllamaKernelService : IOllamaKernelService
    {
        public Task<IChatCompletionService> GetClientAsync(Guid serverId)
            => Task.FromResult<IChatCompletionService>(null!);
    }

    private class MockOpenAIClientService : IOpenAIClientService
    {
        public Task<IChatCompletionService> GetClientAsync(ServerModel serverModel, CancellationToken cancellationToken = default)
            => Task.FromResult<IChatCompletionService>(null!);

        public Task<List<string>> GetAvailableModelsAsync(Guid serverId, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());

        public Task<bool> IsAvailableAsync(Guid serverId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private class MockUserSettingsService : IUserSettingsService
    {
        public event Func<Task>? EmbeddingModelChanged;

        public Task<UserSettings> GetSettingsAsync() => Task.FromResult(new UserSettings());
        public Task SaveSettingsAsync(UserSettings settings) => Task.CompletedTask;
    }

    private class MockLlmServerConfigService : ILlmServerConfigService
    {
        public Task<List<LlmServerConfig>> GetAllAsync() => Task.FromResult(new List<LlmServerConfig>());
        public Task<LlmServerConfig?> GetByIdAsync(Guid id) => Task.FromResult<LlmServerConfig?>(null);
        public Task<LlmServerConfig> CreateAsync(LlmServerConfig server) => Task.FromResult(server);
        public Task<LlmServerConfig> UpdateAsync(LlmServerConfig server) => Task.FromResult(server);
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    [Fact]
    public void InitializeChat_NoAgents_Throws()
    {
        var chatService = new AppChatService(
            kernelService: null!,
            logger: new LoggerFactory().CreateLogger<AppChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new AppForceLastUserReducer(),
            ollamaKernelService: new MockOllamaKernelService(),
            openAIClientService: new MockOpenAIClientService(),
            userSettingsService: new MockUserSettingsService(),
            llmServerConfigService: new MockLlmServerConfigService());

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
            ollamaKernelService: new MockOllamaKernelService(),
            openAIClientService: new MockOpenAIClientService(),
            userSettingsService: new MockUserSettingsService(),
            llmServerConfigService: new MockLlmServerConfigService());

        var prompt = new AgentDescription { AgentName = "Agent", Content = "Hello" };
        chatService.InitializeChat([prompt]);

        Assert.Empty(chatService.Messages);
    }
}
