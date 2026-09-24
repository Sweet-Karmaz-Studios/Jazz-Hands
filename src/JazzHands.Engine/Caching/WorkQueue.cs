using Serilog;

namespace JazzHands.Engine.Caching;

/// <summary>How soon a piece of background work is wanted.</summary>
public enum WorkPriority
{
    /// <summary>On screen now: someone is looking at a placeholder.</summary>
    Visible = 0,

    /// <summary>Just off screen, where a scroll goes next.</summary>
    Near = 1,

    /// <summary>Everything else: filling in while nobody waits.</summary>
    Idle = 2,
}

/// <summary>
/// Background work in priority order on a few threads of their own: thumbnails and waveforms.
/// </summary>
/// <remarks>
/// <para>
/// Work is keyed. Asking again for work that is waiting does not queue it twice; asking at a
/// higher priority moves it up. That is what lets the timeline ask for everything it can see on
/// every frame it draws, which is the simple thing to do, without flooding the queue.
/// </para>
/// <para>
/// Work that scrolled out of view is dropped lazily: each item can carry a test of whether it is
/// still wanted, run when a worker takes it. The timeline's test is "asked for in the last half
/// second", so a fling across an hour of footage leaves nothing behind but the place it stopped.
/// </para>
/// <para>
/// The workers run below normal priority, so playback, decoding ahead and the UI thread always
/// come first. Each has a <see cref="WorkerContext"/> for things that are expensive to open and
/// thread affine, decoders above all, kept between items and closed when the queue has been idle
/// for a few seconds.
/// </para>
/// </remarks>
public sealed class WorkQueue : IDisposable
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(2);

    private readonly ILogger _log = Log.ForContext<WorkQueue>();
    private readonly object _gate = new();
    private readonly PriorityQueue<Item, (int Priority, long Order)> _queue = new();
    private readonly Dictionary<string, Item> _waiting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Thread[] _threads;
    private readonly CancellationTokenSource _stop = new();
    private long _order;
    private long _release;
    private int _released;
    private bool _disposed;

    /// <summary>Starts the workers.</summary>
    /// <param name="workers">How many threads.</param>
    /// <param name="name">What the threads are called in a debugger.</param>
    /// <param name="priority">
    /// Below normal in the editor, so playback and the UI always come first. A test that waits on
    /// the workers while the rest of its process renders on WARP runs them at normal, or they starve.
    /// </param>
    public WorkQueue(int workers, string name, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _threads = new Thread[workers];
        for (int index = 0; index < workers; index++)
        {
            _threads[index] = new Thread(Run)
            {
                IsBackground = true,
                Name = $"{name} {index + 1}",
                Priority = priority,
            };
            _threads[index].Start();
        }
    }

    /// <summary>
    /// Half the cores, at most four. Each worker can hold a 4K decoder open, which is a few hundred
    /// megabytes, and past four the disk is the limit anyway.
    /// </summary>
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Items waiting.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    /// <summary>Items being worked on.</summary>
    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>Items finished.</summary>
    public long Completed { get; private set; }

    /// <summary>Items dropped because nobody wanted them by the time a worker got to them.</summary>
    public long Dropped { get; private set; }

    /// <summary>Items that threw.</summary>
    public long Failed { get; private set; }

    /// <summary>
    /// Queues work, or moves it up if it is already waiting at a lower priority.
    /// </summary>
    /// <param name="key">What the work is, for asking again.</param>
    /// <param name="priority">How soon it is wanted.</param>
    /// <param name="work">The work, handed its worker's resources and a token that fires on shutdown or cancel.</param>
    /// <param name="stillWanted">Asked when a worker takes the item; false drops it. Null keeps it always.</param>
    /// <returns>True when it was queued or moved up; false when it was already waiting as high or running.</returns>
    public bool Enqueue(string key, WorkPriority priority, Action<WorkerContext, CancellationToken> work, Func<bool>? stillWanted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_running.Contains(key))
            {
                return false;
            }

            if (_waiting.TryGetValue(key, out Item? existing))
            {
                if (existing.Priority <= priority)
                {
                    return false;
                }

                // Moved up: the old entry stays in the heap and is skipped when it surfaces.
                existing.Superseded = true;
            }

            var item = new Item(key, priority, work, stillWanted);
            _waiting[key] = item;
            _queue.Enqueue(item, ((int)priority, _order++));

            // All, not one: Release and WaitIdle wait on this monitor too, and a single pulse that
            // woke one of them would leave the item for a worker's idle timeout.
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>True when the work is waiting or running.</summary>
    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _waiting.ContainsKey(key) || _running.Contains(key);
        }
    }

    /// <summary>Drops waiting items whose key matches. Running ones finish.</summary>
    /// <returns>How many were dropped.</returns>
    public int Cancel(Func<string, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        lock (_gate)
        {
            var keys = _waiting.Keys.Where(match).ToList();
            foreach (string key in keys)
            {
                _waiting[key].Superseded = true;
                _waiting.Remove(key);
            }

            return keys.Count;
        }
    }

    /// <summary>
    /// Has every worker close what it holds open (the files its decoders have open, above all)
    /// and waits until they have. A busy worker closes when its current item is done.
    /// </summary>
    /// <remarks>
    /// Windows will not move, rename or delete a file FFmpeg has open, and a worker keeps its
    /// decoders for a couple of seconds after its last item so a burst of work opens each file
    /// once. Anything about to move a media file, and the tests that do, call this first.
    /// </remarks>
    /// <returns>False when a worker was still busy at the timeout.</returns>
    public bool Release(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        lock (_gate)
        {
            _release++;
            _released = 0;
            Monitor.PulseAll(_gate);

            while (_released < _threads.Length)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || _disposed)
                {
                    return false;
                }

                Monitor.Wait(_gate, left);
            }

            return true;
        }
    }

    /// <summary>Waits until nothing is waiting or running, for tests and benchmarks.</summary>
    /// <returns>False on timeout.</returns>
    public bool WaitIdle(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        lock (_gate)
        {
            while (_waiting.Count > 0 || _running.Count > 0)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    return false;
                }

                Monitor.Wait(_gate, left);
            }

            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
            Monitor.PulseAll(_gate);
        }

        foreach (Thread thread in _threads)
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        _stop.Dispose();
    }

    private void Run()
    {
        using var context = new WorkerContext();
        long released = 0;
        CancellationToken stopping = _stop.Token;

        while (true)
        {
            Item? item = Take(context, ref released);
            if (item is null)
            {
                return;
            }

            try
            {
                item.Work(context, stopping);
                lock (_gate)
                {
                    Completed++;
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                lock (_gate)
                {
                    Failed++;
                }

                _log.Warning(error, "Background work {Key} failed", item.Key);

                // A decoder that threw may be in any state; the next item opens a fresh one.
                context.Clear();
            }
            finally
            {
                lock (_gate)
                {
                    _running.Remove(item.Key);
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    /// <summary>The next item worth doing, waiting for one; null on shutdown.</summary>
    private Item? Take(WorkerContext context, ref long released)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_disposed)
                {
                    return null;
                }

                if (released != _release)
                {
                    context.Clear();
                    released = _release;
                    _released++;
                    Monitor.PulseAll(_gate);
                }

                while (_queue.TryDequeue(out Item? item, out _))
                {
                    if (item.Superseded)
                    {
                        continue;
                    }

                    _waiting.Remove(item.Key);

                    if (item.StillWanted is { } wanted && !wanted())
                    {
                        Dropped++;
                        Monitor.PulseAll(_gate);
                        continue;
                    }

                    _running.Add(item.Key);
                    return item;
                }

                // Nothing to do: close what this worker holds open if it stays that way.
                if (!Monitor.Wait(_gate, IdleClose))
                {
                    context.Clear();
                }
            }
        }
    }

    private sealed class Item(string key, WorkPriority priority, Action<WorkerContext, CancellationToken> work, Func<bool>? stillWanted)
    {
        public string Key { get; } = key;

        public WorkPriority Priority { get; } = priority;

        public Action<WorkerContext, CancellationToken> Work { get; } = work;

        public Func<bool>? StillWanted { get; } = stillWanted;

        public bool Superseded { get; set; }
    }
}

