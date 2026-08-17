using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace ChatClient.Tests;

public sealed class HarnessTraceSessionTests
{
    private static readonly ActivitySource HarnessSource = new(HarnessTelemetry.ActivitySourceName);

    [Fact]
    public void RunsHaveDistinctTraceIdsEvenWithAmbientActivity()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        using var session = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        using var ambient = new Activity("ambient").Start();
        using var first = session.TryBeginRun("first");
        using var second = session.TryBeginRun("second");

        var runs = session.GetSnapshot().Runs;
        Assert.Equal(2, runs.Count);
        Assert.NotEqual(runs[0].TraceId, runs[1].TraceId);
        Assert.DoesNotContain(ambient.TraceId.ToString(), runs.Select(run => run.TraceId));
    }

    [Fact]
    public void FinalActivitySnapshotContainsMutableTelemetryData()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        using var session = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        using var run = session.TryBeginRun("run");
        using (var span = HarnessSource.StartActivity("initial", ActivityKind.Client))
        {
            Assert.NotNull(span);
            span!.DisplayName = "final";
            span.SetTag("gen_ai.operation.name", "chat");
            span.SetTag("api_key", "secret");
            span.AddEvent(new ActivityEvent("event", tags: new ActivityTagsCollection { { "detail", "value" } }));
            span.SetStatus(ActivityStatusCode.Error, "failure");
            span.Stop();
        }

        var spanSnapshot = Assert.Single(Assert.Single(session.GetSnapshot().Runs).Spans);
        Assert.Equal("final", spanSnapshot.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, spanSnapshot.StatusCode);
        Assert.Equal("failure", spanSnapshot.StatusDescription);
        Assert.Contains(spanSnapshot.Tags, tag => tag is { Key: "api_key", Value: "[REDACTED]" });
        Assert.Single(spanSnapshot.Events);
    }

    [Fact]
    public void SnapshotRemainsEnumerableWhileActivitiesArrive()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        using var session = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        using var run = session.TryBeginRun("run");
        Parallel.For(0, 100, _ => { using var span = HarnessSource.StartActivity("parallel"); _ = session.GetSnapshot().Runs.SelectMany(item => item.Spans).Count(); });
        Assert.NotEmpty(session.GetSnapshot().Runs);
    }

    [Fact]
    public async Task ConcurrentSessionsCaptureOnlyTheirOwnActivities()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        using var sessionA = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        using var sessionB = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);

        await Task.WhenAll(
            Task.Run(() => Capture(sessionA, "A")),
            Task.Run(() => Capture(sessionB, "B")));

        Assert.All(Assert.Single(sessionA.GetSnapshot().Runs).Spans, span => Assert.StartsWith("A", span.DisplayName));
        Assert.All(Assert.Single(sessionB.GetSnapshot().Runs).Spans, span => Assert.StartsWith("B", span.DisplayName));
    }

    [Fact]
    public void NewRootTraceInCurrentRunIsBoundAndRetained()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        using var session = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        using var run = session.TryBeginRun("run");
        var traceId = ActivityTraceId.CreateRandom();
        using var parent = new Activity("new-root-parent")
            .SetParentId($"00-{traceId}-{ActivitySpanId.CreateRandom()}-01")
            .Start();
        using var additionalRoot = HarnessSource.StartActivity("additional-root", ActivityKind.Internal);

        var snapshot = Assert.Single(session.GetSnapshot().Runs);
        Assert.Equal(2, snapshot.TraceIds.Count);
        Assert.Contains(snapshot.Spans, span => span.TraceId == traceId.ToString());
    }

    [Fact]
    public void ClearRemovesRoutesAndRetention()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        using var session = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        using var run = session.TryBeginRun("run");
        var traceId = Assert.Single(session.GetSnapshot().Runs).TraceId;
        session.Clear();
        using var late = HarnessSource.StartActivity("late", ActivityKind.Internal, new ActivityContext(ActivityTraceId.CreateFromString(traceId), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded));
        Assert.Empty(session.GetSnapshot().Runs);
    }

    [Fact]
    public void RunStatusAndBoundedRetentionAreReported()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        using var session = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        for (var index = 0; index <= HarnessTraceSession.MaxRunsPerSession; index++)
        {
            using var run = session.TryBeginRun($"run-{index}");
            if (index == HarnessTraceSession.MaxRunsPerSession)
                run!.Cancel();
        }
        var runs = session.GetSnapshot().Runs;
        Assert.Equal(HarnessTraceSession.MaxRunsPerSession, runs.Count);
        Assert.Equal(HarnessTraceRunStatus.Canceled, runs[0].Status);
    }

    [Fact]
    public void RedactsAndTruncatesAttributesAndBoundsSpans()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        using var session = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        using var run = session.TryBeginRun("run");
        using (var tagged = HarnessSource.StartActivity("tagged"))
        {
            tagged!.SetTag("authorization", "Bearer secret");
            tagged.SetTag("payload", new string('x', HarnessTraceSession.MaxAttributeValueLength + 1));
        }
        for (var index = 0; index < HarnessTraceSession.MaxSpansPerRun; index++)
        {
            using var span = HarnessSource.StartActivity($"span-{index}");
        }

        var result = Assert.Single(session.GetSnapshot().Runs);
        Assert.True(result.IsTruncated);
        Assert.Equal(HarnessTraceSession.MaxSpansPerRun, result.Spans.Count);
        var taggedSpan = Assert.Single(result.Spans, span => span.DisplayName == "tagged");
        Assert.Contains(taggedSpan.Tags, tag => tag is { Key: "authorization", Value: "[REDACTED]" });
        Assert.Contains(taggedSpan.Tags, tag => tag.Key == "payload" && tag.Value.EndsWith('…'));
    }

    [Fact]
    public void DisposeRemovesCaptureRoute()
    {
        using var hub = new HarnessTelemetryListenerHub(NullLogger<HarnessTelemetryListenerHub>.Instance);
        var session = new HarnessTraceSession(hub, NullLogger<HarnessTraceSession>.Instance);
        using var run = session.TryBeginRun("run");
        session.Dispose();
        using var span = HarnessSource.StartActivity("after-dispose");
        Assert.Empty(session.GetSnapshot().Runs);
    }

    private static void Capture(HarnessTraceSession session, string prefix)
    {
        using var run = session.TryBeginRun(prefix);
        for (var index = 0; index != 10; index++)
        {
            using var span = HarnessSource.StartActivity($"{prefix}-{index}");
        }
    }
}
