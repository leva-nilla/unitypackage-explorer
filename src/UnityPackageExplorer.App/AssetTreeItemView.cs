using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.App;

public sealed class AssetTreeItemView(AssetTreeItem item, bool isRoot = true)
{
    public string Name => item.Entry is null ? item.Name : $"{item.Name}  ·  {item.Entry.Kind}";
    public string PathName => item.PathName;
    public UnityPackageEntry? Entry => item.Entry;
    public bool IsExpanded { get; set; } = isRoot;
    public IReadOnlyList<AssetTreeItemView> Children { get; } = item.Children.Select(x => new AssetTreeItemView(x, false)).ToArray();
}

public sealed class PrefabNodeView(PrefabNode node, bool isRoot = true)
{
    public string DisplayName => node.Components.Count == 0 ? node.Name : $"{node.Name}  ·  {string.Join(", ", node.Components)}";
    public bool IsExpanded { get; set; } = isRoot;
    public IReadOnlyList<PrefabNodeView> Children { get; } = node.Children.Select(x => new PrefabNodeView(x, false)).ToArray();
}
