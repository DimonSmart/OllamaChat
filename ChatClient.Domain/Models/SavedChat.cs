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
}

