using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

public class AppStreamingMessageManagerTests
{

    [Fact]
    public void CancelStreaming_WithContent_AppendsCancelMessage()
    {
        var manager = new AppStreamingMessageManager();
        var streamingMessage = new StreamingAppChatMessage("Hello", DateTime.Now, ChatRole.Assistant);
        streamingMessage.Append(" world");

        var canceledMessage = manager.CancelStreaming(streamingMessage);

        Assert.True(canceledMessage.IsCanceled);
        Assert.Contains("Hello world", canceledMessage.Content);
    }

    [Fact]
    public void CompleteStreaming_PreservesCanceledStatus()
    {
        var manager = new AppStreamingMessageManager();
        var streamingMessage = new StreamingAppChatMessage("Hello", DateTime.Now, ChatRole.Assistant);
        streamingMessage.SetCanceled();

        var finalMessage = manager.CompleteStreaming(streamingMessage, "stats");

        Assert.True(finalMessage.IsCanceled);
        Assert.Equal("Hello", finalMessage.Content);
        Assert.Equal("stats", finalMessage.Statistics);
    }

    [Fact]
    public void CompleteStreaming_PreservesAgentName()
    {
        var manager = new AppStreamingMessageManager();
        var streamingMessage = new StreamingAppChatMessage("Hello", DateTime.Now, ChatRole.Assistant, agentName: "Agent1");

        var finalMessage = manager.CompleteStreaming(streamingMessage);

        Assert.Equal("Agent1", finalMessage.AgentName);
    }

}
