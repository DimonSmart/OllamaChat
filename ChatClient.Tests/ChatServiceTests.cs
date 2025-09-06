using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using OllamaSharp;
using System;
using System.Threading.Tasks;

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
            => Task.FromResult<List<string>>([]);

        public Task<bool> IsAvailableAsync(Guid serverId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private class MockUserSettingsService : IUserSettingsService
    {
        public Task<UserSettings> GetSettingsAsync() => Task.FromResult(new UserSettings());
        public Task SaveSettingsAsync(UserSettings settings) => Task.CompletedTask;
    }

    private class MockLlmServerConfigService : ILlmServerConfigService
    {
        public Task<List<LlmServerConfig>> GetAllAsync() => Task.FromResult<List<LlmServerConfig>>([]);
        public Task<LlmServerConfig?> GetByIdAsync(Guid id) => Task.FromResult<LlmServerConfig?>(null);
        public Task<LlmServerConfig> CreateAsync(LlmServerConfig server) => Task.FromResult(server);
        public Task<LlmServerConfig> UpdateAsync(LlmServerConfig server) => Task.FromResult(server);
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_NoAgents_Throws()
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

        var session = new ChatSessionParameters(new RoundRobinGroupChatManager(), new AppChatConfiguration("m", []), []);
        await Assert.ThrowsAsync<ArgumentException>(() => chatService.StartAsync(session));
    }

    [Fact]
    public async Task StartAsync_SingleAgent_NoSystemMessage()
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
        var session = new ChatSessionParameters(new RoundRobinGroupChatManager(), new AppChatConfiguration("m", []), [prompt]);
        await chatService.StartAsync(session);

        Assert.Empty(chatService.Messages);
    }

    [Fact]
    public async Task ResetChat_ClearsAgents()
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
        var session = new ChatSessionParameters(new RoundRobinGroupChatManager(), new AppChatConfiguration("m", []), [prompt]);
        await chatService.StartAsync(session);
        Assert.Single(chatService.AgentDescriptions);

        var resetRaised = false;
        chatService.ChatReset += () => resetRaised = true;
        chatService.ResetChat();

        Assert.Empty(chatService.AgentDescriptions);
        Assert.True(resetRaised);
    }
}
