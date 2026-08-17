using ChatClient.Api.Diagnostics;
using System.Diagnostics;

namespace ChatClient.Api.Client.Services.Agentic;

public enum HarnessTraceRunStatus { Running, Completed, Canceled, Failed }
public sealed record HarnessTraceAttribute(string Key, string Value);
public sealed record HarnessTraceEventSnapshot(string Name, DateTimeOffset Timestamp, IReadOnlyList<HarnessTraceAttribute> Tags);
public sealed record HarnessTraceLinkSnapshot(string TraceId, string SpanId, ActivityTraceFlags TraceFlags, IReadOnlyList<HarnessTraceAttribute> Tags);
public sealed record HarnessTraceSpanSnapshot(string TraceId, string SpanId, string ParentSpanId, string DisplayName, string SourceName, ActivityKind Kind, DateTimeOffset StartedAt, TimeSpan? Duration, ActivityStatusCode StatusCode, string? StatusDescription, IReadOnlyList<HarnessTraceAttribute> Tags, IReadOnlyList<HarnessTraceEventSnapshot> Events, bool EventsTruncated, IReadOnlyList<HarnessTraceLinkSnapshot> Links);
public sealed record HarnessTraceRunSnapshot(string RunId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, HarnessTraceRunStatus Status, bool IsTruncated, string TraceId, IReadOnlyList<string> TraceIds, IReadOnlyList<HarnessTraceSpanSnapshot> Spans);
public sealed record HarnessTraceSessionSnapshot(IReadOnlyList<HarnessTraceRunSnapshot> Runs);

public sealed class HarnessTraceSession : IDisposable
{
    private readonly HarnessTelemetryListenerHub hub;
    private readonly ILogger<HarnessTraceSession> logger;
    public const int MaxRunsPerSession = 20, MaxSpansPerRun = 1000, MaxEventsPerSpan = 100, MaxAttributeValueLength = 4096;
    private static readonly ActivitySource CaptureSource = new(HarnessTelemetry.CaptureSourceName);
    private readonly object _gate = new();
    private readonly Dictionary<string, MutableRun> _runsById = new(StringComparer.Ordinal);
    private readonly Timer _notificationTimer;
    private bool _disposed;
    private int _notificationPending;
    public event Action? Changed;

