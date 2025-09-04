using ChatClient.Api.Client.ViewModels;
using Microsoft.Extensions.AI;
using System.Text;

namespace ChatClient.Api.Client.Services.Formatters;

public class HtmlChatFormatter : IChatFormatter
{
    public ChatFormat FormatType => ChatFormat.Html;

    public string Format(IEnumerable<AppChatMessageViewModel> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<div class=\"chat-transcript\">");
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

            sb.Append("<p><strong>").Append(name).Append(":</strong> ").Append(msg.HtmlContent).AppendLine("</p>");

            foreach (var html in msg.HtmlThinkSegments)
            {
                sb.Append("<div class=\"think\"><em>").Append(html).AppendLine("</em></div>");
            }

            foreach (var html in msg.HtmlFunctionCalls)
            {
                sb.Append("<div class=\"function-call\">").Append(html).AppendLine("</div>");
            }
        }
        sb.AppendLine("</div>");
        return sb.ToString();
    }
}
