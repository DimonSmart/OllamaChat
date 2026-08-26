using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class AgenticChatEngineStreamingBridgeTests
{
    [Fact]
    public void Complete_PreservesAccumulatedContentAndKeepsStatisticsUsageAndSpeakerSeparate()
    {
        var bridge = new AgenticChatEngineStreamingBridge();
        var stream = bridge.Create("writer", "Writer");
        bridge.Append(stream, "First ");
        bridge.Append(stream, "response");
        var usage = new ChatRunUsage(10, 20, 30, 2, TimeSpan.FromSeconds(3));

        var final = bridge.Complete(stream, stream.Content, "technical metadata", usage);

        Assert.Equal("First response", final.Content);
        Assert.Equal("technical metadata", final.Statistics);
        Assert.NotEqual(final.Content, final.Statistics);
        Assert.Equal(usage, final.Usage);
        Assert.Equal("writer", final.AgentId);
        Assert.Equal("Writer", final.AgentName);
    }
}
