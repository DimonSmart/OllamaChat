using System.Text.Json;
using ChatClient.Api.Services.Scheduling;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Quartz;

namespace ChatClient.Api.Services;

/// <summary>Coordinates JSON as the source of truth and a single Quartz RAM scheduler.</summary>
public sealed class ScheduledTaskService(
    IScheduledTaskRepository repository,
    ISchedulerFactory schedulerFactory,
    ScheduledTaskTriggerFactory triggers,
    ScheduledTaskRuntimeState runtime,
    TimeProvider clock,
    ILogger<ScheduledTaskService> logger,
    IScheduledTaskManualRunService? manualRuns = null) : IScheduledTaskService
{
    private long _nextGeneration;
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            IReadOnlyList<ScheduledTaskDefinition> tasks;
            try { tasks = await repository.GetAllAsync(cancellationToken); }
            catch (Exception e)
            {
                logger.LogError(e, "Scheduled tasks storage could not be loaded; no timers were registered");
                return; // Other application features and the host must still start.
            }
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            foreach (var task in tasks)
            {
                if (task is null) continue;
                var state = NewState(task, null, true);
                runtime.Set(task.Id, state);
                await ReconcileSafeAsync(scheduler, task, state, cancellationToken);
            }
            _initialized = true;
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task<IReadOnlyList<ScheduledTaskView>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            // A broken document throws; never turn it into an empty list.
            var tasks = await repository.GetAllAsync(cancellationToken);
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            var result = new List<ScheduledTaskView>(tasks.Count);
            var now = clock.GetUtcNow();
            foreach (var task in tasks)
            {
                var state = runtime.Get(task.Id);
                try
                {
                    var proposed = triggers.Build(task, now);
                    var nextPossible = proposed.GetFireTimeAfter(now);
                    if (state?.Error is { } error)
                    {
                        result.Add(new(task, ScheduledTaskStatus.Error, state.LastFiredUtc, null, error));
                        continue;
                    }
                    if (!task.Enabled)
                    {
                        result.Add(new(task, ScheduledTaskStatus.Disabled, state?.LastFiredUtc, null, null));
                        continue;
                    }
                    var actual = await scheduler.GetTrigger(ScheduledTaskTriggerFactory.TriggerKey(task.Id), cancellationToken);
                    var next = actual?.NextFireTimeUtc;
                    if (actual is not null && next.HasValue)
                        result.Add(new(task, ScheduledTaskStatus.Active, state?.LastFiredUtc, next, null));
                    else if (nextPossible is null)
                        result.Add(new(task, ScheduledTaskStatus.Completed, state?.LastFiredUtc, null, null));
                    else
                        result.Add(new(task, ScheduledTaskStatus.Error, state?.LastFiredUtc, null,
                            "Schedule has not been registered in Quartz. Retry synchronization."));
                }
                catch (Exception ex)
                {
                    result.Add(new(task, ScheduledTaskStatus.Error, state?.LastFiredUtc, null, ex.Message));
                }
            }
            return result;
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task<ScheduledTaskDefinition> CreateAsync(
        ScheduledTaskDefinition definition, CancellationToken cancellationToken = default)
    {
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            Validate(definition);
            var now = clock.GetUtcNow();
            var saved = await repository.UpdateAsync(items =>
            {
                if (items.Any(x => x.Id == definition.Id))
                    throw new InvalidOperationException("Timer with this ID already exists.");
                var item = Copy(definition);
                item.Name = item.Name.Trim();
                item.CreatedAtUtc = now;
                item.UpdatedAtUtc = now;
                item.Revision = 1;
                items.Add(item);
                return item;
            }, cancellationToken);
            var state = NewState(saved, null, true);
            runtime.Set(saved.Id, state);
            await ReconcileSafeAsync(await schedulerFactory.GetScheduler(cancellationToken),
                saved, state, cancellationToken);
            ThrowSyncError(state);
            return saved;
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task<ScheduledTaskDefinition> UpdateAsync(
        ScheduledTaskDefinition definition, CancellationToken cancellationToken = default)
    {
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            Validate(definition);
            ScheduledTaskDefinition? original = null;
            var now = clock.GetUtcNow();
            var saved = await repository.UpdateAsync(items =>
            {
                var index = items.FindIndex(x => x.Id == definition.Id);
                if (index < 0) throw new KeyNotFoundException("Timer has been deleted.");
                original = items[index];
                CheckRevision(original, definition.Revision);
                var value = Copy(definition);
                value.Name = value.Name.Trim();
                value.CreatedAtUtc = original.CreatedAtUtc;
                value.UpdatedAtUtc = now;
                value.Revision = checked(original.Revision + 1);
                items[index] = value;
                return value;
            }, cancellationToken);
            var previous = runtime.Get(saved.Id);
            var unchanged = original is not null && ScheduledTaskTriggerFactory.SameTrigger(original, saved);
            var state = NewState(saved, previous, !unchanged);
            runtime.Set(saved.Id, state);
            if (!unchanged || previous is null || previous.Error is not null)
                await ReconcileSafeAsync(await schedulerFactory.GetScheduler(cancellationToken),
                    saved, state, cancellationToken);
            ThrowSyncError(state);
            return saved;
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task<ScheduledTaskDefinition> SetEnabledAsync(
        Guid id, long expectedRevision, bool enabled, CancellationToken cancellationToken = default)
    {
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            ScheduledTaskDefinition? original = null;
            var now = clock.GetUtcNow();
            var saved = await repository.UpdateAsync(items =>
            {
                var index = items.FindIndex(x => x.Id == id);
                if (index < 0) throw new KeyNotFoundException("Timer has been deleted.");
                original = items[index];
                CheckRevision(original, expectedRevision);
                var updated = Copy(original);
                updated.Enabled = enabled;
                if (enabled) Validate(updated);
                updated.UpdatedAtUtc = now;
                updated.Revision = checked(original.Revision + 1);
                items[index] = updated;
                return updated;
            }, cancellationToken);
            var previous = runtime.Get(id);
            var changed = original?.Enabled != saved.Enabled;
            var state = NewState(saved, previous, changed);
            runtime.Set(id, state);
            if (changed || previous?.Error is not null || previous is null)
                await ReconcileSafeAsync(await schedulerFactory.GetScheduler(cancellationToken),
                    saved, state, cancellationToken);
            ThrowSyncError(state);
            return saved;
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            await repository.UpdateAsync(items =>
            {
                var index = items.FindIndex(x => x.Id == id);
                if (index < 0) throw new KeyNotFoundException("Timer has been deleted.");
                CheckRevision(items[index], expectedRevision);
                items.RemoveAt(index);
                return true;
            }, cancellationToken);
            runtime.Remove(id); // From this point a stale Quartz job cannot be accepted.
            manualRuns?.TimerDeleted(id);
            try
            {
                var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
                await scheduler.DeleteJob(ScheduledTaskTriggerFactory.JobKey(id), cancellationToken);
                runtime.ClearCleanup(id);
            }
            catch (Exception ex)
            {
                runtime.MarkCleanup(id, ex.Message);
                logger.LogError(ex, "Unable to clean up deleted timer {Id} from Quartz", id);
                throw new InvalidOperationException(
                    "Timer deleted from JSON, but Quartz cleanup failed. Use Retry synchronization.", ex);
            }
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            var task = (await repository.GetAllAsync(cancellationToken))
                .FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Timer has been deleted.");
            var state = NewState(task, runtime.Get(id), true);
            runtime.Set(id, state);
            await ReconcileSafeAsync(await schedulerFactory.GetScheduler(cancellationToken), task, state, cancellationToken);
            ThrowSyncError(state);
        }
        finally { runtime.Gate.Release(); }
    }

    public async Task RetryCleanupAsync(CancellationToken cancellationToken = default)
    {
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            // Verify storage first; do not clean up based on an unreadable desired state.
            var live = (await repository.GetAllAsync(cancellationToken)).Select(x => x.Id).ToHashSet();
            var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
            foreach (var id in runtime.PendingCleanup())
            {
                if (live.Contains(id)) continue;
                try
                {
                    await scheduler.DeleteJob(ScheduledTaskTriggerFactory.JobKey(id), cancellationToken);
                    runtime.ClearCleanup(id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Retrying Quartz cleanup for {Id} failed", id);
                }
            }
        }
        finally { runtime.Gate.Release(); }
    }

    public Task<IReadOnlyList<Guid>> GetPendingCleanupAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(runtime.PendingCleanup());

    public Task<IReadOnlyList<DateTimeOffset>> PreviewAsync(
        ScheduledTaskDefinition definition, CancellationToken cancellationToken = default)
    {
        // Entire Quartz expression parsing and enumeration executes off the Blazor circuit.
        // Never mutate Quartz during preview; reuse the current anchor for an unchanged timer.
        return Task.Run(async () =>
        {
            var now = clock.GetUtcNow();
            var candidate = triggers.Build(definition, now);
            var existing = (await repository.GetAllAsync(cancellationToken))
                .FirstOrDefault(x => x.Id == definition.Id);
            ITrigger trigger = candidate;
            if (existing is not null && existing.Enabled &&
                ScheduledTaskTriggerFactory.SameTrigger(existing, definition))
            {
                var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
                trigger = await scheduler.GetTrigger(ScheduledTaskTriggerFactory.TriggerKey(definition.Id), cancellationToken)
                          ?? candidate;
            }
            return (IReadOnlyList<DateTimeOffset>)ScheduledTaskTriggerFactory.Occurrences(trigger, now);
        }, cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
    }

    private void Validate(ScheduledTaskDefinition definition)
    {
        if (definition.AgentExecution is not null)
            ScheduledAgentConfigurationValidation.Validate(definition.AgentExecution);
        var now = clock.GetUtcNow();
        var trigger = triggers.Build(definition, now);
        if (definition.Enabled && trigger.GetFireTimeAfter(now) is null)
            throw new ArgumentException("Enabled schedules must have a future occurrence.");
    }

    private ScheduledTaskRuntimeState.TimerState NewState(
        ScheduledTaskDefinition definition, ScheduledTaskRuntimeState.TimerState? previous, bool newGeneration)
        => new()
        {
            Enabled = definition.Enabled,
            Name = definition.Name,
            Generation = newGeneration || previous is null
                ? Interlocked.Increment(ref _nextGeneration) : previous.Generation,
            LastFiredUtc = previous?.LastFiredUtc,
            Error = previous?.Error
        };

    private async Task ReconcileSafeAsync(
        IScheduler scheduler, ScheduledTaskDefinition task,
        ScheduledTaskRuntimeState.TimerState state, CancellationToken cancellationToken)
    {
        try
        {
            // Exactly one Job and one Trigger per ID. Deletion is idempotent.
            await scheduler.DeleteJob(ScheduledTaskTriggerFactory.JobKey(task.Id), cancellationToken);
            if (task.Enabled)
            {
                var now = clock.GetUtcNow();
                var trigger = triggers.Build(task, now);
                if (trigger.GetFireTimeAfter(now) is not null)
                {
                    var job = JobBuilder.Create<ScheduledTimerFiredJob>()
                        .WithIdentity(ScheduledTaskTriggerFactory.JobKey(task.Id))
                        .UsingJobData("TimerId", task.Id.ToString("D"))
                        .UsingJobData("Generation", state.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .Build();
                    await scheduler.ScheduleJob(job, trigger, cancellationToken: cancellationToken);
                }
            }
            state.Error = null;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            logger.LogError(ex, "Could not synchronize scheduled timer {Id} with Quartz", task.Id);
        }
    }

    private static void ThrowSyncError(ScheduledTaskRuntimeState.TimerState state)
    {
        if (state.Error is { } error)
            throw new InvalidOperationException("Configuration saved, but Quartz synchronization failed: " + error);
    }

    private static void CheckRevision(ScheduledTaskDefinition item, long expected)
    {
        if (item.Revision != expected)
            throw new InvalidOperationException("Timer changed in another editor. Reload before saving.");
    }

    private static ScheduledTaskDefinition Copy(ScheduledTaskDefinition definition) =>
        JsonSerializer.Deserialize<ScheduledTaskDefinition>(JsonSerializer.Serialize(definition))!;
}
