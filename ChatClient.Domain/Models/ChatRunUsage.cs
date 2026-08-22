namespace ChatClient.Domain.Models;

public sealed record ChatRunUsage(
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    int LlmCalls,
    TimeSpan Duration);