/// <summary>
/// What one worker keeps open between items: decoders, mostly. Thread affine, like them.
/// </summary>
public sealed class WorkerContext : IDisposable
{
    /// <summary>How many things one worker keeps open at once.</summary>
    public const int Capacity = 4;

    private readonly List<(string Key, IDisposable Value)> _open = [];

    /// <summary>How many things are open.</summary>
    public int Count => _open.Count;

    /// <summary>Something kept open under a key, opening it if it is not; the least recently used goes past the capacity.</summary>
    public T Get<T>(string key, Func<T> open)
        where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(open);

        for (int index = 0; index < _open.Count; index++)
        {
            if (string.Equals(_open[index].Key, key, StringComparison.Ordinal) && _open[index].Value is T found)
            {
                // Most recently used at the end.
                (string Key, IDisposable Value) entry = _open[index];
                _open.RemoveAt(index);
                _open.Add(entry);
                return found;
            }
        }

        T opened = open();
        _open.Add((key, opened));

        while (_open.Count > Capacity)
        {
            _open[0].Value.Dispose();
            _open.RemoveAt(0);
        }

        return opened;
    }

    /// <summary>Closes one thing, if it is open.</summary>
    public void Close(string key)
    {
        int index = _open.FindIndex(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
        if (index >= 0)
        {
            _open[index].Value.Dispose();
            _open.RemoveAt(index);
        }
    }

    /// <summary>Closes everything.</summary>
    public void Clear()
    {
        foreach ((_, IDisposable value) in _open)
        {
            value.Dispose();
        }

        _open.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Clear();
}
