using Microsoft.Extensions.AI;

namespace ChatClient.Domain.Models;

public sealed class AgentRunRequest
{
    public required AgentDefinition Agent { get; init; }

    public required ServerModel ResolvedModel { get; init; }

    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<ChatMessage> Conversation { get; init; }

    public required string UserMessage { get; init; }
}
