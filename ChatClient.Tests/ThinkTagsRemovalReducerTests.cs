using ChatClient.Api.Client.Services.Reducers;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Tests;

public class ThinkTagsRemovalReducerTests
{
    [Fact]
    public async Task RemovesThinkTags_FromSingleMessage()
    {
        // Arrange
        var originalMessage = new ChatMessageContent(AuthorRole.User, "<think>This is my thinking process</think>This is my actual response")
        {
            AuthorName = "user"
        };
        ChatHistory history = [originalMessage];

        var reducer = new ThinkTagsRemovalReducer();

        // Act
        var reduced = await reducer.ReduceAsync(history);

        // Assert
        var processedMessage = reduced!.First();
        Assert.Equal("This is my actual response", processedMessage.Content);
        Assert.Equal(AuthorRole.User, processedMessage.Role);
        Assert.Equal("user", processedMessage.AuthorName);
    }

    [Fact]
    public async Task PreservesMessages_WithoutThinkTags()
    {
        // Arrange
        ChatHistory history =
        [
            new ChatMessageContent(AuthorRole.User, "Just a normal message", "user"),
            new ChatMessageContent(AuthorRole.Assistant, "A normal response", "assistant")
        ];

        var reducer = new ThinkTagsRemovalReducer();

        // Act
        var reduced = await reducer.ReduceAsync(history);

        // Assert
        Assert.Equal(2, reduced!.Count());
        Assert.Equal("Just a normal message", reduced!.First().Content);
        Assert.Equal("A normal response", reduced!.Last().Content);
    }

    [Fact]
    public async Task HandlesEmptyContent()
    {
        // Arrange
        ChatHistory history =
        [
            new ChatMessageContent(AuthorRole.User, "", "user"),
            new ChatMessageContent(AuthorRole.Assistant, (string?)null, "assistant")
        ];

        var reducer = new ThinkTagsRemovalReducer();

        // Act
        var reduced = await reducer.ReduceAsync(history);

        // Assert
        Assert.Equal(2, reduced!.Count());
        Assert.Equal("", reduced!.First().Content);
        Assert.Null(reduced!.Last().Content);
    }

    [Fact]
    public async Task RemovesThinkTags_FromMultipleMessages()
    {
        // Arrange
        ChatHistory history =
        [
            new ChatMessageContent(AuthorRole.User, "<think>User thinking</think>User question", "user"),
            new ChatMessageContent(AuthorRole.Assistant, "<think>Assistant thinking</think>Assistant answer", "assistant"),
            new ChatMessageContent(AuthorRole.User, "Follow-up without tags", "user")
        ];

        var reducer = new ThinkTagsRemovalReducer();

        // Act
        var reduced = await reducer.ReduceAsync(history);

        // Assert
        var messages = reduced!.ToList();
        Assert.Equal(3, messages.Count);
        Assert.Equal("User question", messages[0].Content);
        Assert.Equal("Assistant answer", messages[1].Content);
        Assert.Equal("Follow-up without tags", messages[2].Content);
    }

    [Fact]
    public async Task HandlesEmptyHistory()
    {
        // Arrange
        ChatHistory history = [];
        var reducer = new ThinkTagsRemovalReducer();

        // Act
        var reduced = await reducer.ReduceAsync(history);

        // Assert
        Assert.NotNull(reduced);
        Assert.Empty(reduced);
    }
}
