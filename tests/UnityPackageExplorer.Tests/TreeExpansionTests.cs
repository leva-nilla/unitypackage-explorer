using UnityPackageExplorer.App;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class TreeExpansionTests
{
    [Fact]
    public void SmallNestedArchiveTreesExpandAllFoldersByDefault()
    {
        var entry = new UnityPackageEntry("guid", "Luminous China/Luminous China.unitypackage/Assets/Clothing/coat.fbx", true, true);
        var root = Assert.Single(UnityAssetCatalog.BuildTree([entry]));

        var view = Assert.Single(AssetTreeItemView.CreateTree([root]));

        Assert.True(view.IsExpanded);
        Assert.True(view.Children[0].IsExpanded);
        Assert.True(view.Children[0].Children[0].IsExpanded);
        Assert.True(view.Children[0].Children[0].Children[0].IsExpanded);
    }

    [Fact]
    public void LargeAssetTreesExpandArchiveAndAssetsRootsButKeepDeeperFoldersCollapsed()
    {
        var entry = new UnityPackageEntry("guid", "Luminous China/Luminous China.unitypackage/Assets/Clothing/coat.fbx", true, true);
        var root = Assert.Single(UnityAssetCatalog.BuildTree([entry]));

        var view = Assert.Single(AssetTreeItemView.CreateTree([root], maximumFullyExpandedItems: 1));

        Assert.True(view.IsExpanded);
        Assert.True(view.Children[0].IsExpanded);
        Assert.True(view.Children[0].Children[0].IsExpanded);
        Assert.False(view.Children[0].Children[0].Children[0].IsExpanded);
    }

    [Fact]
    public void PrefabTreeExpandsOnlyTopLevelNodesByDefault()
    {
        var grandchild = new PrefabNode { FileId = 3, Name = "Grandchild" };
        var child = new PrefabNode { FileId = 2, Name = "Child" };
        child.Children.Add(grandchild);
        var root = new PrefabNode { FileId = 1, Name = "Root" };
        root.Children.Add(child);

        var view = new PrefabNodeView(root);

        Assert.True(view.IsExpanded);
        Assert.False(view.Children[0].IsExpanded);
        Assert.False(view.Children[0].Children[0].IsExpanded);
    }
}
