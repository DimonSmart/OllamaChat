using System.Collections.Generic;
using System.Linq;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

using ChatClient.Shared.LlmAgents;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

/// <summary>
/// Agent responsible for selecting which worker agent should handle the next message.
/// Currently it does not produce user-facing responses and relies on simple policies.
/// </summary>
public class ManagerLlmAgent(string name, SystemPrompt? agentDescription = null) : LlmAgentBase(name, agentDescription)
{
    public override IAsyncEnumerable<StreamingChatMessageContent> GetResponseAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings promptExecutionSettings,
        Kernel kernel,
        CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<StreamingChatMessageContent>();
}

