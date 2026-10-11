using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityMaterialPreviewPolicyTests
{
    [Fact]
    public void OpaqueColorAlphaDoesNotMakeMaterialTransparent()
    {
        var color = new UnityMaterialColor(0.4f, 0.5f, 0.6f, 0.2f);
        Assert.Equal(1f, UnityMaterialPreviewPolicy.DisplayAlpha(color, UnityMaterialRenderMode.Opaque));
        Assert.False(UnityMaterialPreviewPolicy.UsesTransparentPass(UnityMaterialRenderMode.Opaque));
    }

    [Fact]
    public void ExplicitTransparentAndCutoutModesHaveDistinctPolicies()
    {
        Assert.Equal(0.45f, UnityMaterialPreviewPolicy.DisplayAlpha(new UnityMaterialColor(1, 1, 1, 0.45f), UnityMaterialRenderMode.Transparent));
        Assert.True(UnityMaterialPreviewPolicy.UsesTransparentPass(UnityMaterialRenderMode.Transparent));
        Assert.True(UnityMaterialPreviewPolicy.UsesTransparentPass(UnityMaterialRenderMode.Cutout));
        Assert.Equal(0.37f, UnityMaterialPreviewPolicy.CutoutThreshold(UnityMaterialRenderMode.Cutout, 0.37f));
        Assert.Null(UnityMaterialPreviewPolicy.CutoutThreshold(UnityMaterialRenderMode.Opaque, 0.37f));
        Assert.Null(UnityMaterialPreviewPolicy.CutoutThreshold(UnityMaterialRenderMode.Cutout, null));
    }

    [Fact]
    public void MaterialMatchingReturnsEveryCandidateSoCallerCanRejectAmbiguity()
    {
        var candidates = new[]
        {
            ("material-a", "PouchCanvas", (string?)"Canvas"),
            ("material-b", "PouchCanvas", (string?)"Canvas"),
            ("unrelated", "Metal", (string?)"MetalTexture")
        };
        var matches = UnityMaterialNameMatcher.FindMatchingMaterialGuids(candidates, "PouchCanvas", "Pouch");
        Assert.Equal(["material-a", "material-b"], matches);
        Assert.Equal("material-a", Assert.Single(UnityMaterialNameMatcher.FindMatchingMaterialGuids(candidates[..1], "PouchCanvas", "Pouch")));
        Assert.Empty(UnityMaterialNameMatcher.FindMatchingMaterialGuids(candidates, "UnrelatedMesh", "UnrelatedMesh"));
    }
}
