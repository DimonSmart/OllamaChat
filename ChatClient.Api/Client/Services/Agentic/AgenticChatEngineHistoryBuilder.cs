using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticChatEngineHistoryBuilder : IChatEngineHistoryBuilder
{
    public IReadOnlyList<IAppChatMessage> Build(IEnumerable<IAppChatMessage> messages)
    {
        if (messages is null)
            return [];

        return messages
            .Where(m => !m.IsStreaming)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) || m.Files.Count > 0)
            .ToList();
    }
}
