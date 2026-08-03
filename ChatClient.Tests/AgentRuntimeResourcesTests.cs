using ChatClient.Api.Client.Services.Agentic;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class AgentRuntimeResourcesTests
{
    [Fact]
    public void Dispose_ReleasesResourcesInReverseOrderOnlyOnce()
    {
        var resources = new AgentRuntimeResources(NullLogger.Instance);
        TrackingDisposable.DisposalOrder.Clear();
        var first = new TrackingDisposable("first");
        var second = new TrackingDisposable("second");

        resources.Own(first);
        resources.Own(second);
        resources.Own(first);
        resources.Dispose();
        resources.Dispose();

        Assert.Equal(["second", "first"], TrackingDisposable.DisposalOrder);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void Dispose_ContinuesWhenOneResourceFails()
    {
        var resources = new AgentRuntimeResources(NullLogger.Instance);
        TrackingDisposable.DisposalOrder.Clear();
        var first = new TrackingDisposable("first");
        var failing = new TrackingDisposable("failing", throwOnDispose: true);
        resources.Own(first);
        resources.Own(failing);

        resources.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, failing.DisposeCount);
    }

    private sealed class TrackingDisposable(string name, bool throwOnDispose = false) : IDisposable
    {
        public static List<string> DisposalOrder { get; } = [];
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            DisposalOrder.Add(name);
            if (throwOnDispose)
                throw new InvalidOperationException("Expected cleanup failure.");
        }
    }
}
