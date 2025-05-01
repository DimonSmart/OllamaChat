using Microsoft.Extensions.AI;

namespace ChatClient.Shared.Models;

public record AppChatMessage(string Content, DateTime MsgDateTime, ChatRole Role) : IAppChatMessage;
