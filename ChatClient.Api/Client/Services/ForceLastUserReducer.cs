using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

public sealed class ForceLastUserReducer(ILogger<ForceLastUserReducer>? logger = null) : IChatHistoryReducer
{
    public Task<IEnumerable<ChatMessageContent>?> ReduceAsync(
        IReadOnlyList<ChatMessageContent> source,
        CancellationToken cancellationToken = default)
    {
        if (source.Count == 0)
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(source);

        List<ChatMessageContent> list = new(source);
        ChatMessageContent last = list[^1];
        if (last.Role == AuthorRole.User)
        {
            logger?.LogDebug("ForceLastUserReducer found last role already User");
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(list);
        }

        logger?.LogInformation("ForceLastUserReducer changed last role from {OriginalRole} to User", last.Role);
        list[^1] = new ChatMessageContent(AuthorRole.User, last.Content, "user");
        return Task.FromResult<IEnumerable<ChatMessageContent>?>(list);
    }
}
