using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Client.ViewModels;

public class ChatMessageViewModel
{
    public Guid Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
    public DateTime MsgDateTime { get; set; }
    public ChatRole Role { get; set; }
    public string? Statistics { get; set; }
    public bool IsStatsVisible { get; set; }
    public bool IsStreaming { get; set; }

    public bool IsAssistantStreaming => IsStreaming && Role == ChatRole.Assistant;

    public static ChatMessageViewModel FromDomainModel(IAppChatMessage message)
    {
        return new ChatMessageViewModel
        {
            Id = message.Id,
            Content = message.Content,
            HtmlContent = message.HtmlContent,
            MsgDateTime = message.MsgDateTime,
            Role = message.Role,
            Statistics = message.Statistics,
            IsStatsVisible = false,
            IsStreaming = message.IsStreaming
        };
    }
}
