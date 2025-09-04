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

            string name;
            if (msg.Role == ChatRole.Assistant)
                name = msg.AgentName ?? "Assistant";
            else if (msg.Role == ChatRole.Tool)
                name = "Tool";
            else
                name = "User";

            sb.AppendLine($"{name}: {msg.Content}");

            foreach (var thought in msg.ThinkSegments)
            {
                sb.AppendLine($"  💭 {thought}");
            }

            foreach (var call in msg.FunctionCalls)
            {
                var callName = string.IsNullOrEmpty(call.Function) ? call.Server : $"{call.Server}.{call.Function}";
                sb.AppendLine($"  🛠 {callName}");
                if (!string.IsNullOrEmpty(call.Request))
                {
                    sb.AppendLine("  Request:");
                    sb.AppendLine(call.Request);
                }
                if (!string.IsNullOrEmpty(call.Response))
                {
                    sb.AppendLine("  Response:");
                    sb.AppendLine(call.Response);
                }
            }
        }

        return sb.ToString();
    }
}
