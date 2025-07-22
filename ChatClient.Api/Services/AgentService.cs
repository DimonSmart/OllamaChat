#pragma warning disable SKEXP0070
#pragma warning disable SKEXP0002
#pragma warning disable SKEXP0110
using ChatClient.Shared.Models;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public class AgentService(KernelService kernelService)
{
    public async Task<ChatCompletionAgent> CreateChatAgentAsync(ChatConfiguration chatConfiguration, string systemPrompt)
    {
        var kernel = await kernelService.CreateKernelAsync(chatConfiguration);

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
    public async IAsyncEnumerable<StreamingChatMessageContent> GetAgentStreamingResponseAsync(
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

        // chatHistory already contains the agent instructions and has history mode applied

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
