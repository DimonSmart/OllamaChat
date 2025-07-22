#pragma warning disable SKEXP0070
#pragma warning disable SKEXP0002
#pragma warning disable SKEXP0110
using ChatClient.Shared.Models;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public static class AgentService
{
    public static ChatCompletionAgent CreateChatAgent(Kernel kernel, string systemPrompt)
    {
        var agent = new ChatCompletionAgent
        {
            Kernel = kernel,
            Name = "OllamaAgent",
            Instructions = systemPrompt,
            Description = "AI Assistant powered by Ollama"
        };

        return agent;
    }

    /// <summary>
    /// Processes message through agent and returns streaming response
    /// </summary>
    public static async IAsyncEnumerable<StreamingChatMessageContent> GetAgentStreamingResponseAsync(
        ChatCompletionAgent agent,
        ChatHistory chatHistory,
        ChatConfiguration chatConfiguration,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        var chatService = agent.Kernel.GetRequiredService<IChatCompletionService>();
        var executionSettings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = chatConfiguration.Functions.Any()
                ? FunctionChoiceBehavior.Auto()
                : FunctionChoiceBehavior.None()
        };

        await foreach (var content in chatService.GetStreamingChatMessageContentsAsync(
            chatHistory,
            executionSettings,
            agent.Kernel,
            cancellationToken: cancellationToken))
        {
            if (content.Content is not null)
            {
                yield return content;
            }
        }
    }
}
