namespace UnityPackageExplorer.PreviewHandler;

/// <summary>Transfers late task results and their temporary storage to completion cleanup.</summary>
internal static class DeferredTaskCleanup
{
    /// <summary>Never blocks the COM/UI caller; the process lease outlives every worker and cleanup.</summary>
    public static Task ReleaseAfterAsync(Task[] operations, Action cleanup, IDisposable? processLease)
        => Task.Run(async () =>
        {
            try
            {
                try { await Task.WhenAll(operations).ConfigureAwait(false); }
                catch { /* Faults/cancellation must not skip storage cleanup. */ }
                cleanup();
            }
            finally { processLease?.Dispose(); }
        });

    public static Task<T> StartTracked<T>(object gate, CancellationToken token, Func<Task<T>> start, Action<Task> track)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var job = start();
            track(job);
            return job;
        }
    }

    public static async Task<T> WaitAsync<T>(
        Task<T> operation,
        CancellationToken cancellationToken,
        Action<T>? disposeLateResult,
        Action cleanup,
        Action? onDeferredCleanup = null,
        bool awaitCleanup = false)
    {
        try { return await operation.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            try { onDeferredCleanup?.Invoke(); }
            catch { }
            var completion = ScheduleCleanup(operation, disposeLateResult, cleanup);
            if (awaitCleanup) await completion.ConfigureAwait(false);
            throw;
        }
    }

    private static Task ScheduleCleanup<T>(Task<T> operation, Action<T>? disposeLateResult, Action cleanup)
    {
        return operation.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion) disposeLateResult?.Invoke(completed.Result);
                }
                catch { }
                finally
                {
                    try { cleanup(); }
                    catch { }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

}
