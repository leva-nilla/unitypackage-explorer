using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using UnityPackageExplorer.App;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class AssetTreeSelectionTests
{
    [Fact]
    public void BringsFarVirtualizedRootItemIntoViewAndSelectsItsContainer()
    {
        RunSta(() =>
        {
            var entries = Enumerable.Range(0, 5_000)
                .Select(i => new UnityPackageEntry($"guid-{i}", $"Assets/Models/Prefab-{i:D5}.prefab", true, true)).ToArray();
            var views = AssetTreeItemView.CreateTree(UnityAssetCatalog.BuildTree(entries), maximumFullyExpandedItems: 100);
            var tree = CreateVirtualizedTree();
            tree.ItemsSource = views;
            var window = new Window { Content = tree, Width = 360, Height = 320, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
            window.Show();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(tree.UpdateLayout));

            var target = views[0].Children[0].Children[^1];
            Assert.Null(FindContainer(tree, target));
            Assert.True(AssetTreeViewSelection(views, target, tree));
            var container = Assert.IsType<TreeViewItem>(FindContainer(tree, target));
            Assert.True(container.IsSelected);
            Assert.Same(target, tree.SelectedItem);
            Assert.True(container.IsVisible);
            window.Close();
        });
    }

    private static TreeView CreateVirtualizedTree()
    {
        var panelTemplate = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(PathVirtualizingStackPanel)));
        var itemStyle = new Style(typeof(TreeViewItem));
        itemStyle.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(AssetTreeItemView.IsExpanded)) { Mode = BindingMode.TwoWay }));
        itemStyle.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(AssetTreeItemView.IsSelected)) { Mode = BindingMode.TwoWay }));
        itemStyle.Setters.Add(new Setter(ItemsControl.ItemsPanelProperty, panelTemplate));
        var tree = new TreeView
        {
            ItemsPanel = panelTemplate,
            ItemContainerStyle = itemStyle,
            ItemTemplate = new HierarchicalDataTemplate(typeof(AssetTreeItemView))
            {
                ItemsSource = new Binding(nameof(AssetTreeItemView.Children))
            }
        };
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(tree, true);
        return tree;
    }

    private static bool AssetTreeViewSelection(IReadOnlyList<AssetTreeItemView> views, AssetTreeItemView target, TreeView tree)
    {
        Assert.True(AssetTreeItemView.SelectPath(views, target.PathName));
        return AssetTreeSelection.BringPathIntoView(tree, target);
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, AssetTreeItemView target)
    {
        foreach (var item in parent.Items.OfType<AssetTreeItemView>())
        {
            var container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            if (ReferenceEquals(item, target)) return container;
            if (container?.IsExpanded == true && FindContainer(container, target) is { } child) return child;
        }
        return null;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
