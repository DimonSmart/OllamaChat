using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticExecutionRuntimeRequest
{
    public required AgentDescription Agent { get; init; }

    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<ChatMessage> Conversation { get; init; }

    public required string UserMessage { get; init; }

    public WhiteboardState? Whiteboard { get; init; }
}
