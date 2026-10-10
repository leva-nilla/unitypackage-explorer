using UnityPackageExplorer.App;

namespace UnityPackageExplorer.Tests;

public sealed class PreviewImporterConfigurationTests
{
    [Fact]
    public void EnablesSkeletonCreationForSkinnedFbxPreview()
    {
        var configuration = PreviewImporterConfiguration.Create();

        Assert.True(configuration.CreateSkeletonForBoneSkinningMesh);
    }
}
