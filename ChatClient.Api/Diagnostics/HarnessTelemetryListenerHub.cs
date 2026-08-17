using ChatClient.Api.Client.Services.Agentic;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ChatClient.Api.Diagnostics;

public static class HarnessTelemetry
{
    public const string ActivitySourceName = "OllamaChat.Harness";
    public const string CaptureSourceName = "OllamaChat.TraceCapture";
}

public sealed class HarnessTelemetryListenerHub : IDisposable
{
    private readonly ConcurrentDictionary<string, TraceRoute> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TraceRoute> _spanRoutes = new(StringComparer.Ordinal);
    private readonly AsyncLocal<RunCorrelation?> _currentRun = new();
    private readonly ActivityListener _listener;

    public HarnessTelemetryListenerHub(ILogger<HarnessTelemetryListenerHub> logger)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is HarnessTelemetry.ActivitySourceName or HarnessTelemetry.CaptureSourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => Route(activity, false),
            ActivityStopped = activity => Route(activity, true)
        };
        ActivitySource.AddActivityListener(_listener);
        Logger = logger;
    }

    private ILogger Logger { get; }

    internal IDisposable Activate(HarnessTraceSession session, string runId) => new CorrelationScope(this, _currentRun.Value, new(session, runId));
    internal void Register(ActivityTraceId traceId, HarnessTraceSession session, string runId) => _routes[traceId.ToString()] = new(session, runId);
    internal void UnregisterRun(HarnessTraceSession session, string runId)
    {
        var route = new TraceRoute(session, runId);
        RemoveRoutes(_routes, route);
        RemoveRoutes(_spanRoutes, route);
    }

    internal int RouteCount => _routes.Count;
    internal int SpanRouteCount => _spanRoutes.Count;

    private static void RemoveRoutes(ConcurrentDictionary<string, TraceRoute> routes, TraceRoute route)
    {
        foreach (var entry in routes)
            if (entry.Value == route)
                routes.TryRemove(entry);
    }

    private void Route(Activity activity, bool stopped)
    {
        if (activity.Source.Name != HarnessTelemetry.ActivitySourceName)
            return;
        var spanKey = $"{activity.TraceId}/{activity.SpanId}";
        if (stopped && _spanRoutes.TryRemove(spanKey, out var route))
        {
            Record(route, activity, true);
            return;
        }

        var traceId = activity.TraceId.ToString();
        if (!_routes.TryGetValue(traceId, out route))
        {
            var correlation = _currentRun.Value;
            if (correlation is null || !correlation.Session.TryBindTrace(correlation.RunId, traceId))
                return;
            route = new(correlation.Session, correlation.RunId);
            _routes.TryAdd(traceId, route);
        }
        if (!stopped)
            _spanRoutes[spanKey] = route;
        Record(route, activity, stopped);
    }

    private void Record(TraceRoute route, Activity activity, bool stopped)
    {
        try
        { route.Session.Record(route.RunId, activity, stopped); }
        catch (Exception ex) { Logger.LogWarning(ex, "Could not capture Harness activity {ActivityName}.", activity.DisplayName); }
    }

    public void Dispose() => _listener.Dispose();

    private sealed record TraceRoute(HarnessTraceSession Session, string RunId);
    private sealed record RunCorrelation(HarnessTraceSession Session, string RunId);
    private sealed class CorrelationScope : IDisposable
    {
        private readonly HarnessTelemetryListenerHub owner;
        private readonly RunCorrelation? previous;
        private int disposed;
        public CorrelationScope(HarnessTelemetryListenerHub owner, RunCorrelation? previous, RunCorrelation current)
        {
            this.owner = owner;
            this.previous = previous;
            owner._currentRun.Value = current;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner._currentRun.Value = previous;
        }
    }
}
