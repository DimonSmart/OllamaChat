using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

public sealed class ForceLastUserReducer : IChatHistoryReducer
{
    public Task<IEnumerable<ChatMessageContent>?> ReduceAsync(
        IReadOnlyList<ChatMessageContent> source,
        CancellationToken cancellationToken = default)
    {
        if (source.Count == 0)
        {
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(source);
        }

        List<ChatMessageContent> list = new(source);
        ChatMessageContent last = list[^1];

        if (last.Role != AuthorRole.User)
        {
            list[^1] = new ChatMessageContent(AuthorRole.User, last.Content, "user");
        }

        return Task.FromResult<IEnumerable<ChatMessageContent>?>(list);
    }
}
