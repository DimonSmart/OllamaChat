namespace ChatClient.Client.Utils
{
    /// <summary>
    /// Provides a debounce mechanism with unique-queueing: duplicates are ignored until after processing.
    /// </summary>
    /// <typeparam name="T">Type of items to process. Must implement <see cref="IEquatable{T}"/> by default.</typeparam>
    public sealed class Debouncer<T> : IDisposable where T : IEquatable<T>
    {
        private readonly UniqueQueue<T> _queue;
        private readonly Func<T, Task> _handler;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _cts;
        private readonly Task _processingTask;
        private readonly SemaphoreSlim _signal;

        /// <summary>
        /// Initializes a new instance of <see cref="Debouncer{T}"/> using default equality (requires T : IEquatable&lt;T&gt;).
        /// </summary>
        /// <param name="handler">Async handler to invoke per item.</param>
        /// <param name="interval">Minimum interval between invocations.</param>
        public Debouncer(Func<T, Task> handler, TimeSpan interval)
            : this(handler, interval, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="Debouncer{T}"/> using a custom comparer.
        /// </summary>
        /// <param name="handler">Async handler to invoke per item.</param>
        /// <param name="interval">Minimum interval between invocations.</param>
        /// <param name="comparer">Custom comparer for item uniqueness.</param>
        public Debouncer(Func<T, Task> handler, TimeSpan interval, IEqualityComparer<T>? comparer)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _interval = interval;
            _queue = new UniqueQueue<T>(comparer);
            _signal = new SemaphoreSlim(0, int.MaxValue);

            _cts = new CancellationTokenSource();
            _processingTask = Task.Run(ProcessLoopAsync);
        }

        /// <summary>Enqueues an item if not already queued or processing.</summary>
        /// <param name="item">The item to enqueue.</param>
        public void Enqueue(T item)
        {
            if (_queue.TryEnqueue(item))
            {
                _signal.Release();
            }
        }

        private async Task ProcessLoopAsync()
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);

                    if (_queue.TryDequeue(out var current))
                    {
                        await _handler(current).ConfigureAwait(false);
                        await Task.Delay(_interval, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>Cancels processing and releases resources.</summary>
        public void Dispose()
        {
            _cts.Cancel();
            _signal.Release();
            try { _processingTask.Wait(); } catch { }
            _cts.Dispose();
            _signal.Dispose();
        }

        /// <summary>
        /// Thread-safe queue rejecting duplicates until dequeued.
        /// </summary>
        /// <typeparam name="TItem">Queue item type; must implement IEquatable&lt;TItem&gt; or use provided comparer.</typeparam>
        private class UniqueQueue<TItem> where TItem : IEquatable<TItem>
        {
            private readonly Queue<TItem> _queue;
            private readonly HashSet<TItem> _set;
            private readonly object _lock = new();

            /// <summary>
            /// Creates a queue using default equality (requires TItem : IEquatable&lt;TItem&gt;).
            /// </summary>
            public UniqueQueue() : this(null) { }

            /// <summary>
            /// Creates a queue using a custom comparer.
            /// </summary>
            /// <param name="comparer">Comparer for item uniqueness.</param>
            public UniqueQueue(IEqualityComparer<TItem>? comparer)
            {
                _queue = new Queue<TItem>();
                _set = new HashSet<TItem>(comparer ?? EqualityComparer<TItem>.Default);
            }

            /// <summary>
            /// Attempts to enqueue; returns false if already present.
            /// </summary>
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

            /// <summary>
            /// Attempts to dequeue; returns false if empty.
            /// </summary>
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
        }
    }
}
