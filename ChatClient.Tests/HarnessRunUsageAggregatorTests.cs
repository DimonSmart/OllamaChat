using ChatClient.Api.Client.Services.Agentic;
using System.Diagnostics;

namespace ChatClient.Tests;

public sealed class HarnessRunUsageAggregatorTests
{
    private readonly HarnessRunUsageAggregator aggregator = new();
    private static readonly DateTimeOffset StartedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AggregatesOneProviderModelInvocation()
    {
        var usage = aggregator.Aggregate(Run(Span("chat", "100", "20")));

        Assert.Equal(100, usage!.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(120, usage.TotalTokens);
        Assert.Equal(1, usage.LlmCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(8200), usage.Duration);
    }

    [Fact]
    public void AggregatesAllModelInvocationsIncludingBackgroundAgentSpans()
    {
        var usage = aggregator.Aggregate(Run(
            Span("main-chat", "100", "20"),
            Span("background-chat-1", "150", "30"),
            Span("background-chat-2", "200", "50")));

        Assert.Equal(450, usage!.InputTokens);
        Assert.Equal(100, usage.OutputTokens);
        Assert.Equal(550, usage.TotalTokens);
        Assert.Equal(3, usage.LlmCalls);
    }

    [Fact]
    public void DoesNotCountNestedOrchestrationSpanWithoutModelOperation()
    {
        var usage = aggregator.Aggregate(Run(
            Span("invoke_agent", "100", "20"),
            Span("chat", "100", "20")));

        Assert.Equal(1, usage!.LlmCalls);
        Assert.Equal(120, usage.TotalTokens);
    }

    [Fact]
    public void PreservesUnknownUsageWhenProviderDoesNotReportIt()
    {
        var usage = aggregator.Aggregate(Run(Span("chat", null, null)));

        Assert.Null(usage!.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.TotalTokens);
        Assert.Equal(1, usage.LlmCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(8200), usage.Duration);
    }

    [Fact]
    public void DoesNotUseSummedSpanDurationForRunDuration()
    {
        var usage = aggregator.Aggregate(Run(Span("chat", "100", "20"), Span("chat", "100", "20")));

        Assert.Equal(TimeSpan.FromMilliseconds(8200), usage!.Duration);
    }

    [Theory]
    [InlineData(HarnessTraceRunStatus.Canceled)]
    [InlineData(HarnessTraceRunStatus.Failed)]
    public void RetainsAvailableUsageForUnsuccessfulRuns(HarnessTraceRunStatus status)
    {
        var usage = aggregator.Aggregate(Run(status, Span("chat", "100", "20")));

        Assert.Equal(1, usage!.LlmCalls);
        Assert.Equal(120, usage.TotalTokens);
    }

    private static HarnessTraceRunSnapshot Run(params HarnessTraceSpanSnapshot[] spans) =>
        Run(HarnessTraceRunStatus.Completed, spans);

    private static HarnessTraceRunSnapshot Run(HarnessTraceRunStatus status, params HarnessTraceSpanSnapshot[] spans) =>
        new("run", StartedAt, StartedAt.AddMilliseconds(8200), status, false, "trace", ["trace"], spans);

    private static HarnessTraceSpanSnapshot Span(string displayName, string? input, string? output)
    {
        var tags = new List<HarnessTraceAttribute>();
        if (displayName == "chat" || displayName.Contains("background-chat", StringComparison.Ordinal) || displayName == "main-chat")
            tags.Add(new("gen_ai.operation.name", "chat"));
        if (input is not null)
            tags.Add(new("gen_ai.usage.input_tokens", input));
        if (output is not null)
            tags.Add(new("gen_ai.usage.output_tokens", output));
        return new("trace", Guid.NewGuid().ToString("N"), string.Empty, displayName, "OllamaChat.Harness", ActivityKind.Client,
            StartedAt, TimeSpan.FromSeconds(30), ActivityStatusCode.Ok, null, tags, [], false, []);
    }
}
