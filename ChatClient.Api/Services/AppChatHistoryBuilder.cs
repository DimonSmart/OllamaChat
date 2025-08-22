using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using DimonSmart.AiUtils;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Linq;
using ChatClient.Api.Client.Services;

namespace ChatClient.Api.Services;

public interface IAppChatHistoryBuilder
{
    Task<ChatHistory> BuildChatHistoryAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, CancellationToken cancellationToken);
}

public class AppChatHistoryBuilder(
    IUserSettingsService settingsService,
    ILogger<AppChatHistoryBuilder> logger,
    AppForceLastUserReducer reducer) : IAppChatHistoryBuilder
{
    public ChatHistory BuildBaseHistory(IEnumerable<IAppChatMessage> messages)
    {
        var history = new ChatHistory();
        foreach (var msg in messages)
        {
            var items = new ChatMessageContentItemCollection();
            if (!string.IsNullOrEmpty(msg.Content))
            {
                var answer = ThinkTagParser.ExtractThinkAnswer(msg.Content).Answer;
                items.Add(new Microsoft.SemanticKernel.TextContent(answer));
            }
            foreach (var file in msg.Files)
            {
                if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new ImageContent(new BinaryData(file.Data), file.ContentType));
                }
                else
                {
                    string fileDescription = $"File: {file.Name} ({file.ContentType})";
                    items.Add(new Microsoft.SemanticKernel.TextContent(fileDescription));
                }
            }

            AuthorRole role;
            if (msg.Role == Microsoft.Extensions.AI.ChatRole.System)
            {
                role = AuthorRole.System;
            }
            else if (msg.Role == Microsoft.Extensions.AI.ChatRole.Assistant)
            {
                role = AuthorRole.Assistant;
            }
            else
            {
                role = AuthorRole.User;
            }
            // Semantic Kernel's ChatMessageContent accepts an optional name parameter
            // that we use to preserve the agent identity for each message.
            history.Add(new ChatMessageContent(role, items, msg.AgentName));
        }
        return history;
    }

    public async Task<ChatHistory> BuildChatHistoryAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, CancellationToken cancellationToken)
    {
        var messageList = messages.ToList();
        logger.LogInformation("Building chat history from {MessageCount} messages", messageList.Count);
        var history = BuildBaseHistory(messageList);
        var initialRole = history.LastOrDefault()?.Role;
        logger.LogDebug("Initial history last role: {Role}", initialRole);

        var reduced = await reducer.ReduceAsync(history, cancellationToken) ?? history;
        history = reduced is ChatHistory h ? h : new ChatHistory(reduced);
        var finalRole = history.LastOrDefault()?.Role;
        logger.LogDebug("Final history last role: {Role}", finalRole);
        if (finalRole != AuthorRole.User)
            logger.LogWarning("Final history last role is {Role}, expected User", finalRole);

        logger.LogDebug("Chat history:\n{History}", FormatHistory(history));
        // Temporarily disabled!!!
        // history = await ApplyHistoryModeAsync(history, kernel, cancellationToken);
        return history;
    }

    private async Task<ChatHistory> ApplyHistoryModeAsync(ChatHistory history, Kernel kernel, CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetSettingsAsync();
        switch (settings.ChatHistoryMode)
        {
            case AppChatHistoryMode.Truncate:
                var trunc = new ChatHistoryTruncationReducer(5, 8);
                var truncated = await trunc.ReduceAsync(history, cancellationToken);
                return truncated is not null ? new ChatHistory(truncated) : history;
            case AppChatHistoryMode.Summarize:
                var chatService = kernel.GetRequiredService<IChatCompletionService>();
                var sum = new ChatHistorySummarizationReducer(chatService, 5, 8);
                var summarized = await sum.ReduceAsync(history, cancellationToken);
                return summarized is not null ? new ChatHistory(summarized) : history;
            default:
                return history;
        }
    }

    private static string FormatHistory(ChatHistory history) => string.Join("\n",
        history.Select(m =>
        {
            var text = string.Join(" ", m.Items.OfType<TextContent>().Select(t => t.Text));
            return $"{m.Role} ({m.AuthorName}): {text}";
        }));
}
