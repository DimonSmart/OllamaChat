using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services.Reducers;

public sealed class AppForceLastUserReducer() : IChatHistoryReducer
{
    public Task<IEnumerable<ChatMessageContent>?> ReduceAsync(
        IReadOnlyList<ChatMessageContent> source,
        CancellationToken cancellationToken = default)
    {
        if (source.Count == 0)
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(source);

        var filteredMessages = source.Where(m =>
            !string.IsNullOrEmpty(m.Content) || !string.IsNullOrEmpty(m.AuthorName))
            .ToList();

        if (filteredMessages.Count == 0)
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(filteredMessages);

        var last = filteredMessages[^1];

        if (last.Role == AuthorRole.User)
        {
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(filteredMessages);
        }


        filteredMessages[^1].Role = AuthorRole.User; // = new ChatMessageContent(AuthorRole.User, last.Content, "user");
        return Task.FromResult<IEnumerable<ChatMessageContent>?>(filteredMessages);
    }
}
