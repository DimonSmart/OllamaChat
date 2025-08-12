using System.Collections.Generic;

using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

public class StreamingMessageManagerTests
{

    [Fact]
    public void CancelStreaming_WithContent_AppendsCancelMessage()
    {
        var manager = new StreamingMessageManager(null);
        var streamingMessage = new StreamingAppChatMessage("Hello", DateTime.Now, ChatRole.Assistant);
        streamingMessage.Append(" world");

        var canceledMessage = manager.CancelStreaming(streamingMessage);

        Assert.True(canceledMessage.IsCanceled);
        Assert.Contains("Hello world", canceledMessage.Content);
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

    [Fact]
    public void CompleteStreaming_PreservesAgentName()
    {
        var manager = new StreamingMessageManager(null);
        var streamingMessage = new StreamingAppChatMessage("Hello", DateTime.Now, ChatRole.Assistant, agentName: "Agent1");

        var finalMessage = manager.CompleteStreaming(streamingMessage);

        Assert.Equal("Agent1", finalMessage.AgentName);
    }

    [Fact]
    public async Task AppendToMessageAsync_MultipleStreams_UpdatesCorrectly()
    {
        List<IAppChatMessage> updated = [];
        var manager = new StreamingMessageManager((msg, _) =>
        {
            updated.Add(msg);
            return Task.CompletedTask;
        });

        var msg1 = manager.CreateStreamingMessage(agentName: "Agent1");
        var msg2 = manager.CreateStreamingMessage(agentName: "Agent2");

        await manager.AppendToMessageAsync(msg1.Id, "hi");
        await manager.AppendToMessageAsync(msg2.Id, "bye");

        Assert.Equal("hi", msg1.Content);
        Assert.Equal("bye", msg2.Content);
        Assert.Equal(2, updated.Count);
    }
}
