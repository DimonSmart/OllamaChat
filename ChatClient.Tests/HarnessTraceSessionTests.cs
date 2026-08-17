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
}
