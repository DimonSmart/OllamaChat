namespace ChatClient.Api.Client.Services;

/// <summary>
/// Simple debouncer that triggers an update only if enough time has passed
/// since the last call or when explicitly forced.
/// </summary>
public sealed class StreamingDebouncer
{
    private readonly int _debounceDelayMs;
    private readonly Func<Task> _onUpdate;
    private DateTime _lastUpdate = DateTime.MinValue;

    public StreamingDebouncer(int debounceDelayMs, Func<Task> onUpdate)
    {
        _debounceDelayMs = debounceDelayMs;
        _onUpdate = onUpdate;
    }

    /// <summary>
    /// Triggers the update callback if the debounce interval has elapsed or when forced.
    /// </summary>
    public Task TriggerAsync(MessageUpdateOptions options = default)
    {
        var now = DateTime.UtcNow;

        if (options.ForceRender || (now - _lastUpdate).TotalMilliseconds >= _debounceDelayMs)
        {
            _lastUpdate = now;
            return _onUpdate();
        }

        return Task.CompletedTask;
    }
}
