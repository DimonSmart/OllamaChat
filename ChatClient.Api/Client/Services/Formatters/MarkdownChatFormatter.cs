using ChatClient.Api.Client.ViewModels;
using Microsoft.Extensions.AI;
using System.Text;

namespace ChatClient.Api.Client.Services.Formatters;

public class MarkdownChatFormatter : IChatFormatter
{
    public ChatFormat FormatType => ChatFormat.Markdown;

    public string Format(IEnumerable<AppChatMessageViewModel> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
                continue;
            var name = msg.Role == ChatRole.Assistant ? msg.AgentName ?? "Assistant" : "User";
            sb.AppendLine($"**{name}:** {msg.Content}\n");
        }
        return sb.ToString();
    }
}
