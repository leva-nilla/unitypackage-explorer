using UnityPackageExplorer.PreviewHandler;

namespace UnityPackageExplorer.Tests;

public sealed class PreviewStaDispatcherTests
{
    [Fact]
    public async Task CallsFromMtaAreSerializedOnStaAndDispatcherCanRestart()
    {
        var result = await Task.Run(() =>
        {
            var first = new PreviewStaDispatcher();
            var firstThread = first.Invoke(() =>
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                return Environment.CurrentManagedThreadId;
            });
            var sameThread = first.Invoke(() => Environment.CurrentManagedThreadId);
            var posted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(first.Post(() => posted.TrySetResult(Environment.CurrentManagedThreadId)));
            var postedThread = posted.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            first.Dispose();
            Assert.True(first.IsStopped);
            return (firstThread, sameThread, postedThread);
        });

        Assert.Equal(result.firstThread, result.sameThread);
        Assert.Equal(result.firstThread, result.postedThread);

        var restartedThread = await Task.Run(() =>
        {
            var restarted = new PreviewStaDispatcher();
            var threadId = restarted.Invoke(() =>
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                return Environment.CurrentManagedThreadId;
            });
            restarted.Dispose();
            Assert.True(restarted.IsStopped);
            return threadId;
        });
        Assert.NotEqual(result.firstThread, restartedThread);
    }
}
