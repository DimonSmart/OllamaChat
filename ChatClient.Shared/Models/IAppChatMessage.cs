using Microsoft.Extensions.AI;

namespace ChatClient.Shared.Models;

public interface IAppChatMessage
{
    string Content { get; }
    DateTime MsgDateTime { get; }
    ChatRole Role { get; }
    string HtmlContent { get; }
    string? Statistics { get; }
}
