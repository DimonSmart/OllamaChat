using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using System.Text;

namespace ChatClient.Api.Client.Services.Agentic;

internal static class OrchestrationWorkflowConversationBuilder
{
    public static List<ChatMessage> BuildConversation(IEnumerable<IAppChatMessage> messages)
    {
        List<ChatMessage> result = [];

        foreach (var message in messages.Where(static message => !message.IsStreaming))
        {
            var content = message.Role == AppChatRole.User
                ? BuildUserMessage(message.Content ?? string.Empty, message.Files)
                : message.Content?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            result.Add(new ChatMessage(message.Role.ToAiChatRole(), content));
        }

        return result;
    }

    public static string BuildUserMessage(string text, IReadOnlyList<AppChatMessageFile>? files)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (files is null || files.Count == 0)
        {
            return trimmed;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(trimmed))
        {
            builder.AppendLine(trimmed);
            builder.AppendLine();
        }

        builder.AppendLine("Attached files:");
        foreach (var file in files)
        {
            builder.AppendLine($"- {file.Name} ({file.ContentType}, {file.Size} bytes)");
        }

        return builder.ToString().Trim();
    }
}
