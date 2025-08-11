namespace ChatClient.Api.Client.Services;

/// <summary>
/// Handles debouncing of streaming updates with immediate first update and delayed subsequent updates
/// </summary>
public sealed class StreamingDebouncer : IDisposable
{
    private readonly int _debounceDelayMs;
    private readonly Timer _timer;
    private readonly Func<Task> _onUpdate;
    private readonly object _lockObject = new();
    private bool _hasFirstUpdate;
    private bool _disposed;

    public StreamingDebouncer(int debounceDelayMs, Func<Task> onUpdate)
    {
        _debounceDelayMs = debounceDelayMs;
        _onUpdate = onUpdate;
        _timer = new Timer(OnTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Triggers an update - first update happens immediately, subsequent updates are debounced
    /// </summary>
    public void TriggerUpdate()
    {
        if (_disposed)
            return;

        lock (_lockObject)
        {
            if (!_hasFirstUpdate)
            {
                _hasFirstUpdate = true;
                // First update - execute immediately without waiting
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _onUpdate();
                    }
                    catch (Exception)
                    {
                        // Swallow exceptions to prevent crashing the background task
                    }
                });
                return;
            }

            // Subsequent updates - restart the debounce timer
            _timer.Change(_debounceDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Force immediate execution of pending update
    /// </summary>
    public async Task FlushAsync()
    {
        if (_disposed)
            return;

        lock (_lockObject)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        try
        {
            await _onUpdate();
        }
        catch (Exception)
        {
            // Swallow exceptions as this is called in cleanup scenarios
        }
    }

    private async void OnTimerCallback(object? state)
    {
        if (_disposed)
            return;

        try
        {
            await _onUpdate();
        }
        catch (Exception)
        {
            // Swallow exceptions to prevent crashing the timer callback
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lockObject)
        {
            _disposed = true;
            _timer?.Dispose();
        }
    }
}
