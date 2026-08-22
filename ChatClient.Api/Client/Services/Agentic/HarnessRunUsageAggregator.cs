using ChatClient.Domain.Models;
using System.Globalization;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class HarnessRunUsageAggregator
{
    private const string OperationName = "gen_ai.operation.name";
    private const string InputTokens = "gen_ai.usage.input_tokens";
    private const string OutputTokens = "gen_ai.usage.output_tokens";
    private const string TotalTokens = "gen_ai.usage.total_tokens";

    public ChatRunUsage? Aggregate(HarnessTraceRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var calls = run.Spans.Where(IsModelInvocation).ToArray();
        if (calls.Length == 0)
            return null;

        var input = SumKnown(calls, InputTokens);
        var output = SumKnown(calls, OutputTokens);
        var total = SumKnown(calls, TotalTokens) ??
                    (input.HasValue && output.HasValue ? input + output : null);
        var duration = (run.CompletedAt ?? DateTimeOffset.UtcNow) - run.StartedAt;

        return new ChatRunUsage(input, output, total, calls.Length, duration < TimeSpan.Zero ? TimeSpan.Zero : duration);
    }

    private static bool IsModelInvocation(HarnessTraceSpanSnapshot span) =>
        TryGetTag(span, OperationName, out var operation) &&
        string.Equals(operation, "chat", StringComparison.OrdinalIgnoreCase);

    private static long? SumKnown(IEnumerable<HarnessTraceSpanSnapshot> spans, string tagName)
    {
        var values = spans.Select(span => TryGetTokenCount(span, tagName)).ToArray();
        return values.All(value => value.HasValue) ? values.Sum(value => value!.Value) : null;
    }

    private static long? TryGetTokenCount(HarnessTraceSpanSnapshot span, string tagName) =>
        TryGetTag(span, tagName, out var value) &&
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens) && tokens >= 0
            ? tokens
            : null;

    private static bool TryGetTag(HarnessTraceSpanSnapshot span, string key, out string value)
    {
        var tag = span.Tags.FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.Ordinal));
        value = tag?.Value ?? string.Empty;
        return tag is not null;
    }
}
