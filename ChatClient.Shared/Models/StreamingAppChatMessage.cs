using Microsoft.Extensions.AI;
using System.Text;

namespace ChatClient.Shared.Models;

public class StreamingAppChatMessage(string initialContent, DateTime msgDateTime, ChatRole role) : IAppChatMessage
{
    private readonly StringBuilder _contentBuilder = new(initialContent);

    public string Content => _contentBuilder.ToString();
    public DateTime MsgDateTime { get; private set; } = msgDateTime;
    public ChatRole Role { get; private set; } = role;

    public void Append(string? text)
    {
        _contentBuilder.Append(text);
    }
}