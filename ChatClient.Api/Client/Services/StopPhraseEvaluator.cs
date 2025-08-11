using System;
using System.Linq;

using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

internal sealed class StopPhraseEvaluator(string agentName, string phrase)
{
    public bool Evaluate(ChatHistory history, out GroupChatManagerResult<bool> result)
    {
        result = new(false);

        if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var lastByAgent = history.LastOrDefault(m => m.AuthorName == agentName);
        if (lastByAgent?.ToString()?.Contains(phrase, StringComparison.OrdinalIgnoreCase) == true)
        {
            result = new(true) { Reason = $"{agentName} said {phrase}" };
            return true;
        }

        return false;
    }
}

#pragma warning restore SKEXP0110
