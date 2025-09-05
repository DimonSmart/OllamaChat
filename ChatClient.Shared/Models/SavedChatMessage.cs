using Microsoft.Extensions.AI;

namespace ChatClient.Shared.Models;

public record SavedChatMessage(
    Guid Id,
    string Content,
    DateTime MsgDateTime,
    ChatRole Role,
    string? AgentId,
    string? AgentName);

