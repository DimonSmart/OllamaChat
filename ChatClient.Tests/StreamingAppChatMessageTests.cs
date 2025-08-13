using System;

using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

public class StreamingAppChatMessageTests
{
    [Fact]
    public void NewMessage_HasDefaultValues()
    {
        var msg = new StreamingAppChatMessage("hello", DateTime.Now, ChatRole.Assistant);

        Assert.Equal(0, msg.ApproximateTokenCount);
    }

    [Fact]
    public void ResetContent_ClearsPlaceholder()
    {
        var msg = new StreamingAppChatMessage("...", DateTime.Now, ChatRole.Assistant);
        msg.ResetContent();
        msg.Append("hi");

        Assert.Equal("hi", msg.Content);
    }

    [Fact]
    public void SetAgentName_UpdatesName()
    {
        var msg = new StreamingAppChatMessage(string.Empty, DateTime.Now, ChatRole.Assistant);
        msg.SetAgentName("Agent");

        Assert.Equal("Agent", msg.AgentName);
    }
}

