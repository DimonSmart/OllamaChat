namespace ChatClient.Domain.Models;

public record SavedChat(
    Guid Id,
    string Title,
    DateTime SavedAt,
    IReadOnlyList<SavedChatMessage> Messages,
    IReadOnlyCollection<SavedChatParticipant> Participants)
{
    public string? RuntimeDefinitionKind { get; init; }

    public string? RuntimeDefinitionId { get; init; }

    public Dictionary<string, string> RuntimeInputs { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentSessionOverridesSnapshot? RuntimeOverrides { get; init; }
}

public sealed record AgentSessionOverridesSnapshot
{
    public IReadOnlyList<McpServerSessionBinding>? McpServerBindings { get; init; }
}