    public HarnessTraceSession(HarnessTelemetryListenerHub hub, ILogger<HarnessTraceSession> logger)
    {
        this.hub = hub;
        this.logger = logger;
        _notificationTimer = new Timer(static state => ((HarnessTraceSession)state!).RaiseChanged(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public HarnessTraceSessionSnapshot GetSnapshot()
    {
        lock (_gate)
            return new(_runsById.Values.OrderByDescending(run => run.StartedAt).Select(run => new HarnessTraceRunSnapshot(run.RunId, run.StartedAt, run.CompletedAt, run.Status, run.IsTruncated, run.PrimaryTraceId, run.TraceIds.ToArray(), run.Spans.Values.OrderBy(span => span.StartedAt).Select(span => span.ToSnapshot()).ToArray())).ToArray());
    }

    public HarnessTraceRunScope? TryBeginRun(string runId)
    {
        Activity? root = null;
        MutableRun? run = null;
        IDisposable? correlation = null;
        var registered = false;
        var added = false;
        try
        {
            if (_disposed)
                return null;
            // A synthetic parent context gives the capture root a fresh trace without inheriting Activity.Current.
            root = CaptureSource.StartActivity("HarnessRun", ActivityKind.Internal,
                new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded));
            if (root is null)
            { logger.LogWarning("Harness trace capture is unavailable; continuing without diagnostics."); return null; }
            run = new MutableRun(runId, DateTimeOffset.UtcNow, root.TraceId.ToString());
            lock (_gate)
            {
                if (_disposed)
                { root.Dispose(); return null; }
                _runsById.Add(run.RunId, run);
                added = true;
                TrimRuns();
            }
            hub.Register(root.TraceId, this, run.RunId);
            registered = true;
            correlation = hub.Activate(this, run.RunId);
            NotifyLater();
            return new(this, root, run, correlation);
        }
        catch (Exception ex)
        {
            correlation?.Dispose();
            if (registered && run is not null)
                hub.UnregisterRun(this, run.RunId);
            if (added && run is not null)
                lock (_gate)
                    if (_runsById.TryGetValue(run.RunId, out var recordedRun) && ReferenceEquals(recordedRun, run))
                        _runsById.Remove(run.RunId);
            if (root is not null)
            {
                root.Stop();
                root.Dispose();
            }
            logger.LogWarning(ex, "Could not start Harness trace capture; continuing without diagnostics.");
            return null;
        }
    }

    internal bool TryBindTrace(string runId, string traceId)
    {
        lock (_gate)
        {
            if (_disposed || !_runsById.TryGetValue(runId, out var run))
                return false;
            run.TraceIds.Add(traceId);
            return true;
        }
    }

    internal void Record(string runId, Activity activity, bool stopped)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || !_runsById.TryGetValue(runId, out var run))
                    return;
                var spanKey = GetSpanKey(activity);
                if (!run.Spans.TryGetValue(spanKey, out var span))
                {
                    if (run.Spans.Count >= MaxSpansPerRun)
                    { run.IsTruncated = true; return; }
                    span = new(activity);
                    run.Spans.Add(spanKey, span);
                }
                if (stopped)
                    span.UpdateFinal(activity);
            }
            NotifyLater();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not capture Harness trace activity {ActivityName}.", activity.DisplayName); }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var run in _runsById.Values)
                UnregisterRun(run);
            _runsById.Clear();
        }
        NotifyNow();
    }

    private void Complete(MutableRun run, HarnessTraceRunStatus status, Activity root)
    {
        try
        { lock (_gate) { if (_runsById.ContainsKey(run.RunId)) { run.Status = status; run.CompletedAt = DateTimeOffset.UtcNow; } } root.Stop(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not complete Harness trace capture."); }
        finally { root.Dispose(); NotifyNow(); }
    }
    private void TrimRuns()
    {
        foreach (var old in _runsById.Values.OrderBy(run => run.StartedAt).Take(Math.Max(0, _runsById.Count - MaxRunsPerSession)).ToArray())
        { _runsById.Remove(old.RunId); UnregisterRun(old); }
    }
    private void UnregisterRun(MutableRun run)
    {
        hub.UnregisterRun(this, run.RunId);
    }
    private void NotifyLater() { if (Interlocked.Exchange(ref _notificationPending, 1) == 0) _notificationTimer.Change(TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan); }
    private void NotifyNow() { _notificationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); Interlocked.Exchange(ref _notificationPending, 0); RaiseChanged(); }
    private void RaiseChanged() { Interlocked.Exchange(ref _notificationPending, 0); try { Changed?.Invoke(); } catch (Exception ex) { logger.LogWarning(ex, "Trace observer failed."); } }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); _notificationTimer.Dispose(); }

    public sealed class HarnessTraceRunScope : IDisposable
    {
        private readonly HarnessTraceSession owner;
        private readonly Activity root;
        private readonly MutableRun run; private readonly IDisposable correlation;
        private HarnessTraceRunStatus _status = HarnessTraceRunStatus.Completed; private int _disposed;
        internal HarnessTraceRunScope(HarnessTraceSession owner, Activity root, MutableRun run, IDisposable correlation) { this.owner = owner; this.root = root; this.run = run; this.correlation = correlation; }
        public void Cancel() => _status = HarnessTraceRunStatus.Canceled;
        public void Fail() => _status = HarnessTraceRunStatus.Failed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { correlation.Dispose(); owner.Complete(run, _status, root); } }
    }
    internal sealed class MutableRun(string runId, DateTimeOffset startedAt, string traceId)
    {
        public string RunId { get; } = runId; public DateTimeOffset StartedAt { get; } = startedAt; public string PrimaryTraceId { get; } = traceId;
        public HashSet<string> TraceIds { get; } = new(StringComparer.Ordinal) { traceId };
        public DateTimeOffset? CompletedAt { get; set; }
        public HarnessTraceRunStatus Status { get; set; } = HarnessTraceRunStatus.Running; public bool IsTruncated { get; set; }
        internal Dictionary<string, MutableSpan> Spans { get; } = new(StringComparer.Ordinal);
    }
    internal sealed class MutableSpan
    {
        public MutableSpan(Activity activity) { TraceId = activity.TraceId.ToString(); SpanId = activity.SpanId.ToString(); ParentSpanId = activity.ParentSpanId.ToString(); SourceName = activity.Source.Name; Kind = activity.Kind; StartedAt = activity.StartTimeUtc; DisplayName = activity.DisplayName; }
        public string TraceId { get; }
        public string SpanId { get; }
        public string ParentSpanId { get; }
        public string SourceName { get; }
        public ActivityKind Kind { get; }
        public DateTimeOffset StartedAt { get; }
        public string DisplayName { get; private set; }
        public TimeSpan? Duration { get; private set; }
        public ActivityStatusCode StatusCode { get; private set; }
        public string? StatusDescription { get; private set; }
        public IReadOnlyList<HarnessTraceAttribute> Tags { get; private set; } = [];
        public IReadOnlyList<HarnessTraceEventSnapshot> Events { get; private set; } = [];
        public bool EventsTruncated { get; private set; }
        public IReadOnlyList<HarnessTraceLinkSnapshot> Links { get; private set; } = [];
        public void UpdateFinal(Activity activity)
        {
            DisplayName = activity.DisplayName;
            Duration = activity.Duration;
            StatusCode = activity.Status;
            StatusDescription = activity.StatusDescription;
            Tags = Attributes(activity.TagObjects);
            var events = activity.Events.Take(MaxEventsPerSpan + 1).ToArray();
            EventsTruncated = events.Length > MaxEventsPerSpan;
            Events = events.Take(MaxEventsPerSpan).Select(e => new HarnessTraceEventSnapshot(e.Name, e.Timestamp, Attributes(e.Tags))).ToArray();
            Links = activity.Links.Select(link => new HarnessTraceLinkSnapshot(link.Context.TraceId.ToString(), link.Context.SpanId.ToString(), link.Context.TraceFlags, Attributes(link.Tags))).ToArray();
        }
        public HarnessTraceSpanSnapshot ToSnapshot() => new(TraceId, SpanId, ParentSpanId, DisplayName, SourceName, Kind, StartedAt, Duration, StatusCode, StatusDescription, Tags.ToArray(), Events.ToArray(), EventsTruncated, Links.ToArray());
        private static IReadOnlyList<HarnessTraceAttribute> Attributes(IEnumerable<KeyValuePair<string, object?>> values) => values.Select(value => new HarnessTraceAttribute(value.Key, Clean(value.Key, value.Value?.ToString() ?? string.Empty))).ToArray();
    }
    private static string GetSpanKey(Activity activity) => $"{activity.TraceId}/{activity.SpanId}";
    private static string Clean(string key, string value) { string[] sensitive = ["authorization", "api-key", "api_key", "apikey", "token", "password", "passwd", "secret", "cookie", "connectionstring", "connection_string"]; return sensitive.Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase)) ? "[REDACTED]" : value.Length > MaxAttributeValueLength ? value[..MaxAttributeValueLength] + "…" : value; }
}
