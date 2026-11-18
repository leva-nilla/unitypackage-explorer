using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.App;

public partial class ArchiveBundleWindow : Window
{
    private sealed record SourceChoice(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    private readonly string[] _sources;
    private CancellationTokenSource? _operation;
    private string? _lastSuggestedName;
    private string? _resultDirectory;
    private bool _finished;

    public ArchiveBundleWindow(IEnumerable<string> paths)
    {
        _sources = paths.Select(Path.GetFullPath).ToArray();
        // Reject unsupported selections before presenting a usable operation.
        ArchiveBundleService.Plan(_sources, "UnityPackageExplorer-selection-" + Guid.NewGuid().ToString("N"));
        InitializeComponent();
        SelectionSummary.Text = $"{_sources.Length}個のZIP · 保存先: {Path.GetDirectoryName(_sources[0])}";
        SourceBox.ItemsSource = _sources.Select(path => new SourceChoice(path)).ToArray();
        SourceBox.SelectedIndex = 0;
        Loaded += (_, _) => { FolderNameBox.Focus(); FolderNameBox.SelectAll(); };
    }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is not SourceChoice source) return;
        var suggestion = Path.GetFileNameWithoutExtension(source.Path);
        if (FolderNameBox.Text.Length == 0 || FolderNameBox.Text == _lastSuggestedName)
            FolderNameBox.Text = suggestion;
        _lastSuggestedName = suggestion;
    }

    private void FolderNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LayoutPreview is null) return;
        var name = FolderNameBox.Text;
        LayoutPreview.Text = name + "/\n├─ *.unitypackage（選択した" + _sources.Length + "個のZIPから）\n└─ " + name + "_統合.zip";
        if (!_finished) Status.Text = "";
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_finished) { Close(); return; }
        if (_operation is not null) return;
        ArchiveBundlePlan plan;
        try { plan = ArchiveBundleService.Plan(_sources, FolderNameBox.Text); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status.Text = ex.Message;
            FolderNameBox.Focus();
            return;
        }
        _operation = new CancellationTokenSource();
        var operation = _operation;
        SourceBox.IsEnabled = FolderNameBox.IsEnabled = CreateButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        try
        {
            var result = await ArchiveBundleService.CreateAsync(plan,
                new Progress<ArchiveBundleProgress>(value => Status.Text = value.Stage + (value.FileName is null ? "…" : ": " + value.FileName)),
                operation.Token);
            _finished = true;
            _resultDirectory = result.DirectoryPath;
            Status.Text = result.Warnings.Count == 0 ? "作成しました。" : "作成しました。\n" + string.Join("\n", result.Warnings);
            OpenFolderButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            CreateButton.Content = "閉じる";
        }
        catch (ArchiveBundleRecoveryException ex)
        {
            _finished = true;
            _resultDirectory = ex.RecoveryDirectory;
            Status.Text = ex.Message;
            OpenFolderButton.Visibility = Visibility.Visible;
            OpenFolderButton.Content = "復旧先を開く";
            CancelButton.Visibility = Visibility.Collapsed;
            CreateButton.Content = "閉じる";
        }
        catch (OperationCanceledException) { Status.Text = "中止しました。元ZIPは元の場所にあります。"; }
        catch (Exception ex) { Status.Text = "作成できませんでした。元ZIPは元の場所にあります。\n" + ex.Message; }
        finally
        {
            _operation = null;
            operation.Dispose();
            Progress.Visibility = Visibility.Collapsed;
            SourceBox.IsEnabled = FolderNameBox.IsEnabled = !_finished;
            CreateButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            CancelButton.Content = "キャンセル";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is null) Close();
        else RequestCancellation();
    }

    private void RequestCancellation()
    {
        _operation?.Cancel();
        CancelButton.IsEnabled = false;
        Status.Text = "中止して元ZIPの位置を確認しています…";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        e.Cancel = true;
        RequestCancellation();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_resultDirectory is null) return;
        try { Process.Start(new ProcessStartInfo(_resultDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = "フォルダーを開けませんでした。\n" + _resultDirectory + "\n" + ex.Message; }
    }
}
