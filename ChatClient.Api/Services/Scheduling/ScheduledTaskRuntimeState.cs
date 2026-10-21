using System.Collections.Concurrent;

namespace ChatClient.Api.Services.Scheduling;

public sealed class ScheduledTaskRuntimeState
{
    // Serializes persisted updates and the acceptance of diagnostic firings.
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TimerState> _timers = new();
    private readonly ConcurrentDictionary<Guid, string> _cleanup = new();

    public sealed class TimerState
    {
        public bool Enabled { get; init; }
        public long Generation { get; init; }
        public string Name { get; init; } = "";
        public DateTimeOffset? LastFiredUtc { get; set; }
        public string? Error { get; set; }
    }

    public TimerState? Get(Guid id) => _timers.GetValueOrDefault(id);
    public void Set(Guid id, TimerState state) => _timers[id] = state;
    public void Remove(Guid id) => _timers.TryRemove(id, out _);
    public void MarkCleanup(Guid id, string error) => _cleanup[id] = error;
    public void ClearCleanup(Guid id) => _cleanup.TryRemove(id, out _);
    public IReadOnlyList<Guid> PendingCleanup() => _cleanup.Keys.Order().ToArray();

    public async Task<(bool Valid, string? Name)> TryRecordAsync(
        Guid id, long generation, DateTimeOffset fired, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (!_timers.TryGetValue(id, out var state) || !state.Enabled ||
                state.Generation != generation)
                return (false, null);
            if (state.LastFiredUtc is null || fired > state.LastFiredUtc.Value)
                state.LastFiredUtc = fired;
            return (true, state.Name);
        }
        finally { Gate.Release(); }
    }
}
