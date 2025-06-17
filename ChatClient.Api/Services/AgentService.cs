#pragma warning disable SKEXP0070
#pragma warning disable SKEXP0002
#pragma warning disable SKEXP0110
using ChatClient.Shared.Models;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public class AgentService(KernelService kernelService, ILogger<AgentService> logger)
{
    public async Task<ChatCompletionAgent> CreateChatAgentAsync(ChatConfiguration chatConfiguration, string systemPrompt)
    {
        logger.LogInformation("Creating chat agent for model: {ModelName}", chatConfiguration.ModelName);

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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Processing streaming response through agent: {AgentName}", agent.Name);

        var agentChat = new AgentGroupChat();
        agentChat.AddAgent(agent);

        foreach (var message in chatHistory.Where(m => m.Role != AuthorRole.System))
        {
            agentChat.AddChatMessage(message);
        }

        await foreach (var message in agentChat.InvokeStreamingAsync(cancellationToken))
        {
            if (message.Content is not null)
            {
                yield return new StreamingChatMessageContent(message.Role, message.Content);
            }
        }
    }

    /// <summary>
    /// Processes message through agent (non-streaming)
    /// </summary>
    public async Task<ChatMessageContent> GetAgentResponseAsync(
        ChatCompletionAgent agent,
        ChatHistory chatHistory,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Processing response through agent: {AgentName}", agent.Name);

        var agentChat = new AgentGroupChat();
        agentChat.AddAgent(agent);

        foreach (var message in chatHistory.Where(m => m.Role != AuthorRole.System))
        {
            agentChat.AddChatMessage(message);
        }

        var response = await agentChat.InvokeAsync(cancellationToken).ToListAsync(cancellationToken);
        var lastMessage = response.LastOrDefault();

        return lastMessage ?? new ChatMessageContent(AuthorRole.Assistant, "No response received from agent");
    }
}
