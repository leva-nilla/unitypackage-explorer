using System.Windows.Controls;

namespace UnityPackageExplorer.App;

/// <summary>Exposes WPF's targeted virtualized-item realization for tree path navigation.</summary>
public sealed class PathVirtualizingStackPanel : VirtualizingStackPanel
{
    public void BringItemIntoView(int index) => BringIndexIntoView(index);
}
