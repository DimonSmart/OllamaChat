using Markdig;
using Microsoft.Extensions.AI;

namespace ChatClient.Shared.Models;

public class AppChatMessage : IAppChatMessage
{
    /// <summary>
    /// Unique identifier for the message
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// The original text of the message (Markdown).
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// Cached HTML representation of the content created at initialization.
    /// </summary>
    public string HtmlContent { get; }

    /// <summary>
    /// The timestamp when the message was created.
    /// </summary>
    public DateTime MsgDateTime { get; }

    /// <summary>
    /// The role of the author (User, Assistant, or System).
    /// </summary>
    public ChatRole Role { get; }

    /// <summary>
    /// Chat statistics (call count, etc
    /// </summary>
    public string? Statistics { get; }

    /// <summary>
    /// Indicates whether this message is currently streaming.
    /// Always false for regular messages.
    /// </summary>
    public bool IsStreaming => false;

    public bool Equals(IAppChatMessage? other)
    {
        if (other is null) return false;
        return Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        return obj is IAppChatMessage other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }
    public AppChatMessage(string content, DateTime msgDateTime, ChatRole role, string? statistics = null)
    {
        Id = Guid.NewGuid();
        Content = content ?? string.Empty;
        HtmlContent = Markdown.ToHtml(Content);
        MsgDateTime = msgDateTime;
        Role = role;
        Statistics = statistics;
    }
}
