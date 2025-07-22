using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using DimonSmart.AiUtils;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Services;

public class ChatHistoryBuilder(IUserSettingsService settingsService)
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

    public async Task<ChatHistory> BuildForChatAsync(IEnumerable<IAppChatMessage> messages, Kernel kernel, CancellationToken cancellationToken)
    {
        var history = BuildBaseHistory(messages);
        return await ApplyHistoryModeAsync(history, kernel, cancellationToken);
    }

    public async Task<ChatHistory> BuildForAgentAsync(ChatHistory baseHistory, string instructions, Kernel kernel, CancellationToken cancellationToken)
    {
        var history = new ChatHistory();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            history.AddSystemMessage(instructions);
        }
        foreach (var message in baseHistory.Where(m => m.Role != AuthorRole.System))
        {
            history.Add(message);
        }
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
                return new ChatHistory(await trunc.ReduceAsync(history, cancellationToken) ?? []);
            case ChatHistoryMode.Summarize:
                var sum = new ChatHistorySummarizationReducer(chatService, 5, 8);
                return new ChatHistory(await sum.ReduceAsync(history, cancellationToken) ?? []);
            default:
                return history;
        }
    }
}
