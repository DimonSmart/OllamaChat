namespace ChatClient.Domain.Models;

public sealed class AgentRunRequest
{
    public required AgentExecutionSpec Agent { get; init; }

    public required ServerModel ResolvedModel { get; init; }

    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<AgentRunConversationMessage> Conversation { get; init; }

    public required string UserMessage { get; init; }
}
