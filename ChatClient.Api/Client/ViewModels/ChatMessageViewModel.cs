using ChatClient.Shared.Models;

using Markdig;

using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.ViewModels;

public class ChatMessageViewModel
{
    public Guid Id { get; set; }
    public string Content { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
    public IReadOnlyCollection<string> ThinkSegments { get; set; } = [];
    public IReadOnlyCollection<string> HtmlThinkSegments { get; set; } = [];
    public DateTime MsgDateTime { get; set; }
    public ChatRole Role { get; set; }
    public string? Statistics { get; set; }
    public bool IsStatsVisible { get; set; }
    public bool IsThoughtsVisible { get; set; }
    public bool IsStreaming { get; set; }
    public bool IsCanceled { get; set; }
    private ChatMessageViewModel Populate(IAppChatMessage message)
    {
        Id = message.Id;
        Content = message.Content;
        MsgDateTime = message.MsgDateTime;
        Role = message.Role;
        Statistics = message.Statistics;
        IsStreaming = message.IsStreaming;
        IsCanceled = message.IsCanceled;        // Extract think tags and answer using the DimonSmart.AiUtils parser
        var result = DimonSmart.AiUtils.ThinkTagParser.ExtractThinkAnswer(message.Content);
        // Set individual think segments
        ThinkSegments = result.ThoughtSegments;
        HtmlThinkSegments = result.ThoughtSegments
            .Select(segment => Markdown.ToHtml(segment))
            .ToList()
            .AsReadOnly();        // Set the cleaned answer content
        Content = result.Answer;
        HtmlContent = Markdown.ToHtml(result.Answer);

        return this;
    }

    public static ChatMessageViewModel CreateFromDomainModel(IAppChatMessage message) => new ChatMessageViewModel().Populate(message);

    public void UpdateFromDomainModel(IAppChatMessage message) => Populate(message);
}
