namespace UnityPackageExplorer.PreviewHandler;

/// <summary>Transfers late task results and their temporary storage to completion cleanup.</summary>
internal static class DeferredTaskCleanup
{
    public static async Task<T> WaitAsync<T>(
        Task<T> operation,
        CancellationToken cancellationToken,
        Action<T>? disposeLateResult,
        Action cleanup,
        Action? onDeferredCleanup = null)
    {
        try { return await operation.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            try { onDeferredCleanup?.Invoke(); }
            catch { }
            ScheduleCleanup(operation, disposeLateResult, cleanup);
            throw;
        }
    }

    private static void ScheduleCleanup<T>(Task<T> operation, Action<T>? disposeLateResult, Action cleanup)
    {
        _ = operation.ContinueWith(
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
