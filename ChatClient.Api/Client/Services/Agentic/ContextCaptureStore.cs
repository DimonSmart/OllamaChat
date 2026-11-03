using System.Security.Cryptography;
using System.Text;

namespace ChatClient.Api.Client.Services.Agentic;

/// <summary>
/// Circuit-scoped, bounded, in-memory diagnostic store. Snapshots never keep framework objects.
/// Text is retained only for calls started while FullContext is explicitly selected.
/// </summary>
public sealed class ContextCaptureStore
{
    public const string UnlinkedRunId = "unlinked";
    private readonly object gate = new();
    private readonly ContextCaptureLimits limits;
    private readonly Dictionary<string, ContextInvocationSnapshot> invocations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ContextSegmentSnapshot>> segments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlobEntry> blobs = new(StringComparer.Ordinal);
    private long contentBytes;
    private ContextCaptureMode mode = ContextCaptureMode.MetadataOnly;

    public ContextCaptureStore() : this(new ContextCaptureLimits()) { }
    public ContextCaptureStore(ContextCaptureLimits limits) => this.limits = limits;

    public event Action? Changed;

    public ContextCaptureMode Mode
    {
        get { lock (gate) return mode; }
        set
        {
            lock (gate) mode = value;
            Notify();
        }
    }

    public IReadOnlyList<ContextInvocationSnapshot> GetInvocations(string? runId = null)
    {
        lock (gate)
            return invocations.Values
                .Where(x => runId is null || string.Equals(x.RunId, runId, StringComparison.Ordinal))
                .OrderBy(x => x.StartedAt).ThenBy(x => x.InvocationId, StringComparer.Ordinal)
                .ToArray();
    }

    public IReadOnlyList<ContextSegmentSnapshot> GetSegments(string invocationId)
    {
        lock (gate)
            return segments.TryGetValue(invocationId, out var items) ? items.ToArray() : [];
    }

    public ContextBlobSnapshot? GetContent(string? blobId)
    {
        if (blobId is null) return null;
        lock (gate)
            return blobs.TryGetValue(blobId, out var blob)
                ? new ContextBlobSnapshot(blob.Content, blob.Structured, false) : null;
    }

    internal string? Begin(ContextInvocationSnapshot invocation, IEnumerable<ContextSegmentDraft> input)
    {
        lock (gate)
        {
            if (mode == ContextCaptureMode.Off) return null;
            var runId = invocation.RunId ?? UnlinkedRunId;
            var list = invocations.Values.Where(x => x.RunId == runId).ToArray();
            if (list.Length >= limits.MaxInvocationsPerRun)
            {
                if (list.Length > 0)
                {
                    var last = list.MaxBy(x => x.StartedAt)!;
                    invocations[last.InvocationId] = last with { IsTruncated = true };
                }
                return null;
            }

            // Never let an older chat's diagnostic content accumulate without a bound.
            var runIds = invocations.Values
                .GroupBy(x => x.RunId)
                .Select(g => (RunId: g.Key, Started: g.Min(x => x.StartedAt)))
                .OrderBy(x => x.Started).ToArray();
            if (!runIds.Any(x => x.RunId == runId) && runIds.Length >= limits.MaxRuns)
            {
                foreach (var old in invocations.Values.Where(x => x.RunId == runIds[0].RunId).Select(x => x.InvocationId).ToArray())
                    RemoveInvocation(old);
            }

            var captured = new List<ContextSegmentSnapshot>();
            var categories = new Dictionary<ContextCategory, long>();
            int unknown = 0, order = 0;
            bool truncated = false;
            foreach (var draft in input)
            {
                if (order >= limits.MaxSegmentsPerInvocation)
                {
                    truncated = true;
                    break;
                }

                if (draft.EstimatedTokens is long tokens)
                    categories[draft.Category] = categories.GetValueOrDefault(draft.Category) + tokens;
                else
                    unknown++;

                string? blobId = null;
                bool blobTruncated = false;
                if (invocation.CaptureMode == ContextCaptureMode.FullContext && draft.Content is not null)
                {
                    blobId = RetainBlob(draft.Content, draft.IsStructured);
                    blobTruncated = blobId is null;
                    truncated |= blobTruncated;
                }

                captured.Add(new ContextSegmentSnapshot
                {
                    SegmentId = invocation.InvocationId + ":" + order,
                    InvocationId = invocation.InvocationId,
                    Order = order++,
                    Category = draft.Category,
                    Role = draft.Role,
                    ContentKind = draft.ContentKind,
                    MessageId = draft.MessageId,
                    CallId = draft.CallId,
                    ToolName = draft.ToolName,
                    BlobId = blobId,
                    IsStructured = draft.IsStructured,
                    EstimatedTokens = draft.EstimatedTokens,
                    Quality = draft.EstimatedTokens is null ? ContextTokenQuality.Unknown : ContextTokenQuality.HeuristicEstimated,
                    IsTruncated = blobTruncated
                });
            }

            segments[invocation.InvocationId] = captured;
            invocations[invocation.InvocationId] = invocation with
            {
                RunId = runId,
                SegmentCount = captured.Count,
                EstimatedTokens = categories.Values.Sum(),
                CategoryTokens = categories,
                UnknownSegments = unknown,
                IsTruncated = truncated
            };
        }
        Notify();
        return invocation.InvocationId;
    }

    public void Complete(string? id, ContextInvocationStatus status, TimeSpan elapsed,
        long? input = null, long? output = null, long? cached = null, long? reasoning = null)
    {
        if (id is null) return;
        lock (gate)
        {
            if (!invocations.TryGetValue(id, out var item)) return;
            invocations[id] = item with
            {
                Status = status,
                Duration = elapsed,
                ProviderInputTokens = input,
                ProviderOutputTokens = output,
                ProviderCachedInputTokens = cached,
                ProviderReasoningTokens = reasoning,
                CaptureComplete = status == ContextInvocationStatus.Completed
            };
        }
        Notify();
    }

    public void ClearContent()
    {
        lock (gate)
        {
            blobs.Clear();
            contentBytes = 0;
            foreach (var entry in segments)
                for (int i = 0; i < entry.Value.Count; i++)
                    entry.Value[i] = entry.Value[i] with { BlobId = null };
        }
        Notify();
    }

    public void Clear()
    {
        lock (gate)
        {
            invocations.Clear();
            segments.Clear();
            blobs.Clear();
            contentBytes = 0;
        }
        Notify();
    }

    private string? RetainBlob(string content, bool structured)
    {
        int bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > limits.MaxBlobBytes || contentBytes + bytes > limits.MaxFullContentBytes)
            return null;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            + (structured ? ":json" : ":text");
        if (blobs.TryGetValue(id, out var existing))
            existing.RefCount++;
        else
        {
            blobs.Add(id, new BlobEntry(content, structured, bytes));
            contentBytes += bytes;
        }
        return id;
    }

    private void RemoveInvocation(string id)
    {
        invocations.Remove(id);
        if (!segments.Remove(id, out var items)) return;
        foreach (var item in items)
        {
            if (item.BlobId is null || !blobs.TryGetValue(item.BlobId, out var blob)) continue;
            if (--blob.RefCount == 0)
            {
                contentBytes -= blob.Bytes;
                blobs.Remove(item.BlobId);
            }
        }
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch { /* Diagnostics must never interrupt the chat runtime. */ }
    }

    private sealed class BlobEntry(string content, bool structured, int bytes)
    {
        public string Content { get; } = content;
        public bool Structured { get; } = structured;
        public int Bytes { get; } = bytes;
        public int RefCount { get; set; } = 1;
    }
}
