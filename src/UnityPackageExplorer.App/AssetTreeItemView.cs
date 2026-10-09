using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.App;

public sealed class AssetTreeItemView
{
    private const int DefaultFullyExpandedTreeLimit = 2_000;
    private const int ShallowExpansionDepth = 3;

    public static IReadOnlyList<AssetTreeItemView> CreateTree(
        IReadOnlyList<AssetTreeItem> roots,
        int maximumFullyExpandedItems = DefaultFullyExpandedTreeLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFullyExpandedItems);
        var expandAll = CountItems(roots, maximumFullyExpandedItems, cancellationToken) <= maximumFullyExpandedItems;
        return roots.Select(x => new AssetTreeItemView(x, expandAll, 0, cancellationToken)).ToArray();
    }

    private AssetTreeItemView(AssetTreeItem item, bool expandAll, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Name = item.Entry is null ? item.Name : $"{item.Name}  ·  {item.Entry.Kind}";
        PathName = item.PathName;
        Entry = item.Entry;
        IsExpanded = expandAll || depth < ShallowExpansionDepth;
        Children = item.Children.Select(x => new AssetTreeItemView(x, expandAll, depth + 1, cancellationToken)).ToArray();
    }

    private static int CountItems(IReadOnlyList<AssetTreeItem> roots, int stopAfter, CancellationToken cancellationToken)
    {
        var pending = new Stack<AssetTreeItem>(roots);
        var count = 0;
        while (pending.Count > 0 && count <= stopAfter)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = pending.Pop();
            count++;
            foreach (var child in item.Children) pending.Push(child);
        }
        return count;
    }

    public string Name { get; }
    public string PathName { get; }
    public UnityPackageEntry? Entry { get; }
    public bool IsExpanded { get; set; }
    public IReadOnlyList<AssetTreeItemView> Children { get; }
}

public sealed class PrefabNodeView
{
    public PrefabNodeView(PrefabNode node, bool isRoot = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisplayName = node.Components.Count == 0 ? node.Name : $"{node.Name}  ·  {string.Join(", ", node.Components)}";
        IsExpanded = isRoot;
        Children = node.Children.Select(x => new PrefabNodeView(x, false, cancellationToken)).ToArray();
    }

    public string DisplayName { get; }
    public bool IsExpanded { get; set; }
    public IReadOnlyList<PrefabNodeView> Children { get; }
}
