using Xunit;
using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;

public class StreamingMessageManagerTests
{
    [Fact]
    public void CancelStreaming_WithEmptyContent_ShowsCancelMessage()
    {
        var manager = new StreamingMessageManager(null);
        var streamingMessage = new StreamingAppChatMessage("", DateTime.Now, ChatRole.Assistant);

        var canceledMessage = manager.CancelStreaming(streamingMessage);

        Assert.True(canceledMessage.IsCanceled);
        Assert.Equal("[Message was canceled by user]", canceledMessage.Content);
    }
    [Fact]
    public void CancelStreaming_WithContent_AppendsCancelMessage()
    {
        var manager = new StreamingMessageManager(null);
        var streamingMessage = new StreamingAppChatMessage("Hello", DateTime.Now, ChatRole.Assistant);
        streamingMessage.Append(" world");

        var canceledMessage = manager.CancelStreaming(streamingMessage);

        Assert.True(canceledMessage.IsCanceled);
        Assert.Contains("Hello world", canceledMessage.Content);
        Assert.Contains("*[Message was canceled by user]*", canceledMessage.Content);
    }

    [Fact]
    public void CompleteStreaming_PreservesCanceledStatus()
    {
        var manager = new StreamingMessageManager(null);
        var streamingMessage = new StreamingAppChatMessage("Hello", DateTime.Now, ChatRole.Assistant);
        streamingMessage.SetCanceled();

        var finalMessage = manager.CompleteStreaming(streamingMessage, "stats");

        Assert.True(finalMessage.IsCanceled);
        Assert.Equal("Hello", finalMessage.Content);
        Assert.Equal("stats", finalMessage.Statistics);
    }
}
