using ChatClient.Api.Services.Scheduling;
using ChatClient.Domain.Models;
using Quartz;

namespace ChatClient.Tests;

public class ScheduledTaskTriggerFactoryTests
{
    private static readonly ScheduledTaskTriggerFactory Factory = new();
    private static ScheduledTaskDefinition Task(ScheduleDefinition schedule) => new()
    {
        Id = Guid.Parse("5c4e5340-4924-40f2-855e-fc5e8c354b4c"),
        Name = "Monitor",
        Schedule = schedule,
        TimeZoneId = "Europe/Madrid",
        Enabled = true
    };

    [Fact]
    public void Interval_StartsAfterFullInterval_AndSkipsMisfires()
    {
        var now = DateTimeOffset.Parse("2027-01-01T00:00:00Z");
        var timer = Task(new ScheduleDefinition { Type = ScheduleType.Interval, IntervalValue = 15 });
        var trigger = Factory.Build(timer, now);
        Assert.Equal(now.AddMinutes(15), trigger.GetFireTimeAfter(now));
        Assert.Equal(5, ScheduledTaskTriggerFactory.Occurrences(trigger, now).Count);
        var simple = Assert.IsAssignableFrom<ISimpleTrigger>(trigger);
        Assert.Equal(SimpleTriggerMisfireInstruction.NextWithRemainingCount, simple.MisfireInstruction);
    }

    [Fact]
    public void Weekly_WeekdaysAtTwoPm_SkipsWeekend()
    {
        var timer = Task(new ScheduleDefinition
        {
            Type = ScheduleType.Weekly, Hour = 14, Minute = 0,
            DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday]
        });
        var now = DateTimeOffset.Parse("2027-01-01T15:00:00Z"); // Friday
        var next = Factory.Build(timer, now).GetFireTimeAfter(now);
        Assert.Equal(DateTimeOffset.Parse("2027-01-04T13:00:00Z"), next);
    }

    [Fact]
    public void Monthly_MissingDateIsSkipped()
    {
        var timer = Task(new ScheduleDefinition { Type = ScheduleType.Monthly, Hour = 9, DaysOfMonth = [31] });
        var now = DateTimeOffset.Parse("2027-03-31T12:00:00Z");
        Assert.Equal(DateTimeOffset.Parse("2027-05-31T07:00:00Z"),
            Factory.Build(timer, now).GetFireTimeAfter(now));
    }

    [Fact]
    public void Cron_HashIsStableForTimerIdentity()
    {
        var timer = Task(new ScheduleDefinition { Type = ScheduleType.Cron, CronExpression = "0 H H * * ?" });
        var now = DateTimeOffset.Parse("2027-01-01T00:00:00Z");
        var first = ScheduledTaskTriggerFactory.Occurrences(Factory.Build(timer, now), now);
        var second = ScheduledTaskTriggerFactory.Occurrences(Factory.Build(timer, now), now);
        Assert.Equal(first, second);
        Assert.Equal(5, first.Count);
    }

    [Fact]
    public void Cron_SecondsMustBeExactlyLiteralZero()
    {
        var now = DateTimeOffset.Parse("2027-01-01T00:00:00Z");
        foreach (var value in new[] { "H H 14 * * ?", "0/5 0 14 ? * MON", "0 14 * * *", "0-0 0 14 ? * MON" })
        {
            var timer = Task(new ScheduleDefinition { Type = ScheduleType.Cron, CronExpression = value });
            Assert.ThrowsAny<ArgumentException>(() => Factory.Build(timer, now));
        }
    }

    [Fact]
    public void Daily_DstTransitionInMadrid_IsResolvedByQuartz()
    {
        var timer = Task(new ScheduleDefinition { Type = ScheduleType.Daily, Hour = 2, Minute = 30 });
        var spring = DateTimeOffset.Parse("2027-03-27T23:00:00Z");
        var autumn = DateTimeOffset.Parse("2027-10-30T22:00:00Z");
        // Missing 02:30 is mapped to the end of the 02:00–03:00 local gap.
        Assert.Equal(DateTimeOffset.Parse("2027-03-28T01:00:00Z"),
            Factory.Build(timer, spring).GetFireTimeAfter(spring));
        // Ambiguous 02:30 fires only in its first (+02:00) occurrence.
        Assert.Equal(DateTimeOffset.Parse("2027-10-31T00:30:00Z"),
            Factory.Build(timer, autumn).GetFireTimeAfter(autumn));
    }

    [Fact]
    public void DisabledExpiredCron_IsSyntacticallyValidButHasNoFutureDates()
    {
        var timer = Task(new ScheduleDefinition { Type = ScheduleType.Cron,
            CronExpression = "0 0 14 ? * MON 2020" });
        timer.Enabled = false;
        var now = DateTimeOffset.Parse("2027-01-01T00:00:00Z");
        Assert.Empty(ScheduledTaskTriggerFactory.Occurrences(Factory.Build(timer, now), now));
    }
}
