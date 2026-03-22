using System.Text;
using ChatClient.Api.Client.ViewModels;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services;

public static class ChatTranscriptFormatter
{
    public static string Format(IEnumerable<AppChatMessageViewModel> messages, ChatFormat format) =>
        format switch
        {
            ChatFormat.Text => FormatText(messages),
            ChatFormat.Markdown => FormatMarkdown(messages),
            ChatFormat.Html => FormatHtml(messages),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported chat transcript format.")
        };

    private static string FormatText(IEnumerable<AppChatMessageViewModel> messages)
    {
        var builder = new StringBuilder();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
                continue;

            builder.AppendLine($"{ResolveSpeakerName(message)}: {message.Content}");

            foreach (var thought in message.ThinkSegments.Where(static segment => !string.IsNullOrWhiteSpace(segment)))
            {
                builder.AppendLine($"  [thought] {thought}");
            }

            foreach (var call in message.FunctionCalls)
            {
                var callName = string.IsNullOrEmpty(call.Function) ? call.Server : $"{call.Server}.{call.Function}";
                builder.AppendLine($"  [tool] {callName}");

                if (!string.IsNullOrEmpty(call.Request))
                {
                    builder.AppendLine("  Request:");
                    builder.AppendLine(call.Request);
                }

                if (!string.IsNullOrEmpty(call.Response))
                {
                    builder.AppendLine("  Response:");
                    builder.AppendLine(call.Response);
                }
            }
        }

        return builder.ToString();
    }

    private static string FormatMarkdown(IEnumerable<AppChatMessageViewModel> messages)
    {
        var builder = new StringBuilder();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
                continue;

            builder.AppendLine($"**{ResolveSpeakerName(message)}:** {message.Content}");
            builder.AppendLine();

            foreach (var thought in message.ThinkSegments.Where(static segment => !string.IsNullOrWhiteSpace(segment)))
            {
                builder.AppendLine($"> [thought] {thought}");
                builder.AppendLine();
            }

            foreach (var call in message.FunctionCalls)
            {
                var callName = string.IsNullOrEmpty(call.Function) ? call.Server : $"{call.Server}.{call.Function}";
                builder.AppendLine($"**[tool] {callName}**");

                if (!string.IsNullOrEmpty(call.Request))
                {
                    builder.AppendLine("**Request:**");
                    builder.AppendLine("```");
                    builder.AppendLine(call.Request);
                    builder.AppendLine("```");
                }

                if (!string.IsNullOrEmpty(call.Response))
                {
                    builder.AppendLine("**Response:**");
                    builder.AppendLine("```");
                    builder.AppendLine(call.Response);
                    builder.AppendLine("```");
                }
            }
        }

        return builder.ToString();
    }

    private static string FormatHtml(IEnumerable<AppChatMessageViewModel> messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<div class=\"chat-transcript\">");

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
                continue;

            builder
                .Append("<p><strong>")
                .Append(ResolveSpeakerName(message))
                .Append(":</strong> ")
                .Append(message.HtmlContent)
                .AppendLine("</p>");

            foreach (var thought in message.HtmlThinkSegments.Where(static segment => !string.IsNullOrWhiteSpace(segment)))
            {
                builder.Append("<div class=\"think\"><em>").Append(thought).AppendLine("</em></div>");
            }

            foreach (var call in message.HtmlFunctionCalls.Where(static segment => !string.IsNullOrWhiteSpace(segment)))
            {
                builder.Append("<div class=\"function-call\">").Append(call).AppendLine("</div>");
            }
        }

        builder.AppendLine("</div>");
        return builder.ToString();
    }

    private static string ResolveSpeakerName(AppChatMessageViewModel message)
    {
        if (message.Role == ChatRole.Assistant)
            return message.AgentName ?? "Assistant";

        return message.Role == ChatRole.Tool
            ? "Tool"
            : "User";
    }
}
