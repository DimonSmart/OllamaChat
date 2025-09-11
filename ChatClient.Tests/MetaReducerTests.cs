using ChatClient.Api.Client.Services.Reducers;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Moq;

namespace ChatClient.Tests;

public class MetaReducerTests
{
    [Fact]
    public async Task AppliesReducersInOrder()
    {
        // Arrange
        var mockReducer1 = new Mock<IChatHistoryReducer>();
        var mockReducer2 = new Mock<IChatHistoryReducer>();

        var originalHistory = new List<ChatMessageContent>
        {
            new(AuthorRole.User, "original message", "user")
        };

        var afterFirstReducer = new List<ChatMessageContent>
        {
            new(AuthorRole.User, "after first reducer", "user")
        };

        var afterSecondReducer = new List<ChatMessageContent>
        {
            new(AuthorRole.User, "after second reducer", "user")
        };

        mockReducer1.Setup(x => x.ReduceAsync(
            It.Is<IReadOnlyList<ChatMessageContent>>(list => list[0].Content == "original message"),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(afterFirstReducer);

        mockReducer2.Setup(x => x.ReduceAsync(
            It.Is<IReadOnlyList<ChatMessageContent>>(list => list[0].Content == "after first reducer"),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(afterSecondReducer);

        var metaReducer = new MetaReducer([mockReducer1.Object, mockReducer2.Object]);

        // Act
        var result = await metaReducer.ReduceAsync(originalHistory);

        // Assert
        Assert.Single(result!);
        Assert.Equal("after second reducer", result!.First().Content);

        mockReducer1.Verify(x => x.ReduceAsync(It.IsAny<IReadOnlyList<ChatMessageContent>>(), It.IsAny<CancellationToken>()), Times.Once);
        mockReducer2.Verify(x => x.ReduceAsync(It.IsAny<IReadOnlyList<ChatMessageContent>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandlesEmptyReducersList()
    {
        // Arrange
        var originalHistory = new List<ChatMessageContent>
        {
            new(AuthorRole.User, "test message", "user")
        };

        var metaReducer = new MetaReducer([]);

        // Act
        var result = await metaReducer.ReduceAsync(originalHistory);

        // Assert
        Assert.Same(originalHistory, result);
    }

    [Fact]
    public async Task HandlesNullResultFromReducer()
    {
        // Arrange
        var mockReducer1 = new Mock<IChatHistoryReducer>();
        var mockReducer2 = new Mock<IChatHistoryReducer>();

        var originalHistory = new List<ChatMessageContent>
        {
            new(AuthorRole.User, "original message", "user")
        };

        var afterSecondReducer = new List<ChatMessageContent>
        {
            new(AuthorRole.User, "after second reducer", "user")
        };

        mockReducer1.Setup(x => x.ReduceAsync(It.IsAny<IReadOnlyList<ChatMessageContent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessageContent>?)null);

        mockReducer2.Setup(x => x.ReduceAsync(It.IsAny<IReadOnlyList<ChatMessageContent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(afterSecondReducer);

        var metaReducer = new MetaReducer([mockReducer1.Object, mockReducer2.Object]);

        // Act
        var result = await metaReducer.ReduceAsync(originalHistory);

        // Assert
        Assert.Single(result!);
        Assert.Equal("after second reducer", result!.First().Content);
    }

    [Fact]
    public async Task WorksWithRealReducers()
    {
        // Arrange
        var thinkTagsReducer = new ThinkTagsRemovalReducer();
        var forceLastUserReducer = new AppForceLastUserReducer();

        var originalHistory = new List<ChatMessageContent>
        {
            new(AuthorRole.User, "<think>User thinking</think>User question", "user"),
            new(AuthorRole.Assistant, "<think>Assistant thinking</think>Assistant response", "assistant")
        };

        var metaReducer = new MetaReducer([thinkTagsReducer, forceLastUserReducer]);

        // Act
        var result = await metaReducer.ReduceAsync(originalHistory);

        // Assert
        var messages = result!.ToList();
        Assert.Equal(2, messages.Count);

        // First message should have think tags removed
        Assert.Equal("User question", messages[0].Content);
        Assert.Equal(AuthorRole.User, messages[0].Role);

        // Second message should have think tags removed AND role changed to User
        Assert.Equal("Assistant response", messages[1].Content);
        Assert.Equal(AuthorRole.User, messages[1].Role); // Force last user applied
    }
}
