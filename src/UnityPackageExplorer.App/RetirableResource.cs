namespace UnityPackageExplorer.App;

/// <summary>Keeps a replaced resource alive until all outstanding operations finish.</summary>
internal sealed class RetirableResource<T>(T value) where T : class, IDisposable
{
    private readonly object _gate = new();
    private T? _value = value;
    private int _leases;
    private bool _retired;

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_retired) throw new ObjectDisposedException(nameof(RetirableResource<T>));
            _leases++;
            return new Lease(this);
        }
    }

    public void Retire()
    {
        T? dispose = null;
        lock (_gate)
        {
            if (_retired) return;
            _retired = true;
            if (_leases == 0) { dispose = _value; _value = null; }
        }
        dispose?.Dispose();
    }

    private void Release()
    {
        T? dispose = null;
        lock (_gate)
        {
            if (--_leases == 0 && _retired) { dispose = _value; _value = null; }
        }
        dispose?.Dispose();
    }

    private sealed class Lease(RetirableResource<T> owner) : IDisposable
    {
        private RetirableResource<T>? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
