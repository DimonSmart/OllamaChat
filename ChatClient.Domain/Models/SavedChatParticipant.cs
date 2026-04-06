namespace ChatClient.Domain.Models;

public record SavedChatParticipant(
    string Id,
    string Name,
    AppChatRole Role);

