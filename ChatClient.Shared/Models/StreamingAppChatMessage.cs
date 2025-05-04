using Markdig;
using Microsoft.Extensions.AI;
using System.Text;

namespace ChatClient.Shared.Models;

public class StreamingAppChatMessage(string initialContent, DateTime msgDateTime, ChatRole role) : IAppChatMessage
{
    private readonly StringBuilder _contentBuilder = new(initialContent);
    public string Content => _contentBuilder.ToString();
    public DateTime MsgDateTime { get; private set; } = msgDateTime;
    public ChatRole Role { get; private set; } = role;
    public string HtmlContent => Markdown.ToHtml(Content);
    public string? Statistics { get; private set; } = string.Empty;

    public Guid Id { get; private set; } = Guid.NewGuid();

    public void Append(string? text)
    {
        _contentBuilder.Append(text);
    }

    public void SetStatistics(string stats)
    {
        Statistics = stats;
    }
}