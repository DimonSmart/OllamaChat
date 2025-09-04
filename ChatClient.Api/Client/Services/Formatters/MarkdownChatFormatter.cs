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

            string name;
            if (msg.Role == ChatRole.Assistant)
                name = msg.AgentName ?? "Assistant";
            else if (msg.Role == ChatRole.Tool)
                name = "Tool";
            else
                name = "User";

            sb.AppendLine($"**{name}:** {msg.Content}\n");

            foreach (var thought in msg.ThinkSegments)
            {
                sb.AppendLine($"> 💭 {thought}\n");
            }

            foreach (var call in msg.FunctionCalls)
            {
                var callName = string.IsNullOrEmpty(call.Function) ? call.Server : $"{call.Server}.{call.Function}";
                sb.AppendLine($"**🛠 {callName}**  ");
                if (!string.IsNullOrEmpty(call.Request))
                {
                    sb.AppendLine("**Request:**");
                    sb.AppendLine("```");
                    sb.AppendLine(call.Request);
                    sb.AppendLine("```");
                }
                if (!string.IsNullOrEmpty(call.Response))
                {
                    sb.AppendLine("**Response:**");
                    sb.AppendLine("```");
                    sb.AppendLine(call.Response);
                    sb.AppendLine("```");
                }
            }
        }
        return sb.ToString();
    }
}
