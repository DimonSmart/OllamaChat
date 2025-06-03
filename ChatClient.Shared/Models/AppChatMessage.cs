using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class AppChatMessage : IAppChatMessage
{
    /// <summary>
    /// Unique identifier for the message
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The original text of the message (Markdown).
    /// </summary>
    public string Content { get; set; }

    /// <summary>
    /// The timestamp when the message was created.
    /// </summary>
    public DateTime MsgDateTime { get; set; }

    /// <summary>
    /// The role of the author (User, Assistant, or System).
    /// </summary>
    public ChatRole Role { get; set; }
    /// <summary>
    /// Chat statistics (call count, tokens, etc.)
    /// </summary>
    public string? Statistics { get; set; }

    /// <summary>
    /// Indicates whether this message was canceled by the user.
    /// </summary>
    public bool IsCanceled { get; set; }

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

    /// <summary>
    /// Parameterless constructor for JSON deserialization
    /// </summary>
    [JsonConstructor]
    public AppChatMessage()
    {
        Id = Guid.NewGuid();
        Content = string.Empty;
        MsgDateTime = DateTime.UtcNow;
        Role = ChatRole.User;
    }    
        
    /// <summary>
    /// Creates a new AppChatMessage by copying an existing IAppChatMessage, preserving its Id.
    /// </summary>
    /// <param name="message">The source message to copy from</param>
    public AppChatMessage(IAppChatMessage message)
    {
        Id = message.Id;
        Content = message.Content;
        MsgDateTime = message.MsgDateTime;
        Role = message.Role;
        Statistics = message.Statistics;
        IsCanceled = message.IsCanceled;
    }

    public AppChatMessage(string content, DateTime msgDateTime, ChatRole role, string? statistics = null)
    {
        Id = Guid.NewGuid();
        Content = content ?? string.Empty;
        MsgDateTime = msgDateTime;
        Role = role;
        Statistics = statistics;
    }
}
