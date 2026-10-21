using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public enum ScheduledTaskStatus { Active, Disabled, Completed, Error }

public sealed record ScheduledTaskView(
    ScheduledTaskDefinition Definition,
    ScheduledTaskStatus Status,
    DateTimeOffset? LastFiredUtc,
    DateTimeOffset? NextFireUtc,
    string? Error);

public interface IScheduledTaskService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduledTaskView>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ScheduledTaskDefinition> CreateAsync(ScheduledTaskDefinition definition, CancellationToken cancellationToken = default);
    Task<ScheduledTaskDefinition> UpdateAsync(ScheduledTaskDefinition definition, CancellationToken cancellationToken = default);
    Task<ScheduledTaskDefinition> SetEnabledAsync(Guid id, long expectedRevision, bool enabled, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);
    Task RetryAsync(Guid id, CancellationToken cancellationToken = default);
    Task RetryCleanupAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetPendingCleanupAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DateTimeOffset>> PreviewAsync(ScheduledTaskDefinition definition, CancellationToken cancellationToken = default);
}
