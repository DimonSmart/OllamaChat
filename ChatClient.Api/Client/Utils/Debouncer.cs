namespace ChatClient.Api.Client.Utils;

/// <summary>
/// Provides a debounce mechanism with unique-queueing: duplicates are ignored until after processing.
/// </summary>
/// <typeparam name="T">Type of items to process. Must implement <see cref="IEquatable{T}"/> by default.</typeparam>
public sealed class Debouncer<T> : IDisposable where T : IEquatable<T>
{
    private readonly UniqueQueue<T> _queue;
    private readonly Func<T, Task> _handler;
    private readonly TimeSpan _interval;
    private SemaphoreSlim _signal;
    private CancellationTokenSource _cts;
    private Task _processingTask;

    public Debouncer(Func<T, Task> handler, TimeSpan interval)
        : this(handler, interval, null)
    {
    }

    public Debouncer(Func<T, Task> handler, TimeSpan interval, IEqualityComparer<T>? comparer)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _interval = interval;
        _queue = new UniqueQueue<T>(comparer);
        _signal = new SemaphoreSlim(0, int.MaxValue);
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(ProcessLoopAsync);
    }

    public Debouncer(Func<Task> handler, TimeSpan interval)
       : this(_ => handler(), interval, null)
    {
    }

    public void Enqueue(T item)
    {
        if (_queue.TryEnqueue(item)) _signal.Release();
    }

    public void Enqueue()
    {
        Enqueue(default!);
    }

    /// <summary>
    /// Clears any pending calls and restarts the processing loop.
    /// </summary>
    public void ClearDelayedCalls()
    {
        // Stop current loop
        _cts.Cancel();
        _signal.Release();
        try { _processingTask.Wait(); } catch { }

        // Clear pending items and dispose old resources
        _queue.Clear();
        _cts.Dispose();
        _signal.Dispose();

        // Restart
        _signal = new SemaphoreSlim(0, int.MaxValue);
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(ProcessLoopAsync);
    }

    private async Task ProcessLoopAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                if (_queue.TryDequeue(out var current))
                {
                    await _handler(current).ConfigureAwait(false);
                    await Task.Delay(_interval, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Release();
        try { _processingTask.Wait(); } catch { }
        _cts.Dispose();
        _signal.Dispose();
    }

    private class UniqueQueue<TItem> where TItem : IEquatable<TItem>
    {
        private readonly Queue<TItem> _queue;
        private readonly HashSet<TItem> _set;
        private readonly object _lock = new();

        public UniqueQueue(IEqualityComparer<TItem>? comparer = null)
        {
            _queue = new Queue<TItem>();
            _set = new HashSet<TItem>(comparer ?? EqualityComparer<TItem>.Default);
        }

        public bool TryEnqueue(TItem item)
        {
            lock (_lock)
            {
                if (_set.Add(item))
                {
                    _queue.Enqueue(item);
                    return true;
                }
                return false;
            }
        }

        public bool TryDequeue(out TItem item)
        {
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    item = default!;
                    return false;
                }
                item = _queue.Dequeue();
                _set.Remove(item);
                return true;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _queue.Clear();
                _set.Clear();
            }
        }
    }
}
