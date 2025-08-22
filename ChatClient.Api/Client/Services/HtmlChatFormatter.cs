using System.Collections.Generic;
using System.Text;

using ChatClient.Api.Client.ViewModels;

using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services;

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
            var name = msg.Role == ChatRole.Assistant ? msg.AgentName ?? "Assistant" : "User";
            sb.Append("<p><strong>").Append(name).Append(":</strong> ").Append(msg.HtmlContent).AppendLine("</p>");
        }
        sb.AppendLine("</div>");
        return sb.ToString();
    }
}
