using UnityPackageExplorer.App;

namespace UnityPackageExplorer.Tests;

public sealed class RetirableResourceTests
{
    [Fact]
    public void ReplacementWaitsForPreviewAndLilToonOperations()
    {
        var resource = new CountedResource();
        var owner = new RetirableResource<CountedResource>(resource);
        var preview = owner.Acquire();
        var lilToon = owner.Acquire();

        owner.Retire();
        owner.Retire();
        Assert.Equal(0, resource.Disposals);
        Assert.Throws<ObjectDisposedException>(() => owner.Acquire());
        preview.Dispose();
        preview.Dispose();
        Assert.Equal(0, resource.Disposals);
        lilToon.Dispose();
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public void ReplacementWithoutActiveOperationsDisposesImmediately()
    {
        var resource = new CountedResource();
        var owner = new RetirableResource<CountedResource>(resource);
        owner.Retire();
        Assert.Equal(1, resource.Disposals);
    }

    private sealed class CountedResource : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
