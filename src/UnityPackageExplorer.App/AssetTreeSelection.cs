using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UnityPackageExplorer.App;

public static class AssetTreeSelection
{
    public static bool BringPathIntoView(TreeView tree, AssetTreeItemView target)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(target);
        var path = new List<AssetTreeItemView>();
        if (!FindPath(tree.Items.OfType<AssetTreeItemView>(), target, path)) return false;

        ItemsControl owner = tree;
        TreeViewItem? selectedContainer = null;
        foreach (var item in path)
        {
            owner.ApplyTemplate();
            var panel = FindItemsPanel(owner);
            if (panel is null)
            {
                // Newly expanded ancestors may not have applied their item template yet.
                owner.UpdateLayout();
                panel = FindItemsPanel(owner);
            }
            if (panel is null) return false;
            var index = owner.Items.IndexOf(item);
            if (index < 0) return false;

            panel.BringItemIntoView(index);
            selectedContainer = owner.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            if (selectedContainer is null)
            {
                // The selected ancestor alone is laid out once after the virtualizing panel request.
                owner.UpdateLayout();
                selectedContainer = owner.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            }
            if (selectedContainer is null) return false;
            selectedContainer.IsExpanded = item.IsExpanded;
            owner = selectedContainer;
        }

        selectedContainer!.IsSelected = true;
        selectedContainer.BringIntoView();
        return true;
    }

    private static bool FindPath(IEnumerable<AssetTreeItemView> items, AssetTreeItemView target, List<AssetTreeItemView> path)
    {
        foreach (var item in items)
        {
            path.Add(item);
            if (ReferenceEquals(item, target) || FindPath(item.Children, target, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private static PathVirtualizingStackPanel? FindItemsPanel(ItemsControl owner)
    {
        var presenter = FindVisualChild<ItemsPresenter>(owner);
        if (presenter is null) return null;
        presenter.ApplyTemplate();
        return VisualTreeHelper.GetChildrenCount(presenter) > 0 ? VisualTreeHelper.GetChild(presenter, 0) as PathVirtualizingStackPanel : null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
