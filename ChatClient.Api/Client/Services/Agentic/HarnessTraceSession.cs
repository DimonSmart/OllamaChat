using ChatClient.Api.Diagnostics;
using System.Diagnostics;

namespace ChatClient.Api.Client.Services.Agentic;

public enum HarnessTraceRunStatus { Running, Completed, Canceled, Failed }
public sealed record HarnessTraceAttribute(string Key, string Value);
public sealed class HarnessTraceSpanSnapshot
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string ParentSpanId { get; init; } = "";
    public required string DisplayName { get; set; }
    public required string SourceName { get; init; }
    public ActivityKind Kind { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan? Duration { get; set; }
    public ActivityStatusCode StatusCode { get; set; }
    public string? StatusDescription { get; set; }
    public IReadOnlyList<HarnessTraceAttribute> Tags { get; set; } = [];
}
public sealed class HarnessTraceRunSnapshot
{
    public required string RunId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public HarnessTraceRunStatus Status { get; set; } = HarnessTraceRunStatus.Running; public bool IsTruncated { get; set; }
    public List<HarnessTraceSpanSnapshot> Spans { get; } = []; public string TraceId { get; init; } = "";
}

public sealed class HarnessTraceSession(HarnessTelemetryListenerHub hub, ILogger<HarnessTraceSession> logger) : IDisposable
{
    public const int MaxRunsPerSession = 20, MaxSpansPerRun = 1000, MaxAttributeValueLength = 4096;
    private static readonly ActivitySource CaptureSource = new(HarnessTelemetry.CaptureSourceName);
    private readonly object _gate = new(); private readonly Dictionary<string, HarnessTraceRunSnapshot> _runsByTrace = new();
    private bool _disposed;
    public event Action? Changed;
    public IReadOnlyList<HarnessTraceRunSnapshot> Runs { get { lock (_gate) return _runsByTrace.Values.OrderByDescending(x => x.StartedAt).ToArray(); } }
    public HarnessTraceRunScope BeginRun(string runId)
    {
        var root = CaptureSource.StartActivity("HarnessRun", ActivityKind.Internal) ?? throw new InvalidOperationException("Trace listener is unavailable.");
        var run = new HarnessTraceRunSnapshot { RunId = runId, StartedAt = DateTimeOffset.UtcNow, TraceId = root.TraceId.ToString() };
        lock (_gate)
        { _runsByTrace[run.TraceId] = run; TrimRuns(); }
        hub.Register(root.TraceId, this);
        Notify();
        return new HarnessTraceRunScope(this, root, run);
    }
    internal void Record(Activity activity, bool stopped)
    {
        lock (_gate)
        {
            if (_disposed || !_runsByTrace.TryGetValue(activity.TraceId.ToString(), out var run))
                return;
            var span = run.Spans.FirstOrDefault(x => x.SpanId == activity.SpanId.ToString());
            if (span is null)
            {
                if (run.Spans.Count >= MaxSpansPerRun)
                { run.IsTruncated = true; return; }
                span = new HarnessTraceSpanSnapshot { TraceId = activity.TraceId.ToString(), SpanId = activity.SpanId.ToString(), ParentSpanId = activity.ParentSpanId.ToString(), DisplayName = activity.DisplayName, SourceName = activity.Source.Name, Kind = activity.Kind, StartedAt = activity.StartTimeUtc, Tags = activity.TagObjects.Select(x => new HarnessTraceAttribute(x.Key, Clean(x.Key, x.Value?.ToString() ?? ""))).ToArray() };
                run.Spans.Add(span);
            }
            if (stopped)
            { span.Duration = activity.Duration; span.StatusCode = activity.Status; span.StatusDescription = activity.StatusDescription; }
        }
        Notify();
    }
    public void Clear() { lock (_gate) { foreach (var run in _runsByTrace.Values) hub.Unregister(ActivityTraceId.CreateFromString(run.TraceId), this); _runsByTrace.Clear(); } Notify(); }
    private void Complete(HarnessTraceRunSnapshot run, HarnessTraceRunStatus status, Activity root) { lock (_gate) { run.Status = status; run.CompletedAt = DateTimeOffset.UtcNow; } root.Stop(); Notify(); }
    private void TrimRuns() { foreach (var old in _runsByTrace.Values.OrderBy(x => x.StartedAt).Take(Math.Max(0, _runsByTrace.Count - MaxRunsPerSession)).ToArray()) { _runsByTrace.Remove(old.TraceId); hub.Unregister(ActivityTraceId.CreateFromString(old.TraceId), this); } }
    private static string Clean(string key, string value) { var sensitive = new[] { "authorization", "api-key", "api_key", "apikey", "token", "password", "passwd", "secret", "cookie", "connectionstring", "connection_string" }; return sensitive.Any(x => key.Contains(x, StringComparison.OrdinalIgnoreCase)) ? "[REDACTED]" : value.Length > MaxAttributeValueLength ? value[..MaxAttributeValueLength] + "…" : value; }
    private void Notify() { try { Changed?.Invoke(); } catch (Exception ex) { logger.LogWarning(ex, "Trace observer failed."); } }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); }
    public sealed class HarnessTraceRunScope(HarnessTraceSession owner, Activity root, HarnessTraceRunSnapshot run) : IDisposable { private HarnessTraceRunStatus _status = HarnessTraceRunStatus.Completed; public void Cancel() => _status = HarnessTraceRunStatus.Canceled; public void Fail() => _status = HarnessTraceRunStatus.Failed; public void Dispose() => owner.Complete(run, _status, root); }
}
