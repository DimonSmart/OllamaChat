namespace ChatClient.Shared.LlmAgents;

using ChatClient.Shared.Models;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

public abstract class LlmAgentBase : ILlmAgent
{
    protected LlmAgentBase(string name, SystemPrompt? agentDescription = null)
    {
        Name = name;
        AgentDescription = agentDescription;
    }

    public string Name { get; }
    public SystemPrompt? AgentDescription { get; protected set; }

    public abstract IAsyncEnumerable<StreamingChatMessageContent> GetResponseAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings promptExecutionSettings,
        Kernel kernel,
        CancellationToken cancellationToken = default);
}
