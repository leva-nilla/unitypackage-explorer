namespace UnityPackageExplorer.PreviewHandler;

/// <summary>Owns disposable results until the queued UI callback explicitly takes them.</summary>
internal sealed class PendingUiPayloadQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<IDisposable> _pending = [];
    private bool _closed;

    public PendingUiPayload<T> Enqueue<T>(T payload, Action<T> consume) where T : class, IDisposable
    {
        var lease = new PendingUiPayload<T>(payload, consume, OnFinished);
        lock (_gate)
        {
            if (!_closed)
            {
                _pending.Add(lease);
                return lease;
            }
        }
        lease.Dispose();
        return lease;
    }

    private void OnFinished(IDisposable lease)
    {
        lock (_gate) _pending.Remove(lease);
    }

    public void Dispose()
    {
        IDisposable[] pending;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            pending = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var lease in pending) lease.Dispose();
    }
}

internal sealed class PendingUiPayload<T> : IDisposable where T : class, IDisposable
{
    private readonly Action<T> _consume;
    private readonly Action<IDisposable> _onFinished;
    private T? _payload;
    private int _state; // 0 pending, 1 transferred to callback, 2 disposed

    internal PendingUiPayload(T payload, Action<T> consume, Action<IDisposable> onFinished)
    {
        _payload = payload;
        _consume = consume;
        _onFinished = onFinished;
    }

    public bool IsPending => Volatile.Read(ref _state) == 0;

    public bool TryConsume()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
        var payload = Interlocked.Exchange(ref _payload, default);
        try
        {
            if (payload is not null) _consume(payload);
            return true;
        }
        catch
        {
            payload?.Dispose();
            throw;
        }
        finally { _onFinished(this); }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return;
        var payload = Interlocked.Exchange(ref _payload, default);
        try { payload?.Dispose(); }
        finally { _onFinished(this); }
    }
}
