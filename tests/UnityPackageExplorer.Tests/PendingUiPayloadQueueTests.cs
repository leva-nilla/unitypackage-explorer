using UnityPackageExplorer.PreviewHandler;

namespace UnityPackageExplorer.Tests;

public sealed class PendingUiPayloadQueueTests
{
    [Fact]
    public void DisposeBeforeQueuedCallbackRuns_ReleasesPayloadAndMakesCallbackNoOp()
    {
        var queue = new PendingUiPayloadQueue();
        var payload = new TrackedPayload();
        var consumed = 0;
        var lease = queue.Enqueue(payload, _ => consumed++);
        Action queuedUiCallback = () => lease.TryConsume();

        // Native rendering has completed and BeginInvoke accepted the delegate,
        // but Explorer unloads the preview before the UI dispatcher runs it.
        queue.Dispose();
        queuedUiCallback();

        Assert.Equal(1, payload.DisposeCount);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public async Task CallbackRacingUnload_DisposesPayloadExactlyOnce()
    {
        var queue = new PendingUiPayloadQueue();
        var payload = new TrackedPayload();
        var lease = queue.Enqueue(payload, item => item.Dispose());

        await Task.WhenAll(
            Task.Run(() => lease.TryConsume()),
            Task.Run(queue.Dispose));

        Assert.Equal(1, payload.DisposeCount);
    }

    private sealed class TrackedPayload : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
