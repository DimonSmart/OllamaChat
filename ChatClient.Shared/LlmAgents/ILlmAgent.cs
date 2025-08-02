namespace ChatClient.Shared.LlmAgents;

using ChatClient.Shared.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

public interface ILlmAgent
{
    string Name { get; }
    SystemPrompt? AgentDescription { get; }
    IAsyncEnumerable<StreamingChatMessageContent> GetResponseAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings promptExecutionSettings,
        Kernel kernel,
        CancellationToken cancellationToken = default);
}
