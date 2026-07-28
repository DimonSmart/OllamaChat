using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed class AgentRunRequest
{
    public required AgentExecutionSpec Agent { get; init; }

    public required ServerModel ResolvedModel { get; init; }

    public required AppChatConfiguration Configuration { get; init; }

    public required IReadOnlyList<AgentRunConversationMessage> Conversation { get; init; }

    public required string UserMessage { get; init; }

    public AgentSessionRuntimeResources RuntimeResources { get; init; } = new();
}
