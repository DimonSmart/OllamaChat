namespace ChatClient.Domain.Models;

public sealed class SavedWorkflowDefinition
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string WorkflowId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string SourceCode { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public static class WorkflowDefinitionKinds
{
    public const string Handoff = "handoff";

    public const string Sequential = "sequential";

    public const string Concurrent = "concurrent";

    public const string GroupChat = "group-chat";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            Handoff => Handoff,
            Sequential => Sequential,
            Concurrent => Concurrent,
            GroupChat or "group chat" or "group_chat" or "groupchat" => GroupChat,
            null or "" => Handoff,
            _ => Handoff
        };
    }
}
