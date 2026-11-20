using Microsoft.Win32;
using UnityPackageExplorer.App;

namespace UnityPackageExplorer.Tests;

public sealed class ArchiveBundleWindowTests
{
    [Fact]
    public async Task DefaultsToSourceNameAcceptsEnterAndPreservesCustomEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), "UPX-bundle-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sources = new[] { Path.Combine(root, "衣装.zip"), Path.Combine(root, "素材.zip") };
        foreach (var path in sources) File.WriteAllBytes(path, [0]);
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                ArchiveBundleWindow? window = null;
                try
                {
                    window = new ArchiveBundleWindow(sources);
                    Assert.Equal("衣装", window.FolderNameBox.Text);
                    Assert.True(window.CreateButton.IsDefault);
                    window.SourceBox.SelectedIndex = 1;
                    Assert.Equal("素材", window.FolderNameBox.Text);
                    window.FolderNameBox.Text = "好きな衣装名";
                    window.SourceBox.SelectedIndex = 0;
                    Assert.Equal("好きな衣装名", window.FolderNameBox.Text);
                    Assert.Contains("好きな衣装名_統合.zip", window.LayoutPreview.Text);
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
                finally { window?.Close(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RestoresMergeChoiceEvenWhenThePreviousWindowWasCanceled()
    {
        var path = @"Software\UnityPackageExplorer.Tests\BundlePreferences-" + Guid.NewGuid().ToString("N");
        var root = Directory.CreateTempSubdirectory("UPX-bundle-preference-").FullName;
        var sources = new[] { Path.Combine(root, "衣装.zip"), Path.Combine(root, "素材.rar") };
        foreach (var source in sources) File.WriteAllBytes(source, [0]);
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                ArchiveBundleWindow? window = null;
                try
                {
                    using var registry = Registry.CurrentUser.CreateSubKey(path);
                    window = new ArchiveBundleWindow(sources, new ArchiveBundlePreferences(registry));
                    Assert.False(window.MergePackagesCheckBox.IsChecked);
                    window.MergePackagesCheckBox.IsChecked = true;
                    Assert.Contains("衣装.unitypackage", window.LayoutPreview.Text);
                    window.Close();

                    window = new ArchiveBundleWindow(sources, new ArchiveBundlePreferences(registry));
                    Assert.True(window.MergePackagesCheckBox.IsChecked);
                    window.MergePackagesCheckBox.IsChecked = false;
                    Assert.Contains("別々に収録", window.LayoutPreview.Text);
                    Assert.False(new ArchiveBundlePreferences(registry).MergePackages);
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
                finally { window?.Close(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }
}
