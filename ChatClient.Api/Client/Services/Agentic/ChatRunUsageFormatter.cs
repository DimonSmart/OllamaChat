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
            parts.Add($"↑ {FormatTokens(input)}  ↓ {FormatTokens(output)}  Σ {FormatTokens(total)}");

        parts.Add(FormatLlmCalls(usage.LlmCalls));
        parts.Add(FormatDuration(usage.Duration));
        return string.Join(" · ", parts);
    }

    public static string FormatTokens(long tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);

    public static string FormatLlmCalls(int calls) => $"{calls} LLM {(calls == 1 ? "call" : "calls")}";

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
            : $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
    }
}
