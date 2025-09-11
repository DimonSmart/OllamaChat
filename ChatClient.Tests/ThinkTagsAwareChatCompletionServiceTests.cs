using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.Services.Reducers;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Moq;

namespace ChatClient.Tests;

public class ThinkTagsAwareChatCompletionServiceTests
{
    [Fact]
    public async Task AppliesBothReducers_InCorrectOrder()
    {
        // Arrange
        var mockInner = new Mock<IChatCompletionService>();
        mockInner.Setup(x => x.GetChatMessageContentsAsync(
            It.IsAny<ChatHistory>(),
            It.IsAny<PromptExecutionSettings>(),
            It.IsAny<Kernel>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ChatMessageContent(AuthorRole.Assistant, "Mock response")]);

        var thinkTagsReducer = new ThinkTagsRemovalReducer();
        var forceLastUserReducer = new AppForceLastUserReducer();

        var service = new ThinkTagsAwareChatCompletionService(
            mockInner.Object,
            thinkTagsReducer,
            forceLastUserReducer);

        ChatHistory history =
        [
            new ChatMessageContent(AuthorRole.User, "<think>User thinking</think>User question", "user"),
            new ChatMessageContent(AuthorRole.Assistant, "<think>Assistant thinking</think>Assistant response", "assistant")
        ];

        // Act
        var result = await service.GetChatMessageContentsAsync(history);

        // Assert
        Assert.Single(result);
        Assert.Equal("Mock response", result[0].Content);

        // Verify that the inner service was called with processed history
        mockInner.Verify(x => x.GetChatMessageContentsAsync(
            It.Is<ChatHistory>(h =>
                h.Count == 2 &&
                h.Last().Role == AuthorRole.User && // Force last user applied
                h.Last().Content == "Assistant response" && // Think tags removed
                h.First().Content == "User question"), // Think tags removed from first message too
            It.IsAny<PromptExecutionSettings>(),
            It.IsAny<Kernel>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StreamingAppliesBothReducers()
    {
        // Arrange
        var mockInner = new Mock<IChatCompletionService>();
        var streamingContent = new StreamingChatMessageContent(AuthorRole.Assistant, "Stream");

        mockInner.Setup(x => x.GetStreamingChatMessageContentsAsync(
            It.IsAny<ChatHistory>(),
            It.IsAny<PromptExecutionSettings>(),
            It.IsAny<Kernel>(),
            It.IsAny<CancellationToken>()))
            .Returns(CreateAsyncEnumerable([streamingContent]));

        var thinkTagsReducer = new ThinkTagsRemovalReducer();
        var forceLastUserReducer = new AppForceLastUserReducer();

        var service = new ThinkTagsAwareChatCompletionService(
            mockInner.Object,
            thinkTagsReducer,
            forceLastUserReducer);

        ChatHistory history =
        [
            new ChatMessageContent(AuthorRole.Assistant, "<think>Some thinking</think>Final answer", "assistant")
        ];

        // Act
        var results = new List<StreamingChatMessageContent>();
        await foreach (var item in service.GetStreamingChatMessageContentsAsync(history))
        {
            results.Add(item);
        }

        // Assert
        Assert.Single(results);
        Assert.Equal("Stream", results[0].Content);

        // Verify the processing was applied
        mockInner.Verify(x => x.GetStreamingChatMessageContentsAsync(
            It.Is<ChatHistory>(h =>
                h.Count == 1 &&
                h.Last().Role == AuthorRole.User && // Force last user applied
                h.Last().Content == "Final answer"), // Think tags removed
            It.IsAny<PromptExecutionSettings>(),
            It.IsAny<Kernel>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}
