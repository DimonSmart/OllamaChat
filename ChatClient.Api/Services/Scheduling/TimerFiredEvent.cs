namespace ChatClient.Api.Services.Scheduling;

/// <summary>A diagnostic Quartz firing already accepted by the runtime state.</summary>
public sealed record TimerFiredEvent(Guid TimerId, string TimerName, DateTimeOffset FiredAtUtc);
