using ChatClient.Api.Services.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;

namespace ChatClient.Tests;

public sealed class ScheduledTaskTimerFiredJobTests
{
    private static readonly DateTimeOffset Actual = DateTimeOffset.Parse("2027-01-01T12:00:00Z");

    [Fact]
    public async Task AcceptedJobPublishesOneEventOutsideRuntimeGate()
    {
        var runtime = new ScheduledTaskRuntimeState();
        var id = Guid.NewGuid();
        runtime.Set(id, new ScheduledTaskRuntimeState.TimerState
        {
            Enabled = true, Generation = 7, Name = "Name at acceptance"
        });
        var publisher = new TimerEventPublisher(NullLogger<TimerEventPublisher>.Instance);
        var received = new List<TimerFiredEvent>();
        publisher.TimerFired += e =>
        {
            Assert.True(runtime.Gate.Wait(0));
            runtime.Gate.Release();
            received.Add(e);
        };

        await CreateJob(runtime, publisher).Execute(Context(id.ToString(), "7"));

        var fired = Assert.Single(received);
        Assert.Equal(id, fired.TimerId);
        Assert.Equal("Name at acceptance", fired.TimerName);
        Assert.Equal(Actual, fired.FiredAtUtc);
        Assert.Equal(Actual, runtime.Get(id)!.LastFiredUtc);
    }

    [Fact]
    public async Task AcceptedJobsAtSameTimestampAreBothPublished()
    {
        var runtime = new ScheduledTaskRuntimeState();
        var id = Guid.NewGuid();
        runtime.Set(id, new ScheduledTaskRuntimeState.TimerState { Enabled = true, Generation = 1, Name = "Same" });
        var publisher = new TimerEventPublisher(NullLogger<TimerEventPublisher>.Instance);
        var count = 0;
        publisher.TimerFired += _ => count++;
        var job = CreateJob(runtime, publisher);
        await job.Execute(Context(id.ToString(), "1"));
        await job.Execute(Context(id.ToString(), "1"));
        Assert.Equal(2, count);
        Assert.Equal(Actual, runtime.Get(id)!.LastFiredUtc);
    }

    [Fact]
    public async Task InvalidDeletedDisabledAndOldGenerationJobsDoNotPublish()
    {
        var runtime = new ScheduledTaskRuntimeState();
        var id = Guid.NewGuid();
        var publisher = new TimerEventPublisher(NullLogger<TimerEventPublisher>.Instance);
        var count = 0;
        publisher.TimerFired += _ => count++;
        var job = CreateJob(runtime, publisher);

        await job.Execute(Context("bad-guid", "1"));
        await job.Execute(Context(id.ToString(), "bad-generation"));
        await job.Execute(Context(id.ToString(), "1"));
        runtime.Set(id, new ScheduledTaskRuntimeState.TimerState { Enabled = false, Generation = 1, Name = "Off" });
        await job.Execute(Context(id.ToString(), "1"));
        runtime.Set(id, new ScheduledTaskRuntimeState.TimerState { Enabled = true, Generation = 2, Name = "New" });
        await job.Execute(Context(id.ToString(), "1"));
        runtime.Remove(id);
        await job.Execute(Context(id.ToString(), "2"));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ThrowingSubscriberDoesNotFailJobOrSuppressOtherSubscribers()
    {
        var runtime = new ScheduledTaskRuntimeState();
        var id = Guid.NewGuid();
        runtime.Set(id, new ScheduledTaskRuntimeState.TimerState { Enabled = true, Generation = 1, Name = "Live" });
        var publisher = new TimerEventPublisher(NullLogger<TimerEventPublisher>.Instance);
        var seen = 0;
        publisher.TimerFired += _ => throw new InvalidOperationException("UI closed");
        publisher.TimerFired += _ => seen++;
        await CreateJob(runtime, publisher).Execute(Context(id.ToString(), "1"));
        Assert.Equal(1, seen);
        Assert.Equal(Actual, runtime.Get(id)!.LastFiredUtc);
    }

    private static ScheduledTimerFiredJob CreateJob(ScheduledTaskRuntimeState runtime, ITimerEventPublisher publisher) =>
        new(runtime, publisher, NullLogger<ScheduledTimerFiredJob>.Instance, new FrozenClock());

    private static IJobExecutionContext Context(string timerId, string generation)
    {
        var detail = JobBuilder.Create<ScheduledTimerFiredJob>()
            .UsingJobData("TimerId", timerId)
            .UsingJobData("Generation", generation)
            .Build();
        var context = new Mock<IJobExecutionContext>();
        context.Setup(x => x.JobDetail).Returns(detail);
        context.Setup(x => x.ScheduledFireTimeUtc).Returns(Actual);
        return context.Object;
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Actual;
    }
}
