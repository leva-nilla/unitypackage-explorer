using System.Runtime.InteropServices;
using System.Diagnostics;

namespace UnityPackageExplorer.PreviewHandler;

/// <summary>Keeps the COM surrogate alive until canceled workers have released their storage.</summary>
internal sealed class PreviewProcessLease : IDisposable
{
    private Action? _release;

    internal PreviewProcessLease(Action acquire, Action release)
    {
        acquire();
        _release = release;
    }

    public static IDisposable? Acquire(object owner, Action<Exception>? logFailure = null)
    {
        // Do not change the process lifetime when the component is used in-process by a test/app.
        using var process = Process.GetCurrentProcess();
        if (!string.Equals(process.ProcessName, "prevhost", StringComparison.OrdinalIgnoreCase))
            return null;
        // In-process agile/GIT references may bypass standard marshaling. A LOCAL
        // TABLESTRONG packet explicitly pins the surrogate's marshaled identity.
        // Its COM apartment must survive until CoReleaseMarshalData, independently
        // of the disposable UI STA and the external client's lifetime.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var thread = new Thread(() => HoldMarshaledReference(owner, ready, release, logFailure))
        {
            IsBackground = true,
            Name = "UnityPackageExplorer request lifetime"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        try { thread.Start(); }
        catch { release.Dispose(); throw; }
        ready.Task.GetAwaiter().GetResult();
        return new PreviewProcessLease(() => { }, release.Set);
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    private static void HoldMarshaledReference(object owner, TaskCompletionSource ready, ManualResetEventSlim release, Action<Exception>? logFailure)
    {
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            Marshal.ThrowExceptionForHR(initialized);
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream));
            try
            {
                var unknown = Marshal.GetIUnknownForObject(owner);
                try
                {
                    // MSHCTX_LOCAL=0, MSHLFLAGS_TABLESTRONG=1.
                    Marshal.ThrowExceptionForHR(CoMarshalInterface(stream, in IidUnknown, unknown, 0, IntPtr.Zero, 1));
                }
                finally { Marshal.Release(unknown); }
                ready.TrySetResult();
                release.Wait();
                stream.Seek(0, 0, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(CoReleaseMarshalData(stream));
            }
            finally { Marshal.ReleaseComObject(stream); }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            // Keep release faults observable without crashing the process.
            logFailure?.Invoke(ex);
        }
        finally
        {
            release.Dispose();
            if (initialized >= 0) CoUninitialize();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(IntPtr memory, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out System.Runtime.InteropServices.ComTypes.IStream stream);
    [DllImport("ole32.dll")]
    private static extern int CoMarshalInterface(System.Runtime.InteropServices.ComTypes.IStream stream, in Guid interfaceId, IntPtr unknown, uint context, IntPtr contextData, uint flags);
    [DllImport("ole32.dll")]
    private static extern int CoReleaseMarshalData(System.Runtime.InteropServices.ComTypes.IStream stream);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

}
