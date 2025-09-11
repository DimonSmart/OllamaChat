using DimonSmart.AiUtils;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services.Reducers;

public sealed class ThinkTagsRemovalReducer() : IChatHistoryReducer
{
    public Task<IEnumerable<ChatMessageContent>?> ReduceAsync(IReadOnlyList<ChatMessageContent> source, CancellationToken cancellationToken = default)
    {
        if (source.Count == 0)
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(source);

        foreach (var message in source)
        {
            // TODO: Check if mutation here is possible
            if (message.Content != null)
                message.Content = ThinkTagParser.ExtractThinkAnswer(message.Content).Answer;
        }

        return Task.FromResult<IEnumerable<ChatMessageContent>?>(source);
    }
}
