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
}
