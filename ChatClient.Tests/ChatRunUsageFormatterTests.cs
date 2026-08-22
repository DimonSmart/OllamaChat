using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class ChatRunUsageFormatterTests
{
    [Fact]
    public void PreservesUsageWhenStreamingMessageCompletes()
    {
        var bridge = new AgenticChatEngineStreamingBridge();
        var stream = bridge.Create("agent", "Agent");
        var usage = new ChatRunUsage(100, 20, 120, 1, TimeSpan.FromSeconds(8.2));

        var message = bridge.Complete(stream, "answer", null, usage);

        Assert.Equal(usage, message.Usage);
    }

    [Fact]
    public void FormatsCompleteUsageSummary()
    {
        var summary = ChatRunUsageFormatter.Format(new(12431, 1284, 13715, 4, TimeSpan.FromSeconds(8.2)));

        Assert.Equal("↑ 12,431  ↓ 1,284  Σ 13,715 · 4 LLM calls · 8.2 s", summary);
    }

    [Fact]
    public void FormatsSingularCallAndLongDuration()
    {
        var summary = ChatRunUsageFormatter.Format(new(null, null, null, 1, TimeSpan.FromSeconds(72)));

        Assert.Equal("1 LLM call · 1m 12s", summary);
    }

    [Fact]
    public void OmitsUnknownSummary()
    {
        Assert.Null(ChatRunUsageFormatter.Format(null));
        Assert.Null(ChatRunUsageFormatter.Format(new(null, null, null, 0, TimeSpan.Zero)));
    }
}
