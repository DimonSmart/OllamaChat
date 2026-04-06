namespace ChatClient.Domain.Models;

public sealed record AgentRunConversationMessage(
    AppChatRole Role,
    string Text);
