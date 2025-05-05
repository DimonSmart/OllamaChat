using ChatClient.Shared.Models;
using Markdig;
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

    private ChatMessageViewModel Populate(IAppChatMessage message)
    {
        Id = message.Id;
        Content = message.Content;
        HtmlContent = Markdown.ToHtml(message.Content);
        MsgDateTime = message.MsgDateTime;
        Role = message.Role;
        Statistics = message.Statistics;
        IsStreaming = message.IsStreaming;
        return this;
    }

    public static ChatMessageViewModel CreateFromDomainModel(IAppChatMessage message)
    {
        return (new ChatMessageViewModel()).Populate(message);
      
    }

    public void UpdateFromDomainModel(IAppChatMessage message)
    {
        Populate(message);
    }
}
