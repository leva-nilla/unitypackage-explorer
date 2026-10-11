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

    [Fact]
    public void SelectingDeepPathExpandsOnlyItsAncestors()
    {
        var selected = new UnityPackageEntry("selected", "Archive/Assets/Artwork/Models/Avatar.prefab", true, true);
        var other = new UnityPackageEntry("other", "Archive/Assets/Artwork/Textures/face.png", true, true);
        var roots = AssetTreeItemView.CreateTree(UnityAssetCatalog.BuildTree([selected, other]), maximumFullyExpandedItems: 1);

        Assert.True(AssetTreeItemView.SelectPath(roots, selected.PathName));
        var archive = Assert.Single(roots);
        var assets = Assert.Single(archive.Children);
        var artwork = assets.Children.Single(x => x.Name == "Artwork");
        var models = artwork.Children.Single(x => x.Name == "Models");
        Assert.True(archive.IsExpanded);
        Assert.True(assets.IsExpanded);
        Assert.True(artwork.IsExpanded);
        Assert.True(models.IsExpanded);
        Assert.True(Assert.Single(models.Children).IsSelected);
        Assert.False(artwork.Children.Single(x => x.Name == "Textures").IsExpanded);

        var texture = artwork.Children.Single(x => x.Name == "Textures").Children.Single();
        Assert.True(AssetTreeItemView.SelectPath(roots, other.PathName));
        Assert.False(Assert.Single(models.Children).IsSelected);
        Assert.True(texture.IsSelected);
    }
}
