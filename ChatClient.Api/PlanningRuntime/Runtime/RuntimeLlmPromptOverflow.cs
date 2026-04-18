using ChatClient.Api.PlanningRuntime.Common;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Runtime;

internal sealed record RuntimeLlmPromptCallMetadata(
    int PromptChars,
    bool TruncationAttempted,
    bool RetryAttempted,
    int TruncatedValueCount,
    IReadOnlyList<string> TruncatedInputNames);

internal static class RuntimeLlmPromptScope
{
    private static readonly AsyncLocal<RuntimeLlmPromptCallMetadata?> CurrentMetadata = new();

    public static RuntimeLlmPromptCallMetadata? Current => CurrentMetadata.Value;

    public static IDisposable Push(RuntimeLlmPromptCallMetadata metadata)
    {
        var previous = CurrentMetadata.Value;
        CurrentMetadata.Value = metadata;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(RuntimeLlmPromptCallMetadata? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentMetadata.Value = previous;
        }
    }
}

internal static class RuntimeLlmPromptOverflow
{
    public const string ErrorCode = "llm_context_length_exceeded";

    private static readonly string[] DetailMessages =
    [
        "LLM input exceeded the model context window after runtime prompt truncation."
    ];

    public static ResultEnvelope<JsonElement?> CreateFailure(RuntimeLlmPromptCallMetadata? metadata) =>
        ResultEnvelope<JsonElement?>.Failure(
            ErrorCode,
            "Runtime LLM input exceeded the model context window.",
            JsonSerializer.SerializeToElement(new
            {
                status = "blocked",
                needsReplan = true,
                type = "error",
                details = DetailMessages,
                retryable = false,
                providerCode = "context_length_exceeded",
                parameter = "messages",
                promptChars = metadata?.PromptChars ?? 0,
                truncationAttempted = metadata?.TruncationAttempted ?? false,
                retryAttempted = metadata?.RetryAttempted ?? false,
                truncatedValueCount = metadata?.TruncatedValueCount ?? 0,
                truncatedInputNames = metadata?.TruncatedInputNames ?? Array.Empty<string>()
            }));

    public static bool IsOverflow(ResultEnvelope<JsonElement?> envelope) =>
        !envelope.Ok && string.Equals(envelope.Error?.Code, ErrorCode, StringComparison.Ordinal);
}
