using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

public sealed class AppForceLastUserReducer(ILogger<AppForceLastUserReducer>? logger = null) : IChatHistoryReducer
{
    public Task<IEnumerable<ChatMessageContent>?> ReduceAsync(
        IReadOnlyList<ChatMessageContent> source,
        CancellationToken cancellationToken = default)
    {
        if (source.Count == 0)
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(source);

        // Filter out empty messages without proper AuthorName (streaming placeholders from GroupChat)
        var filteredMessages = source.Where(m =>
            !string.IsNullOrEmpty(m.Content) || !string.IsNullOrEmpty(m.AuthorName))
            .ToList();

        logger?.LogDebug("AppForceLastUserReducer processing {OriginalCount} messages ({FilteredCount} after filtering empty)",
            source.Count, filteredMessages.Count);

        if (filteredMessages.Count == 0)
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(filteredMessages);

        ChatMessageContent last = filteredMessages[^1];

        logger?.LogDebug("AppForceLastUserReducer last message: Role={LastRole}, Content='{LastContent}', AuthorName='{AuthorName}'",
            last.Role, last.Content?.Length > 50 ? last.Content[..50] + "..." : last.Content, last.AuthorName);

        if (last.Role == AuthorRole.User)
        {
            logger?.LogDebug("AppForceLastUserReducer found last role already User");
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(filteredMessages);
        }

        logger?.LogInformation("AppForceLastUserReducer changed last role from {OriginalRole} to User. Content: '{Content}'",
            last.Role, last.Content?.Length > 100 ? last.Content[..100] + "..." : last.Content);
        filteredMessages[^1] = new ChatMessageContent(AuthorRole.User, last.Content, "user");
        return Task.FromResult<IEnumerable<ChatMessageContent>?>(filteredMessages);
    }
}
