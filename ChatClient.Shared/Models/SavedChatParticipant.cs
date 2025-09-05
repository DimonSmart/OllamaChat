using Microsoft.Extensions.AI;

namespace ChatClient.Shared.Models;

public record SavedChatParticipant(
    string Id,
    string Name,
    ChatRole Role);

