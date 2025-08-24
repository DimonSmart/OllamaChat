using ChatClient.Api.Client.ViewModels;
using Microsoft.Extensions.AI;
using System.Collections.Generic;
using System.Text;

namespace ChatClient.Api.Client.Services.Formatters;

public class TextChatFormatter : IChatFormatter
{
    public ChatFormat FormatType => ChatFormat.Text;

    public string Format(IEnumerable<AppChatMessageViewModel> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
                continue;

            var name = msg.Role == ChatRole.Assistant ? msg.AgentName ?? "Assistant" : "User";
            sb.AppendLine($"{name}: {msg.Content}");
        }

        return sb.ToString();
    }
}
