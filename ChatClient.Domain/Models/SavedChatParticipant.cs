using Microsoft.Extensions.AI;

namespace ChatClient.Domain.Models;

public record SavedChatParticipant(
    string Id,
    string Name,
    ChatRole Role);

