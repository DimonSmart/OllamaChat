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
        Assert.Equal(0, msg.FunctionCallStartIndex);
    }
}

