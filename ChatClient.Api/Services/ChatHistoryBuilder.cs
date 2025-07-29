using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using DimonSmart.AiUtils;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public interface IChatHistoryBuilder
{
    Task<ChatHistory> BuildChatHistoryAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, CancellationToken cancellationToken);
}

public class ChatHistoryBuilder(IUserSettingsService settingsService) : IChatHistoryBuilder
{
    public ChatHistory BuildBaseHistory(IEnumerable<IAppChatMessage> messages)
    {
        var history = new ChatHistory();
        foreach (var msg in messages.Where(m => !m.IsStreaming))
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
            history.Add(new ChatMessageContent(role, items));
        }
        return history;
    }

    public async Task<ChatHistory> BuildChatHistoryAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, CancellationToken cancellationToken)
    {
        var history = BuildBaseHistory(messages);
        return await ApplyHistoryModeAsync(history, kernel, cancellationToken);
    }

    private async Task<ChatHistory> ApplyHistoryModeAsync(ChatHistory history, Kernel kernel, CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetSettingsAsync();
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        switch (settings.ChatHistoryMode)
        {
            case ChatHistoryMode.Truncate:
                var trunc = new ChatHistoryTruncationReducer(5, 8);
                var truncated = await trunc.ReduceAsync(history, cancellationToken);
                return truncated is not null ? new ChatHistory(truncated) : history;
            case ChatHistoryMode.Summarize:
                var sum = new ChatHistorySummarizationReducer(chatService, 5, 8);
                var summarized = await sum.ReduceAsync(history, cancellationToken);
                return summarized is not null ? new ChatHistory(summarized) : history;
            default:
                return history;
        }
    }
}
