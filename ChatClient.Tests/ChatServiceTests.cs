using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Shared.LlmAgents;
using ChatClient.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Tests;

public class ChatServiceTests
{
    private class DummyAgent : ILlmAgent
    {
        public string Name => "dummy";
        public SystemPrompt? AgentDescription => null;
        public async IAsyncEnumerable<StreamingChatMessageContent> GetResponseAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings promptExecutionSettings,
            Kernel kernel,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }

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
            llmAgentCoordinator: new DefaultLlmAgentCoordinator(new DummyAgent()),
            logger: new LoggerFactory().CreateLogger<ChatService>());

        Assert.Throws<ArgumentException>(() => chatService.InitializeChat([]));
    }
}
