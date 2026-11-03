using System.Diagnostics;

namespace ChatClient.Api.Client.Services.Agentic;

// Everything here describes the Microsoft.Extensions.AI client boundary, not the HTTP wire request.
public enum ContextCaptureMode { Off, MetadataOnly, FullContext }
public enum ContextInvocationStatus { Running, Completed, Canceled, Failed }
public enum ContextLinkState { Linked, Pending, Ambiguous, Unavailable }
public enum ContextCategory { Instructions, Conversation, ToolCalls, ToolResults, ToolDefinitions, InjectedContext, Other }
public enum ContextTokenQuality { HeuristicEstimated, Unknown }

public sealed record ContextInvocationSnapshot
{
    public required string InvocationId { get; init; }
    public string? RunId { get; init; }
    public required string AgentName { get; init; }
    public string? AgentInstanceId { get; init; }
    public required string Purpose { get; init; }
    public required string ModelId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public ContextInvocationStatus Status { get; init; } = ContextInvocationStatus.Running;
    public ContextCaptureMode CaptureMode { get; init; }
    public ContextLinkState LinkState { get; init; } = ContextLinkState.Unavailable;
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public long? ProviderInputTokens { get; init; }
    public long? ProviderOutputTokens { get; init; }
    public long? ProviderCachedInputTokens { get; init; }
    public long? ProviderReasoningTokens { get; init; }
    public long EstimatedTokens { get; init; }
    public int UnknownSegments { get; init; }
    public int SegmentCount { get; init; }
    public IReadOnlyDictionary<ContextCategory, long> CategoryTokens { get; init; }
        = new Dictionary<ContextCategory, long>();
    public bool IsTruncated { get; init; }
    public bool CaptureComplete { get; init; }
}

public sealed record ContextSegmentSnapshot
{
    public required string SegmentId { get; init; }
    public required string InvocationId { get; init; }
    public int Order { get; init; }
    public ContextCategory Category { get; init; }
    public required string Role { get; init; }
    public required string ContentKind { get; init; }
    public string? MessageId { get; init; }
    public string? CallId { get; init; }
    public string? ToolName { get; init; }
    public string? BlobId { get; init; }
    public bool IsStructured { get; init; }
    public long? EstimatedTokens { get; init; }
    public ContextTokenQuality Quality { get; init; }
    public bool IsTruncated { get; init; }
}

public sealed record ContextBlobSnapshot(string Content, bool IsStructured, bool IsTruncated);

internal sealed record ContextSegmentDraft(
    ContextCategory Category, string Role, string ContentKind, string? MessageId,
    string? CallId, string? ToolName, long? EstimatedTokens, string? Content,
    bool IsStructured);

public sealed record ContextCaptureLimits(
    int MaxRuns = 20,
    int MaxInvocationsPerRun = 250,
    int MaxSegmentsPerInvocation = 2000,
    long MaxFullContentBytes = 64L * 1024 * 1024,
    int MaxBlobBytes = 256 * 1024);

/// <summary>
/// Explicit run correlation flows with ExecutionContext into child agent calls.
/// It does not rely on mutable shared "current agent" state or timestamp matching.
/// </summary>
public static class ContextCaptureScope
{
    private static readonly AsyncLocal<string?> CurrentRun = new();
    public static string? RunId => CurrentRun.Value;

    public static IDisposable Begin(string runId)
    {
        var previous = CurrentRun.Value;
        CurrentRun.Value = runId;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CurrentRun.Value = previous;
        }
    }

    public static (ContextLinkState State, string? TraceId, string? SpanId) GetChatSpan()
    {
        var activity = Activity.Current;
        // A parent invoke_agent/HarnessRun activity is NOT proof of the inner chat span.
        if (activity is null || !string.Equals(
                activity.GetTagItem("gen_ai.operation.name")?.ToString(), "chat", StringComparison.Ordinal))
            return (ContextLinkState.Unavailable, null, null);
        return (ContextLinkState.Pending, activity.TraceId.ToString(), activity.SpanId.ToString());
    }
}
