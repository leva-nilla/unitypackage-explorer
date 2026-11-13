using System.Reflection;
using HelixToolkit.SharpDX.Assimp;
using UnityPackageExplorer.Rendering;

namespace UnityPackageExplorer.Tests;

public sealed class GeometryPreviewLifetimeTests
{
    [Fact]
    public void StagingCleanupWaitsForMaterialReaderAndRunsOnce()
    {
        var preview = (GeometryPreview)Activator.CreateInstance(
            typeof(GeometryPreview),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [Array.Empty<HelixToolkitScene>(), Array.Empty<GeometryPendingMaterials>(), Array.Empty<string>()],
            culture: null)!;
        var begin = typeof(GeometryPreview).GetMethod("BeginMaterials", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var end = typeof(GeometryPreview).GetMethod("EndMaterials", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cleanups = 0;
        begin.Invoke(preview, null);

        preview.DisposeAfterRelease(() => cleanups++);
        preview.Dispose();
        Assert.True(preview.IsDisposed);
        Assert.Equal(0, cleanups);
        end.Invoke(preview, null);
        Assert.Equal(1, cleanups);
        preview.Dispose();
        Assert.Equal(1, cleanups);
    }
}
