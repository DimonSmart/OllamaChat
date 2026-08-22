using ChatClient.Domain.Models;
using System.Globalization;

namespace ChatClient.Api.Client.Services.Agentic;

public static class ChatRunUsageFormatter
{
    public static string? Format(ChatRunUsage? usage)
    {
        if (usage is null || usage.LlmCalls <= 0)
            return null;

        var parts = new List<string>();
        if (usage.InputTokens is { } input && usage.OutputTokens is { } output && usage.TotalTokens is { } total)
            parts.Add($"↑ {input.ToString("N0", CultureInfo.InvariantCulture)}  ↓ {output.ToString("N0", CultureInfo.InvariantCulture)}  Σ {total.ToString("N0", CultureInfo.InvariantCulture)}");

        parts.Add($"{usage.LlmCalls} LLM {(usage.LlmCalls == 1 ? "call" : "calls")}");
        parts.Add(FormatDuration(usage.Duration));
        return string.Join(" · ", parts);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
            : $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
    }
}
