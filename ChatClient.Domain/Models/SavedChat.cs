namespace ChatClient.Domain.Models;

public record SavedChat(
    Guid Id,
    string Title,
    DateTime SavedAt,
    IReadOnlyList<SavedChatMessage> Messages,
    IReadOnlyCollection<SavedChatParticipant> Participants);

