using ChatClient.Api.Client.Services.Reducers;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Runtime.CompilerServices;

namespace ChatClient.Api.Client.Services;

/// <summary>
/// Chat completion service that applies both think tags removal and force last user logic.
/// This service creates a chain of reducers: first removes think tags, then ensures last message is from user.
/// </summary>
public sealed class ThinkTagsAwareChatCompletionService(
    IChatCompletionService inner,
    ThinkTagsRemovalReducer thinkTagsReducer,
    AppForceLastUserReducer forceLastUserReducer) : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes => inner.Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var history = await ApplyReducersAsync(chatHistory, cancellationToken);
        return await inner.GetChatMessageContentsAsync(history, executionSettings, kernel, cancellationToken);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = await ApplyReducersAsync(chatHistory, cancellationToken);

        await foreach (var item in inner.GetStreamingChatMessageContentsAsync(history, executionSettings, kernel, cancellationToken))
            yield return item;
    }

    private async Task<ChatHistory> ApplyReducersAsync(ChatHistory chatHistory, CancellationToken cancellationToken)
    {
        // First, remove think tags from all messages
        var withoutThinkTags = await thinkTagsReducer.ReduceAsync(chatHistory, cancellationToken) ?? chatHistory;
        var historyWithoutThinkTags = withoutThinkTags is ChatHistory h1 ? h1 : new ChatHistory(withoutThinkTags);

        // Then, ensure the last message is from user
        var withLastUserForced = await forceLastUserReducer.ReduceAsync(historyWithoutThinkTags, cancellationToken) ?? historyWithoutThinkTags;
        return withLastUserForced is ChatHistory h2 ? h2 : new ChatHistory(withLastUserForced);
    }
}
