using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class PreviewModePolicyTests
{
    [Fact]
    public void CanReturnToLightweightWhilePreviewIsBusyAndLilToonIsActive()
    {
        Assert.True(PreviewModePolicy.CanReturnToLightweight(
            hasActiveScene: true,
            isOpeningSource: false,
            isPreviewBusy: true,
            isLilToonMode: true,
            isLilToonLoading: false));
    }

    [Fact]
    public void DisablesLightweightReturnWhenSourceOpeningInvalidatesScene()
    {
        Assert.False(PreviewModePolicy.CanReturnToLightweight(
            hasActiveScene: true,
            isOpeningSource: true,
            isPreviewBusy: false,
            isLilToonMode: true,
            isLilToonLoading: false));
    }
}
