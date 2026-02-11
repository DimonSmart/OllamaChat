using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.Services.Reducers;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp;
using System;
using System.Linq;
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

        public Task<IReadOnlyCollection<string>> GetAvailableModelsAsync(Guid serverId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<bool> IsAvailableAsync(Guid serverId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private class MockUserSettingsService : IUserSettingsService
    {
        public Task<UserSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UserSettings());

        public Task SaveSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private class MockLlmServerConfigService : ILlmServerConfigService
    {
        public Task<IReadOnlyCollection<LlmServerConfig>> GetAllAsync() => Task.FromResult<IReadOnlyCollection<LlmServerConfig>>([]);
        public Task<LlmServerConfig?> GetByIdAsync(Guid serverId) => Task.FromResult<LlmServerConfig?>(null);
        public Task CreateAsync(LlmServerConfig serverConfig) => Task.CompletedTask;
        public Task UpdateAsync(LlmServerConfig serverConfig) => Task.CompletedTask;
        public Task DeleteAsync(Guid serverId) => Task.CompletedTask;
    }

    private sealed class MockModelCapabilityService : IModelCapabilityService
    {
        public Task<bool> SupportsFunctionCallingAsync(ServerModel model, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
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
            modelCapabilityService: new MockModelCapabilityService(),
            userSettingsService: new MockUserSettingsService(),
            llmServerConfigService: new MockLlmServerConfigService());

        var session = new ChatSessionParameters(new ResettableRoundRobinGroupChatManager(), new AppChatConfiguration("m", []), []);
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
            modelCapabilityService: new MockModelCapabilityService(),
            userSettingsService: new MockUserSettingsService(),
            llmServerConfigService: new MockLlmServerConfigService());

        var prompt = new AgentDescription { AgentName = "Agent", Content = "Hello" };
        var session = new ChatSessionParameters(new ResettableRoundRobinGroupChatManager(), new AppChatConfiguration("m", []), [prompt]);
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
            modelCapabilityService: new MockModelCapabilityService(),
            userSettingsService: new MockUserSettingsService(),
            llmServerConfigService: new MockLlmServerConfigService());

        var prompt = new AgentDescription { AgentName = "Agent", Content = "Hello" };
        var session = new ChatSessionParameters(new ResettableRoundRobinGroupChatManager(), new AppChatConfiguration("m", []), [prompt]);
        await chatService.StartAsync(session);
        Assert.Single(chatService.AgentDescriptions);

        var resetRaised = false;
        chatService.ChatReset += () => resetRaised = true;
        chatService.ResetChat();

        Assert.Empty(chatService.AgentDescriptions);
        Assert.True(resetRaised);
    }

    [Fact]
    public void ConfigureWhiteboardPlugin_Disabled_SkipsRegistration()
    {
        var chatService = new AppChatService(
            kernelService: null!,
            logger: new LoggerFactory().CreateLogger<AppChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new AppForceLastUserReducer(),
            ollamaKernelService: new MockOllamaKernelService(),
            openAIClientService: new MockOpenAIClientService(),
            modelCapabilityService: new MockModelCapabilityService(),
            userSettingsService: new MockUserSettingsService(),
            llmServerConfigService: new MockLlmServerConfigService());

        var kernel = Kernel.CreateBuilder().Build();
        chatService.ConfigureWhiteboardPlugin(kernel, new AppChatConfiguration("model", [], UseWhiteboard: false), supportsFunctionCalling: true);

        Assert.DoesNotContain(kernel.Plugins, p => p.Name.Equals("whiteboard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigureWhiteboardPlugin_Enabled_RegistersPlugin()
    {
        var chatService = new AppChatService(
            kernelService: null!,
            logger: new LoggerFactory().CreateLogger<AppChatService>(),
            chatHistoryBuilder: new DummyHistoryBuilder(),
            reducer: new AppForceLastUserReducer(),
            ollamaKernelService: new MockOllamaKernelService(),
            openAIClientService: new MockOpenAIClientService(),
            modelCapabilityService: new MockModelCapabilityService(),
            userSettingsService: new MockUserSettingsService(),
            llmServerConfigService: new MockLlmServerConfigService());

        var kernel = Kernel.CreateBuilder().Build();
        chatService.ConfigureWhiteboardPlugin(kernel, new AppChatConfiguration("model", []), supportsFunctionCalling: true);

        Assert.Contains(kernel.Plugins, p => p.Name.Equals("whiteboard", StringComparison.OrdinalIgnoreCase));
    }
}
