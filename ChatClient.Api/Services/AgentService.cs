using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public static class AgentService
{
    /// <summary>
    /// Processes message through agent and returns streaming response
    /// </summary>
    public static async IAsyncEnumerable<StreamingChatMessageContent> GetAgentStreamingResponseAsync(
         ChatHistory chatHistory,
         PromptExecutionSettings promptExecutionSettings,
         Kernel kernel,
         [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        await foreach (StreamingChatMessageContent content in chatService.GetStreamingChatMessageContentsAsync(
            chatHistory,
            promptExecutionSettings,
            kernel,
            cancellationToken: cancellationToken))
        {
            if (content.Content is not null)
            {
                yield return content;
            }
        }
    }
}
