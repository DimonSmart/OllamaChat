using Microsoft.Extensions.AI;
using System.Text;

namespace ChatClient.Shared.Models;

public class StreamingAppChatMessage : IAppChatMessage
{
    private readonly StringBuilder _contentBuilder;

    public string Content => _contentBuilder.ToString();
    public DateTime MsgDateTime { get; private set; }
    public ChatRole Role { get; private set; }

    public StreamingAppChatMessage(string initialContent, DateTime msgDateTime, ChatRole role)
    {
        _contentBuilder = new StringBuilder(initialContent);
        MsgDateTime = msgDateTime;
        Role = role;
    }

    public void Append(string? text)
    {
        _contentBuilder.Append(text);
    }
}