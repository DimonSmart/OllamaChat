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
    private readonly ConcurrentDictionary<string, HarnessTraceSession> _sessions = new(StringComparer.Ordinal);
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

    internal void Register(ActivityTraceId traceId, HarnessTraceSession session) => _sessions[traceId.ToString()] = session;
    internal void Unregister(ActivityTraceId traceId, HarnessTraceSession session) => _sessions.TryRemove(new KeyValuePair<string, HarnessTraceSession>(traceId.ToString(), session));

    private void Route(Activity activity, bool stopped)
    {
        if (activity.Source.Name != HarnessTelemetry.ActivitySourceName || !_sessions.TryGetValue(activity.TraceId.ToString(), out var session))
            return;
        try
        { session.Record(activity, stopped); }
        catch (Exception ex) { Logger.LogWarning(ex, "Could not capture Harness activity {ActivityName}.", activity.DisplayName); }
    }

    public void Dispose() => _listener.Dispose();
}
