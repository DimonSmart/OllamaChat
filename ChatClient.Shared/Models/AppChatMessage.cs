namespace ChatClient.Shared.Models;

/// <summary>
/// Сообщение в чате: роль (user/assistant/function) + текст.
/// </summary>
public record AppChatMessage(string Role, string Content);
