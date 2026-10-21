using ChatClient.Api.Services.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class ScheduledTaskTimerEventPublisherTests
{
    private static readonly TimerFiredEvent Firing =
        new(Guid.Parse("5c4e5340-4924-40f2-855e-fc5e8c354b4c"), "Repository Monitor",
            DateTimeOffset.Parse("2027-01-01T12:00:00Z"));

    [Fact]
    public void PublishedEventReachesAllSubscribersWithExactValues()
    {
        var publisher = new TimerEventPublisher(NullLogger<TimerEventPublisher>.Instance);
        var first = new List<TimerFiredEvent>();
        var second = new List<TimerFiredEvent>();
        Action<TimerFiredEvent> listener = first.Add;
        publisher.TimerFired += listener;
        publisher.TimerFired += second.Add;
        publisher.Publish(Firing);
        Assert.Same(Firing, Assert.Single(first));
        Assert.Same(Firing, Assert.Single(second));

        publisher.TimerFired -= listener;
        publisher.Publish(Firing);
        Assert.Single(first);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void RepeatedEventsAreNotDeduplicatedAndNoSubscribersIsSafe()
    {
        var publisher = new TimerEventPublisher(NullLogger<TimerEventPublisher>.Instance);
        publisher.Publish(Firing);
        var count = 0;
        publisher.TimerFired += _ => count++;
        publisher.Publish(Firing);
        publisher.Publish(Firing);
        Assert.Equal(2, count);
    }

    [Fact]
    public void AThrowingSubscriberIsLoggedAndDoesNotBlockFollowingSubscribers()
    {
        var logger = new CountingLogger();
        var publisher = new TimerEventPublisher(logger);
        var received = 0;
        publisher.TimerFired += _ => throw new InvalidOperationException("Broken subscriber");
        publisher.TimerFired += _ => received++;
        publisher.Publish(Firing);
        Assert.Equal(1, received);
        Assert.Equal(1, logger.Errors);
    }

    [Fact]
    public void ConcurrentPublishAndSubscriptionChangesRemainSafe()
    {
        var publisher = new TimerEventPublisher(NullLogger<TimerEventPublisher>.Instance);
        var deliveries = 0;
        publisher.TimerFired += _ => Interlocked.Increment(ref deliveries);
        Parallel.For(0, 500, _ =>
        {
            Action<TimerFiredEvent> temporary = _ => { };
            publisher.TimerFired += temporary;
            publisher.Publish(Firing);
            publisher.TimerFired -= temporary;
        });
        Assert.Equal(500, deliveries);
    }

    private sealed class CountingLogger : ILogger<TimerEventPublisher>
    {
        public int Errors { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Error) Errors++;
        }
    }
}
