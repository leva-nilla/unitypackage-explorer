using UnityPackageExplorer.PreviewHandler;

namespace UnityPackageExplorer.Tests;

public sealed class DeferredTaskCleanupTests
{
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
