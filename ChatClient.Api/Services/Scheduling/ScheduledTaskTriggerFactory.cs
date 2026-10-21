using ChatClient.Domain.Models;
using Quartz;

namespace ChatClient.Api.Services.Scheduling;

/// <summary>The single Quartz schedule builder used for validation, preview and runtime.</summary>
public sealed class ScheduledTaskTriggerFactory
{
    public static JobKey JobKey(Guid id) => new($"job-{id:N}", "scheduled-tasks");
    public static TriggerKey TriggerKey(Guid id) => new($"trigger-{id:N}", "scheduled-tasks");

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Select a time zone.");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"Time zone '{id}' is not available on this host.", ex);
        }
    }

    public static TimeSpan Interval(ScheduleDefinition s)
    {
        if (s.IntervalValue <= 0 || !Enum.IsDefined(s.IntervalUnit))
            throw new ArgumentException("Interval must be a positive number of minutes or hours.");
        try
        {
            return s.IntervalUnit == ScheduleIntervalUnit.Minutes
                ? TimeSpan.FromTicks(checked(TimeSpan.TicksPerMinute * (long)s.IntervalValue))
                : TimeSpan.FromTicks(checked(TimeSpan.TicksPerHour * (long)s.IntervalValue));
        }
        catch (OverflowException ex)
        {
            throw new ArgumentException("Interval is too large.", ex);
        }
    }

    public static string Cron(ScheduleDefinition s)
    {
        if (s.Hour is < 0 or > 23 || s.Minute is < 0 or > 59)
            throw new ArgumentException("Time must be between 00:00 and 23:59.");
        var prefix = $"0 {s.Minute} {s.Hour}";
        return s.Type switch
        {
            ScheduleType.Daily => $"{prefix} * * ?",
            ScheduleType.Weekly => $"{prefix} ? * {WeeklyDays(s.DaysOfWeek)}",
            ScheduleType.Monthly => $"{prefix} {MonthlyDays(s.DaysOfMonth)} * ?",
            ScheduleType.Cron => ValidatedCronText(s.CronExpression),
            _ => throw new ArgumentException("Unsupported schedule type.")
        };
    }

    private static string WeeklyDays(List<DayOfWeek>? days)
    {
        if (days is null || days.Count == 0 || days.Count != days.Distinct().Count() ||
            days.Any(static day => !Enum.IsDefined(day)))
            throw new ArgumentException("Select one or more unique weekdays.");
        return string.Join(",", days.OrderBy(x => x).Select(x => x switch
        {
            DayOfWeek.Sunday => "SUN",
            DayOfWeek.Monday => "MON",
            DayOfWeek.Tuesday => "TUE",
            DayOfWeek.Wednesday => "WED",
            DayOfWeek.Thursday => "THU",
            DayOfWeek.Friday => "FRI",
            _ => "SAT"
        }));
    }

    private static string MonthlyDays(List<int>? days)
    {
        if (days is null || days.Count == 0 || days.Count != days.Distinct().Count() ||
            days.Any(static day => day is < 1 or > 31))
            throw new ArgumentException("Select one or more unique month days (1–31).");
        return string.Join(",", days.Order());
    }

    private static string ValidatedCronText(string? expression)
    {
        var text = expression?.Trim() ?? "";
        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is not (6 or 7) || fields[0] != "0")
            throw new ArgumentException("Quartz Cron needs 6 or 7 fields, with the seconds field exactly '0'.");
        return text;
    }

    public ITrigger Build(ScheduledTaskDefinition definition, DateTimeOffset now)
    {
        if (definition.Id == Guid.Empty)
            throw new ArgumentException("Timer ID must not be empty.");
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Name is required.");
        var zone = ResolveTimeZone(definition.TimeZoneId);
        var s = definition.Schedule ?? throw new ArgumentException("Schedule is required.");
        if (!Enum.IsDefined(s.Type))
            throw new ArgumentException("Unknown schedule type.");

        var builder = TriggerBuilder.Create()
            .WithIdentity(TriggerKey(definition.Id))
            .ForJob(JobKey(definition.Id));

        if (s.Type == ScheduleType.Interval)
        {
            return builder.StartAt(now.Add(Interval(s)))
                .WithSimpleSchedule(x => x.WithInterval(Interval(s)).RepeatForever()
                    .WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithRemainingCount))
                .Build();
        }
        return builder.StartAt(now)
            .WithCronSchedule(Cron(s), x => x.InTimeZone(zone)
                .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
            .Build();
    }

    public static bool SameTrigger(ScheduledTaskDefinition before, ScheduledTaskDefinition after)
    {
        if (before.Enabled != after.Enabled || before.Schedule.Type != after.Schedule.Type)
            return false;
        if (before.Schedule.Type != ScheduleType.Interval &&
            before.TimeZoneId != after.TimeZoneId)
            return false;
        return System.Text.Json.JsonSerializer.Serialize(before.Schedule) ==
               System.Text.Json.JsonSerializer.Serialize(after.Schedule);
    }

    public static IReadOnlyList<DateTimeOffset> Occurrences(ITrigger trigger, DateTimeOffset now, int count = 5)
    {
        var dates = new List<DateTimeOffset>(count);
        var after = now;
        for (var i = 0; i < count; i++)
        {
            var next = trigger.GetFireTimeAfter(after);
            if (next is null)
                break;
            if (next <= after)
                throw new InvalidOperationException("Quartz returned a non-increasing schedule.");
            dates.Add(next.Value);
            after = next.Value;
        }
        return dates;
    }
}
