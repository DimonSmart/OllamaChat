using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public class AppChatMessage : IAppChatMessage
{
    public Guid Id { get; set; }
    public string Content { get; set; }
    public DateTime MsgDateTime { get; set; }
    public AppChatRole Role { get; set; }
    public string? AgentId { get; set; }
    public string? AgentName { get; set; }
    public string? Statistics { get; set; }
    public bool IsCanceled { get; set; }
    public IReadOnlyList<AppChatMessageFile> Files { get; set; } = [];
    public IReadOnlyCollection<ToolInvocationViewState> ToolInvocations { get; set; } = [];
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
        Role = AppChatRole.User;
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
        AgentId = message.AgentId;
        AgentName = message.AgentName;
        Statistics = message.Statistics;
        IsCanceled = message.IsCanceled;
        Files = message.Files;
        ToolInvocations = message.ToolInvocations.ToList();
    }

    public AppChatMessage(
        string content,
        DateTime msgDateTime,
        AppChatRole role,
        string? statistics = null,
        IReadOnlyList<AppChatMessageFile>? files = null,
        IReadOnlyCollection<ToolInvocationViewState>? toolInvocations = null,
        string? agentId = null,
        string? agentName = null)
    {
        Id = Guid.NewGuid();
        Content = content ?? string.Empty;
        MsgDateTime = msgDateTime;
        Role = role;
        AgentId = agentId;
        AgentName = agentName;
        Statistics = statistics;
        Files = files ?? [];
        ToolInvocations = toolInvocations ?? [];
    }
}
