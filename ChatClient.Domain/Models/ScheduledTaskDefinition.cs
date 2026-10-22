namespace ChatClient.Domain.Models;

public enum ScheduleType { Interval, Daily, Weekly, Monthly, Cron }
public enum ScheduleIntervalUnit { Minutes, Hours }

public sealed class ScheduleDefinition
{
    public ScheduleType Type { get; set; } = ScheduleType.Daily;
    public int IntervalValue { get; set; } = 1;
    public ScheduleIntervalUnit IntervalUnit { get; set; } = ScheduleIntervalUnit.Minutes;
    public int Hour { get; set; } = 14;
    public int Minute { get; set; }
    public List<DayOfWeek> DaysOfWeek { get; set; } = [];
    public List<int> DaysOfMonth { get; set; } = [];
    public string CronExpression { get; set; } = "0 0 14 ? * MON-FRI";
}

public sealed class ScheduledAgentConfiguration
{
    public Guid AgentId { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public Guid? WorkspaceId { get; set; }
}

public static class ScheduledAgentConfigurationValidation
{
    public const int MaximumPromptLength = 32 * 1024;

    public static void Validate(ScheduledAgentConfiguration? value)
    {
        if (value is null)
            throw new ArgumentException("An Agent and a prompt must be configured.");
        if (value.WorkspaceId == Guid.Empty)
            throw new ArgumentException("Saved Workspace ID cannot be empty.");
        if (value.AgentId == Guid.Empty)
            throw new ArgumentException("Choose a Saved Agent.");
        if (string.IsNullOrWhiteSpace(value.Prompt))
            throw new ArgumentException("Agent prompt cannot be empty.");
        if (value.Prompt.Length > MaximumPromptLength)
            throw new ArgumentException($"Agent prompt cannot exceed {MaximumPromptLength} characters.");
    }
}

public sealed class ScheduledTaskDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public ScheduleDefinition Schedule { get; set; } = new();
    public ScheduledAgentConfiguration? AgentExecution { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Revision { get; set; }
}
