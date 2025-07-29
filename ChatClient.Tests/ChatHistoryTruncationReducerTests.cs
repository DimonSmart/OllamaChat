using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Tests;

public class ChatHistoryTruncationReducerTests
{
    [Fact]
    public async Task ReduceAsync_ShortHistory_ReturnsNull()
    {
        var reducer = new ChatHistoryTruncationReducer(5, 8);
        var history = new ChatHistory();
        history.AddSystemMessage("sys");
        history.AddUserMessage("hi");
        history.AddAssistantMessage("hello");

        var result = await reducer.ReduceAsync(history);

        Assert.Null(result);
    }
}
