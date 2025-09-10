using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services.Reducers;

public sealed class MetaReducer(IReadOnlyList<IChatHistoryReducer> reducers) : IChatHistoryReducer
{
    public async Task<IEnumerable<ChatMessageContent>?> ReduceAsync(
        IReadOnlyList<ChatMessageContent> source,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChatMessageContent> current = source;

        foreach (var reducer in reducers)
        {
            var reduced = await reducer.ReduceAsync(current, cancellationToken);
            if (reduced is null)
                continue;
            current = reduced.ToList();
        }

        return current;
    }
}
