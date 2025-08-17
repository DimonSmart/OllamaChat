using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;

namespace ChatClient.Api.Client.Services;

public sealed class ForceLastUserReducer : IChatHistoryReducer
{
    public Task<IEnumerable<ChatMessageContent>?> ReduceAsync(
        IReadOnlyList<ChatMessageContent> source,
        CancellationToken cancellationToken = default)
    {
        if (source.Count == 0)
            return Task.FromResult<IEnumerable<ChatMessageContent>?>(source);

        var list = new List<ChatMessageContent>(source);
        var last = list[^1];

        if (last.Role != AuthorRole.User)
        {
            var items = new ChatMessageContentItemCollection();
            foreach (var item in last.Items)
                items.Add(item);

            var replaced = new ChatMessageContent(AuthorRole.User, items, "user");

            list[^1] = replaced;
        }

        return Task.FromResult<IEnumerable<ChatMessageContent>?>(list);
    }
}
