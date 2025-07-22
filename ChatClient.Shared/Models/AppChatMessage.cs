using System.Text.Json.Serialization;

using Microsoft.Extensions.AI;

namespace ChatClient.Shared.Models;

public class AppChatMessage : IAppChatMessage
{
    public Guid Id { get; set; }
    public string Content { get; set; }
    public DateTime MsgDateTime { get; set; }
    public ChatRole Role { get; set; }
    public string? Statistics { get; set; }
    public bool IsCanceled { get; set; }
    public IReadOnlyList<ChatMessageFile> Files { get; set; } = [];
    public IReadOnlyCollection<FunctionCallRecord> FunctionCalls { get; set; } = [];
    public bool IsStreaming => false;

    public bool Equals(IAppChatMessage? other)
    {
        if (other is null)
            return false;
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
    [JsonConstructor]
    public AppChatMessage()
    {
        Id = Guid.NewGuid();
        Content = string.Empty;
        MsgDateTime = DateTime.UtcNow;
        Role = ChatRole.User;
        Files = [];
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
        Files = message.Files;
        FunctionCalls = message.FunctionCalls;
    }

    public AppChatMessage(string content, DateTime msgDateTime, ChatRole role, string? statistics = null, IReadOnlyList<ChatMessageFile>? files = null, IReadOnlyCollection<FunctionCallRecord>? functionCalls = null)
    {
        Id = Guid.NewGuid();
        Content = content ?? string.Empty;
        MsgDateTime = msgDateTime;
        Role = role;
        Statistics = statistics;
        Files = files ?? [];
        FunctionCalls = functionCalls ?? [];
    }
}
