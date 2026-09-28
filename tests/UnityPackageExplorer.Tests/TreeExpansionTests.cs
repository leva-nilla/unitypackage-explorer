using UnityPackageExplorer.App;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class TreeExpansionTests
{
    [Fact]
    public void AssetTreeExpandsOnlyTopLevelItemsByDefault()
    {
        var grandchild = new AssetTreeItem("file", "root/child/file", null, []);
        var child = new AssetTreeItem("child", "root/child", null, [grandchild]);
        var root = new AssetTreeItem("root", "root", null, [child]);

        var view = new AssetTreeItemView(root);

        Assert.True(view.IsExpanded);
        Assert.False(view.Children[0].IsExpanded);
        Assert.False(view.Children[0].Children[0].IsExpanded);
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
