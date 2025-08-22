using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

public sealed class AppForceLastUserChatCompletionService(IChatCompletionService inner, AppForceLastUserReducer reducer) : IChatCompletionService
{
    public IReadOnlyDictionary<string, object?> Attributes => inner.Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var reduced = await reducer.ReduceAsync(chatHistory, cancellationToken) ?? chatHistory;
        var history = reduced is ChatHistory h ? h : new ChatHistory(reduced);
        return await inner.GetChatMessageContentsAsync(history, executionSettings, kernel, cancellationToken);
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reduced = await reducer.ReduceAsync(chatHistory, cancellationToken) ?? chatHistory;
        var history = reduced is ChatHistory h ? h : new ChatHistory(reduced);
        await foreach (var item in inner.GetStreamingChatMessageContentsAsync(history, executionSettings, kernel, cancellationToken))
            yield return item;
    }
}

