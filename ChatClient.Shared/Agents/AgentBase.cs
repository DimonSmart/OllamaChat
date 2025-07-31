namespace ChatClient.Shared.Agents;

using ChatClient.Shared.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

public abstract class AgentBase : IAgent
{
    protected AgentBase(string name, SystemPrompt? prompt = null)
    {
        Name = name;
        SystemPrompt = prompt;
    }

    public string Name { get; }
    public SystemPrompt? SystemPrompt { get; protected set; }

    public abstract IAsyncEnumerable<StreamingChatMessageContent> GetResponseAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings promptExecutionSettings,
        Kernel kernel,
        CancellationToken cancellationToken = default);
}
