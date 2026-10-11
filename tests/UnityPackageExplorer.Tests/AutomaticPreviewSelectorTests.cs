using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class AutomaticPreviewSelectorTests
{
    [Fact]
    public void SelectsSourceRelatedNonDemoPrefabWithoutReadingAssetPayloads()
    {
        var candidates = new[]
        {
            new UnityPackageEntry("demo", "Demo/Avatar.prefab", true, true),
            new UnityPackageEntry("unrelated", "Assets/Other.prefab", true, true),
            new UnityPackageEntry("related", "Assets/AvatarPackage.prefab", true, true),
            new UnityPackageEntry("missing", "Assets/AvatarMissing.prefab", false, true)
        };

        var selected = AutomaticPreviewSelector.Select(candidates, "AvatarPackage.zip");

        Assert.Equal("related", selected?.Guid);
    }

    [Fact]
    public void PrefersPrefabThenFbxThenSupportedImageAndSkipsUnsupportedImages()
    {
        var candidates = new[]
        {
            new UnityPackageEntry("psd", "Artwork.psd", true, true),
            new UnityPackageEntry("image", "Preview.png", true, true),
            new UnityPackageEntry("model", "Model.fbx", true, true)
        };

        Assert.Equal("model", AutomaticPreviewSelector.Select(candidates, "source.zip")?.Guid);
        Assert.Equal("image", AutomaticPreviewSelector.Select(candidates[..2], "source.zip")?.Guid);
        Assert.Null(AutomaticPreviewSelector.Select([candidates[0]], "source.zip"));
    }

    [Fact]
    public void DuplicatePrefabPathsInDifferentPackageMembersHaveStableMemberTieBreak()
    {
        var candidates = new[]
        {
            new UnityPackageEntry("same", "B.unitypackage/Assets/Avatar.prefab", true, true) { PackageMemberPath = "B.unitypackage" },
            new UnityPackageEntry("same", "A.unitypackage/Assets/Avatar.prefab", true, true) { PackageMemberPath = "A.unitypackage" }
        };

        Assert.Equal("A.unitypackage", AutomaticPreviewSelector.Select(candidates, "unrelated.zip")?.PackageMemberPath);
    }

    [Fact]
    public void PrefersShallowMainPrefabOverNestedPartsWithTheSameSourceName()
    {
        var candidates = new[]
        {
            new UnityPackageEntry("parts", "Assets/HeadAcc/Accessories/Parts/MAYO_HeadAcc.prefab", true, true),
            new UnityPackageEntry("main", "Assets/HeadAcc/MAYO_HeadAcc.prefab", true, true)
        };

        Assert.Equal("main", AutomaticPreviewSelector.Select(candidates, "MAYO_HeadAcc.zip")?.Guid);
    }

    [Fact]
    public void PrefersOutfitBodyOverSameDepthHeadAccessoryAndKeepsOnlyCandidateEligible()
    {
        var candidates = new[]
        {
            new UnityPackageEntry("head", "ALL Bandage.unitypackage/Assets/Prefab/HeadAcc/MAYO_HeadAcc.prefab", true, true)
                { PackageMemberPath = "ALL Bandage.unitypackage", AssetPathName = "Assets/Prefab/HeadAcc/MAYO_HeadAcc.prefab" },
            new UnityPackageEntry("body", "ALL Bandage.unitypackage/Assets/Prefab/MAYO/01.prefab", true, true)
                { PackageMemberPath = "ALL Bandage.unitypackage", AssetPathName = "Assets/Prefab/MAYO/01.prefab" }
        };

        Assert.Equal("body", AutomaticPreviewSelector.Select(candidates, "ALL Bandage.zip")?.Guid);
        Assert.Equal("head", AutomaticPreviewSelector.Select([candidates[0]], "ALL Bandage.zip")?.Guid);
    }
}
