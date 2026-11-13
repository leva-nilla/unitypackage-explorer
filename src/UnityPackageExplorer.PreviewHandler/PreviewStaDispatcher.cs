using System.Windows.Forms;

namespace UnityPackageExplorer.PreviewHandler;

/// <summary>Owns a message-pumped STA thread for all preview UI work.</summary>
internal sealed class PreviewStaDispatcher : IDisposable
{
    private readonly TaskCompletionSource<Control> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private Control? _marshalControl;
    private int _threadId;
    private int _stopping;

    public PreviewStaDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "UnityPackageExplorer Preview UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _marshalControl = _ready.Task.GetAwaiter().GetResult();
    }

    public bool CheckAccess() => Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);
    internal bool IsStopped => !_thread.IsAlive;

    public T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess()) return action();
        if (Volatile.Read(ref _stopping) != 0) throw new ObjectDisposedException(nameof(PreviewStaDispatcher));
        var control = _marshalControl ?? throw new ObjectDisposedException(nameof(PreviewStaDispatcher));
        return (T)control.Invoke(action)!;
    }

    public void Invoke(Action action) => Invoke(() => { action(); return true; });

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref _stopping) != 0) return false;
        var control = _marshalControl;
        if (control is null || control.IsDisposed || !control.IsHandleCreated) return false;
        try
        {
            control.BeginInvoke(action);
            return true;
        }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void Run()
    {
        Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => PreviewLog.Write("UI thread exception", args.Exception);
        try
        {
            var control = new Control();
            _ = control.Handle;
            _ready.TrySetResult(control);
            try { Application.Run(); }
            finally
            {
                // ElementHost creates a WPF dispatcher on this WinForms STA. Exiting only
                // the WinForms loop skips WPF shutdown (MediaContext/rendering cleanup).
                var wpfDispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (wpfDispatcher is not null && !wpfDispatcher.HasShutdownStarted)
                    wpfDispatcher.InvokeShutdown();
                control.Dispose();
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        var control = _marshalControl;
        _marshalControl = null;
        if (control is not null && _thread.IsAlive)
        {
            if (CheckAccess()) Application.ExitThread();
            else
            {
                try { control.BeginInvoke((Action)Application.ExitThread); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                _thread.Join();
            }
        }
        else if (_thread.IsAlive && !CheckAccess()) _thread.Join();
    }
}
