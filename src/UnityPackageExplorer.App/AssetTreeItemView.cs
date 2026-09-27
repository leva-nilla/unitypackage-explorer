using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.App;

public sealed class AssetTreeItemView(AssetTreeItem item)
{
    public string Name => item.Entry is null ? item.Name : $"{item.Name}  ·  {item.Entry.Kind}";
    public string PathName => item.PathName;
    public UnityPackageEntry? Entry => item.Entry;
    public IReadOnlyList<AssetTreeItemView> Children { get; } = item.Children.Select(x => new AssetTreeItemView(x)).ToArray();
}

public sealed class PrefabNodeView(PrefabNode node)
{
    public string DisplayName => node.Components.Count == 0 ? node.Name : $"{node.Name}  ·  {string.Join(", ", node.Components)}";
    public IReadOnlyList<PrefabNodeView> Children { get; } = node.Children.Select(x => new PrefabNodeView(x)).ToArray();
}
