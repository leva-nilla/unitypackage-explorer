using UnityPackageExplorer.PreviewHandler;

namespace UnityPackageExplorer.Tests;

public sealed class DeferredTaskCleanupTests
{
    [Fact]
    public async Task StartedJobIsRegisteredBeforeUnloadCanTakeSnapshot()
    {
        var gate = new object();
        var tracked = new List<Task>();
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starter = Task.Run(() => DeferredTaskCleanup.StartTracked(gate, cancellation.Token, () =>
        {
            entered.Set();
            Assert.True(proceed.Wait(TimeSpan.FromSeconds(5)));
            return operation.Task;
        }, tracked.Add));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        var snapshot = Task.Run(() =>
        {
            lock (gate) return tracked.ToArray();
        });
        proceed.Set();
        var actual = await snapshot.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(operation.Task, Assert.Single(actual));
        operation.SetResult(42);
        Assert.Equal(42, await starter.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CancellationBeforeStartDoesNotCreateOrTrackJob()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var started = false;
        var tracked = false;
        OperationCanceledException? error = null;
        try
        {
            DeferredTaskCleanup.StartTracked(new object(), cancellation.Token, () =>
            {
                started = true;
                return Task.FromResult(1);
            }, _ => tracked = true);
        }
        catch (OperationCanceledException exception) { error = exception; }
        Assert.NotNull(error);
        Assert.False(started);
        Assert.False(tracked);
    }

    [Fact]
    public async Task CancellationReturnsImmediatelyAndLateResultOwnsTempUntilCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"UnityPackageExplorer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var resultDisposed = false;
        string? ownedRoot = root;
        var cleanupCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new TaskCompletionSource<TrackedResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var waiting = DeferredTaskCleanup.WaitAsync(
            operation.Task,
            cancellation.Token,
            result => result.Dispose(),
            () =>
            {
                Directory.Delete(root, recursive: true);
                cleanupCompleted.TrySetResult();
            },
            onDeferredCleanup: () => ownedRoot = null);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(Directory.Exists(root));
        Assert.Null(ownedRoot);
        Assert.False(resultDisposed);
        var result = new TrackedResult(() => resultDisposed = true);
        operation.SetResult(result);
        await cleanupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(resultDisposed);
        Assert.False(Directory.Exists(root));
    }

    private sealed class TrackedResult(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
