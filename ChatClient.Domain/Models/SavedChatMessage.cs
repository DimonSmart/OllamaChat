namespace ChatClient.Domain.Models;

public record SavedChatMessage(
    Guid Id,
    string Content,
    DateTime MsgDateTime,
    AppChatRole Role,
    string? AgentId,
    string? AgentName);

