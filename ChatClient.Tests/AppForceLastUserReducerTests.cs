using ChatClient.Api.Client.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Linq;
using Xunit;

namespace ChatClient.Tests;

public class AppForceLastUserReducerTests
{
    [Fact]
    public async Task ReplaceAssistantWithUser()
    {
        ChatHistory history =
        [
            new ChatMessageContent(AuthorRole.System, "sys", "system"),
            new ChatMessageContent(AuthorRole.Assistant, "answer", "assistant")
        ];

        var reducer = new AppForceLastUserReducer(new NullLogger<AppForceLastUserReducer>());
        var reduced = await reducer.ReduceAsync(history);
        var last = reduced!.Last();
        Assert.Equal(AuthorRole.User, last.Role);
    }
}
