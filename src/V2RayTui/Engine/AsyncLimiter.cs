namespace V2RayTui.Engine;

/// <summary>
/// Async concurrency limiter whose limit can be changed at runtime (settings are editable while tests run).
/// Shared by all test jobs, so a manual test and a background test never exceed the global budget together.
/// </summary>
public sealed class AsyncLimiter(int limit)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    private int _limit = Math.Max(1, limit);
    private int _inUse;

    public int Limit
    {
        get => _limit;
        set
        {
            List<TaskCompletionSource> wake;
            lock (_gate)
            {
                _limit = Math.Max(1, value);
                wake = DequeueRunnable();
            }
            wake.ForEach(t => t.TrySetResult());
        }
    }

    public int InUse => Volatile.Read(ref _inUse);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        TaskCompletionSource tcs;
        LinkedListNode<TaskCompletionSource> node;
        lock (_gate)
        {
            if (_inUse < _limit && _waiters.Count == 0)
            {
                _inUse++;
                return new Releaser(this);
            }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(tcs);
        }

        await using (ct.Register(() =>
        {
            bool removed;
            lock (_gate)
            {
                removed = node.List != null;
                if (removed)
                {
                    _waiters.Remove(node);
                }
            }
            if (removed)
            {
                tcs.TrySetCanceled(ct);
            }
        }))
        {
            await tcs.Task.ConfigureAwait(false);
        }
        return new Releaser(this);
    }

    private void Release()
    {
        List<TaskCompletionSource> wake;
        lock (_gate)
        {
            _inUse--;
            wake = DequeueRunnable();
        }
        wake.ForEach(t => t.TrySetResult());
    }

    // Caller holds _gate. Slots are handed over (counted) before the waiter is woken.
    private List<TaskCompletionSource> DequeueRunnable()
    {
        var list = new List<TaskCompletionSource>();
        while (_inUse < _limit && _waiters.First is { } first)
        {
            _waiters.RemoveFirst();
            _inUse++;
            list.Add(first.Value);
        }
        return list;
    }

    private sealed class Releaser(AsyncLimiter owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
