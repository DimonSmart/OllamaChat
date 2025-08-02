using ChatClient.Shared.LlmAgents;
using ChatClient.Shared.Models;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

/// <summary>
/// Default agent implementation that proxies chat completion requests to the kernel.
/// </summary>
public class KernelLlmAgent(string name, SystemPrompt? agentDescription = null) : LlmAgentBase(name, agentDescription)
{
    public override async IAsyncEnumerable<StreamingChatMessageContent> GetResponseAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings promptExecutionSettings,
        Kernel kernel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        await foreach (var content in chatService.GetStreamingChatMessageContentsAsync(
            chatHistory,
            promptExecutionSettings,
            kernel,
            cancellationToken))
        {
            if (content.Content is not null)
            {
                yield return content;
            }
        }
    }
}
