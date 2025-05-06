using ChatClient.Client.Utils;
using ChatClient.Shared.Models;
using Markdig;
using Microsoft.Extensions.AI;

namespace ChatClient.Client.ViewModels;

public class ChatMessageViewModel
{
    public Guid Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
    public string Think { get; set; } = string.Empty;
    public string HtmlThink { get; set; } = string.Empty;
    public DateTime MsgDateTime { get; set; }
    public ChatRole Role { get; set; }
    public string? Statistics { get; set; }
    public bool IsStatsVisible { get; set; }
    public bool IsThoughtsVisible { get; set; }
    public bool IsStreaming { get; set; }

    private ChatMessageViewModel Populate(IAppChatMessage message)
    {
        Id = message.Id;
        Content = message.Content;
        MsgDateTime = message.MsgDateTime;
        Role = message.Role;
        Statistics = message.Statistics;
        IsStreaming = message.IsStreaming;

        // Split think and answer
        var (think, answer) = ThoughtParser.SplitThinkAndAnswer(message.Content);
        Think = think;
        HtmlThink = string.IsNullOrEmpty(think)
            ? string.Empty
            : Markdown.ToHtml(think);

        Content = answer;
        HtmlContent = Markdown.ToHtml(answer);

        return this;
    }

    public static ChatMessageViewModel CreateFromDomainModel(IAppChatMessage message) => new ChatMessageViewModel().Populate(message);

    public void UpdateFromDomainModel(IAppChatMessage message) => Populate(message);
}
