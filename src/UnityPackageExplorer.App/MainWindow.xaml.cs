using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Diagnostics;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.App;

public partial class MainWindow : Window
{
    private readonly string? _initialPath;
    private readonly PreviewRequestGate _previewRequests = new();
    private readonly DefaultEffectsManager _effectsManager = new();
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _treeRefreshCancellation;
    private long _treeRefreshVersion;
    private UnityAssetCatalog? _catalog;
    private IReadOnlyList<UnityPackageEntry> _entries = [];
    private string? _packagePath;
    private string? _directPrefabPath;
    private string? _sourcePath;
    private string _selectedKind = "すべて";
    private bool _updatingFilter;
    private bool _isBusy;
    private bool _isOpeningSource;
    private bool _filterChangedWhileOpening;
    private bool _updatingSelection;
    private UnityPackageEntry? _selectedPreviewEntry;
    private IReadOnlyList<AssetTreeItemView> _treeItems = [];
    private readonly HashSet<Key> _flightKeys = [];
    private readonly List<HelixToolkitScene> _currentScenes = [];
    private readonly Dictionary<long, PreviewTemporaryState> _requestTemporaryRoots = [];
    private long? _activeSceneRequestId;
    private Point _lastLookPoint;
    private bool _looking;
    private double _flightSpeed = 12;
    private long _lastMovementTimestamp;

    private sealed record TextureBinding(string MaterialGuid, string MaterialName, string TextureName, string Slot, string AssetPath);
    private sealed record ModelLoadResult(HelixToolkitScene Scene, IReadOnlyList<string> Warnings);
    private sealed record PrefabPreviewData(PrefabParseResult Parse, IReadOnlyList<PrefabNodeView> Tree, IReadOnlyList<PrefabModelPlacement> Models);
    private sealed class PreviewTemporaryState(string path)
    {
        public string Path { get; } = path;
        public bool InFlight { get; set; } = true;
    }
    private static readonly string[] Categories = ["アクセサリー", "アバター", "その他", "ツール", "テクスチャ", "ワールド", "衣装", "小物", "髪型"];

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        Viewport.EffectsManager = _effectsManager;
        _initialPath = initialPath;
        KindFilter.ItemsSource = new[] { "すべて" };
        KindFilter.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_initialPath) && File.Exists(_initialPath)) await OpenPathAsync(_initialPath);
        };
        Closed += (_, _) =>
        {
            CancelPendingOperations();
            CleanupTemporaryFiles();
            StopCameraInput();
            _effectsManager.Dispose();
        };
    }

    private (long RequestId, CancellationTokenSource Cancellation) BeginPreviewRequest()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _previewCancellation, cancellation);
        previous?.Cancel();
        return (_previewRequests.BeginRequest(), cancellation);
    }

    private void CancelActivePreview()
    {
        _previewRequests.BeginRequest();
        ResetPreviewSelection();
        var cancellation = Interlocked.Exchange(ref _previewCancellation, null);
        cancellation?.Cancel();
        ClearDisplay();
        SetBusy(false);
    }

    private void CancelPendingOperations()
    {
        var preview = Interlocked.Exchange(ref _previewCancellation, null);
        preview?.Cancel();

        var tree = Interlocked.Exchange(ref _treeRefreshCancellation, null);
        tree?.Cancel();
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e);
        if (paths.Length == 1 && IsSupportedDrop(paths[0]) && !_isOpeningSource)
        {
            e.Effects = DragDropEffects.Copy;
            SetStatus("対応ファイルをドロップするとプレビューします。元ファイルは移動・展開しません。");
        }
        else
        {
            e.Effects = DragDropEffects.None;
            SetStatus(paths.Length > 1 ? "一度に開けるファイルは1つだけです。" : paths.Length == 1 ? "対応形式は .zip、.unitypackage、.prefab です。" : "対応ファイルをドロップしてください。");
        }
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e);
        e.Effects = !_isOpeningSource && paths.Length == 1 && IsSupportedDrop(paths[0]) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        var paths = GetDroppedPaths(e);
        e.Handled = true;
        if (paths.Length != 1)
        {
            SetStatus(paths.Length > 1 ? "一度に開けるファイルは1つだけです。" : "ファイルを読み取れませんでした。");
            return;
        }
        if (!IsSupportedDrop(paths[0]))
        {
            SetStatus("対応形式は .zip、.unitypackage、.prefab です。元ファイルは変更していません。");
            return;
        }
        if (_isOpeningSource)
        {
            SetStatus("読み込みが終わってからファイルをドロップしてください。");
            return;
        }
        await OpenPathAsync(paths[0]);
    }

    private static string[] GetDroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];

    private static bool IsSupportedDrop(string path) =>
        new[] { ".zip", ".unitypackage", ".prefab" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private async Task OpenPathAsync(string path)
    {
        var (requestId, cancellation) = BeginPreviewRequest();
        var cancellationToken = cancellation.Token;
        try
        {
            _isOpeningSource = true;
            _filterChangedWhileOpening = false;
            ResetPreviewSelection();
            _treeItems = [];
            _updatingSelection = true;
            try
            {
                PrefabPicker.ItemsSource = null;
                PrefabPicker.SelectedItem = null;
                PrefabPicker.IsEnabled = false;
                PrefabCandidateCount.Text = "Prefab 0件";
            }
            finally { _updatingSelection = false; }
            SetBusy(true, "読み込み中…");
            ClearDisplay();
            _updatingFilter = true;
            try
            {
                AssetTree.ItemsSource = null;
                SearchBox.Clear();
                _selectedKind = "すべて";
                KindFilter.SelectedIndex = 0;
            }
            finally { _updatingFilter = false; }
            _packagePath = null;
            _directPrefabPath = null;
            _sourcePath = Path.GetFullPath(path);
            _catalog = null;
            _entries = [];
            CategoryPanel.Visibility = Visibility.Collapsed;
            CategoryPicker.ItemsSource = null;
            MoveToCategoryButton.IsEnabled = false;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".unitypackage")
            {
                var catalog = await Task.Run(() => UnityAssetCatalog.OpenPackage(path, cancellationToken), cancellationToken);
                if (!_previewRequests.IsCurrent(requestId)) return;
                _catalog = catalog;
                _packagePath = Path.GetFullPath(path);
                _entries = _catalog.Entries;
                var presentation = await Task.Run(() => (
                    Summary: BuildSummary(_entries, Path.GetFileName(path)),
                    Kinds: GetAvailableKinds(_entries)), cancellationToken);
                if (!_previewRequests.IsCurrent(requestId)) return;
                RefreshKindFilter(presentation.Kinds);
                SourceLabel.Text = Path.GetFileName(path);
                ExtractButton.IsEnabled = true;
                DetailsText.Text = presentation.Summary;
                SetStatus($"{_entries.Count:N0} 個のパスを読み込みました。Unity は起動していません。");
                RefreshPrefabCandidates();
                var suppressAutoPreview = _filterChangedWhileOpening;
                _filterChangedWhileOpening = false;
                _isOpeningSource = false;
                SetBusy(false);
                await RefreshTreeAsync(cancellationToken);
                if (!_previewRequests.IsCurrent(requestId)) return;
                if (AutoPreviewToggle.IsChecked == true && !suppressAutoPreview)
                    await PreviewAutomaticCandidateAsync(Path.GetFileName(path), requestId, cancellationToken);
            }
            else if (extension == ".zip")
            {
                var catalog = await Task.Run(() => UnityAssetCatalog.OpenZip(path, cancellationToken), cancellationToken);
                if (!_previewRequests.IsCurrent(requestId)) return;
                _catalog = catalog;
                _entries = _catalog.Entries;
                SourceLabel.Text = Path.GetFileName(path);
                ExtractButton.IsEnabled = false;
                CategoryPanel.Visibility = Visibility.Visible;
                var categoryRoot = GetCategoryRoot(path);
                var availableCategories = Categories.Where(name => Directory.Exists(Path.Combine(categoryRoot, name))).ToArray();
                CategoryPicker.ItemsSource = availableCategories;
                var warnings = _catalog.Warnings;
                var zipPresentation = await Task.Run(() => (
                    Summary: BuildZipSummary(_entries, Path.GetFileName(path), warnings, availableCategories.Length == 0),
                    Kinds: GetAvailableKinds(_entries)), cancellationToken);
                if (!_previewRequests.IsCurrent(requestId)) return;
                RefreshKindFilter(zipPresentation.Kinds);
                DetailsText.Text = zipPresentation.Summary;
                var warningsStatus = _catalog.Warnings.Count == 0 ? string.Empty : $" {_catalog.Warnings.Count:N0} 件の警告があります。";
                var emptyStatus = _entries.Count == 0 ? " 対応する素材が見つかりません。" : string.Empty;
                SetStatus($"ZIP内の素材を読み込みました。元ZIPは展開していません。{warningsStatus}{emptyStatus}");
                RefreshPrefabCandidates();
                var suppressAutoPreview = _filterChangedWhileOpening;
                _filterChangedWhileOpening = false;
                _isOpeningSource = false;
                SetBusy(false);
                await RefreshTreeAsync(cancellationToken);
                if (!_previewRequests.IsCurrent(requestId)) return;
                if (AutoPreviewToggle.IsChecked == true && !suppressAutoPreview)
                    await PreviewAutomaticCandidateAsync(Path.GetFileName(path), requestId, cancellationToken);
            }
            else if (extension == ".prefab")
            {
                var catalog = await Task.Run(() => UnityAssetCatalog.OpenProjectPrefab(path, cancellationToken), cancellationToken);
                if (!_previewRequests.IsCurrent(requestId)) return;
                _catalog = catalog;
                _entries = _catalog.Entries;
                var kinds = await Task.Run(() => GetAvailableKinds(_entries), cancellationToken);
                if (!_previewRequests.IsCurrent(requestId)) return;
                RefreshKindFilter(kinds);
                _directPrefabPath = Path.GetFullPath(path);
                SourceLabel.Text = Path.GetFileName(path);
                ExtractButton.IsEnabled = false;
                await RefreshTreeAsync(cancellationToken);
                var entry = _entries.FirstOrDefault(x => string.Equals(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_directPrefabPath)!, x.PathName)), _directPrefabPath, StringComparison.OrdinalIgnoreCase));
                await LoadPrefabAsync(entry, _directPrefabPath, requestId, cancellationToken);
            }
            else
            {
                throw new InvalidDataException("対応形式は .unitypackage、.zip、.prefab です。");
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) return;
            if (!_previewRequests.IsCurrent(requestId)) return;
            SetStatus($"読み込みに失敗しました: {ex.Message}");
            DetailsText.Text = ex.Message;
        }
        finally
        {
            Interlocked.CompareExchange(ref _previewCancellation, null, cancellation);
            cancellation.Dispose();
            if (_previewRequests.IsCurrent(requestId)) _isOpeningSource = false;
            CompletePreviewRequest(requestId);
            if (_previewRequests.IsCurrent(requestId)) SetBusy(false);
        }
    }

    private static string BuildSummary(IReadOnlyList<UnityPackageEntry> entries, string fileName)
    {
        var counts = entries.Where(x => x.HasAsset).GroupBy(x => x.Kind).OrderBy(x => x.Key)
            .Select(x => $"{x.Key}: {x.Count():N0}");
        var prefabNames = entries.Where(x => x.Extension == ".prefab").Select(x => Path.GetFileName(x.PathName)).Take(8).ToArray();
        var text = new StringBuilder()
            .AppendLine(fileName)
            .AppendLine($"Asset: {entries.Count(x => x.HasAsset):N0} 個")
            .AppendLine(string.Join("   ·   ", counts));
        if (prefabNames.Length > 0)
            text.AppendLine().AppendLine("Prefab").AppendLine(string.Join(Environment.NewLine, prefabNames));
        return text.ToString();
    }

    private static string BuildZipSummary(IReadOnlyList<UnityPackageEntry> entries, string fileName, IReadOnlyList<string> warnings, bool noCategoriesAvailable)
    {
        var packages = entries.Where(x => x.PackageMemberPath is not null).Select(x => x.PackageMemberPath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var looseAssets = entries.Where(x => x.PackageMemberPath is null).ToArray();
        var counts = entries.Where(x => x.HasAsset).GroupBy(x => x.Kind).OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Count():N0}");
        var lines = new StringBuilder().AppendLine(fileName).AppendLine("ZIPを展開せずに読み取り").AppendLine($"確認できるファイル: {entries.Count:N0} 個");
        if (packages.Length > 0) lines.AppendLine().AppendLine("同梱 UnityPackage").AppendLine(string.Join(Environment.NewLine, packages));
        if (looseAssets.Length > 0)
        {
            lines.AppendLine().AppendLine("ZIP直収録").AppendLine(string.Join(Environment.NewLine, looseAssets.Take(12).Select(x => x.PathName)));
            if (looseAssets.Length > 12) lines.AppendLine($"ほか {looseAssets.Length - 12:N0} 個");
        }
        lines.AppendLine().AppendLine(string.Join("   ·   ", counts));
        if (warnings.Count > 0)
            lines.AppendLine().AppendLine("読み込めなかった内容").AppendLine(string.Join(Environment.NewLine, warnings));
        if (noCategoriesAvailable)
            lines.AppendLine().AppendLine("同じ分類フォルダー群が見つからないため、画面からの分類移動は使えません。");
        return lines.ToString();
    }

    private static string[] GetAvailableKinds(IReadOnlyList<UnityPackageEntry> entries) => entries.Where(x => x.HasAsset)
        .Select(x => x.Kind)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void RefreshKindFilter(IEnumerable<string> kinds)
    {
        var availableKinds = kinds.ToArray();
        _updatingFilter = true;
        try
        {
            KindFilter.ItemsSource = new[] { "すべて" }.Concat(availableKinds).ToArray();
            if (!availableKinds.Contains(_selectedKind, StringComparer.OrdinalIgnoreCase))
                _selectedKind = "すべて";
            KindFilter.SelectedItem = _selectedKind;
        }
        finally { _updatingFilter = false; }
    }

    private async Task RefreshTreeAsync(CancellationToken ownerToken = default)
    {
        var cancellation = ownerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ownerToken)
            : new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _treeRefreshCancellation, cancellation);
        previous?.Cancel();
        var requestVersion = Interlocked.Increment(ref _treeRefreshVersion);
        var entries = _entries;
        var search = SearchBox.Text.Trim();
        var kind = _selectedKind;
        try
        {
            var items = await Task.Run(() =>
            {
                var tree = UnityAssetCatalog.BuildTree(entries, x => x.HasAsset || x.HasMeta, cancellation.Token);
                var filtered = UnityAssetCatalog.FilterTree(tree, search, kind, cancellation.Token).ToArray();
                return AssetTreeItemView.CreateTree(filtered, cancellationToken: cancellation.Token);
            }, cancellation.Token);
            if (cancellation.IsCancellationRequested || requestVersion != Volatile.Read(ref _treeRefreshVersion)) return;
            _updatingFilter = true;
            try { _treeItems = items; AssetTree.ItemsSource = items; }
            finally { _updatingFilter = false; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref _treeRefreshVersion))
                SetStatus($"ツリーを更新できませんでした: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _treeRefreshCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_updatingFilter || _updatingSelection) return;
        if (e.NewValue is AssetTreeItemView { Entry: { } selected } && Equals(selected, _selectedPreviewEntry)) return;
        var (requestId, cancellation) = BeginPreviewRequest();
        var cancellationToken = cancellation.Token;
        try
        {
            if (_catalog is null) return;
            if (e.NewValue is not AssetTreeItemView { Entry: { } entry } || !entry.HasAsset)
            {
                ResetPreviewSelection();
                ClearDisplay();
                DetailsText.Text = "ファイルを選択すると内容をプレビューできます。";
                SetBusy(false);
                return;
            }

            _updatingSelection = true;
            try { PrefabPicker.SelectedItem = (PrefabPicker.ItemsSource as IEnumerable<PrefabCandidate>)?.FirstOrDefault(x => ReferenceEquals(x.Entry, entry)); }
            finally { _updatingSelection = false; }

            await PreviewEntryAsync(entry, requestId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_previewRequests.IsCurrent(requestId)) return;
            ResetPreviewSelection();
            SetStatus($"表示に失敗しました: {ex.Message}");
            DetailsText.Text = ex.Message;
        }
        finally
        {
            Interlocked.CompareExchange(ref _previewCancellation, null, cancellation);
            cancellation.Dispose();
            CompletePreviewRequest(requestId);
            if (_previewRequests.IsCurrent(requestId)) SetBusy(false);
        }
    }

    private async Task PreviewEntryAsync(UnityPackageEntry entry, long requestId, CancellationToken cancellationToken)
    {
        if (!_previewRequests.IsCurrent(requestId)) return;
        _selectedPreviewEntry = entry;
        SetBusy(true, $"{entry.PathName} を読み込み中…");
        switch (entry.Extension)
        {
            case ".prefab": await LoadPrefabAsync(entry, null, requestId, cancellationToken); break;
            case ".fbx": await LoadPackageModelAsync(entry, Matrix4x4.Identity, [], requestId, cancellationToken); break;
            case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".gif": case ".tga": case ".psd": case ".tif": case ".tiff":
                await ShowImageAsync(entry, requestId, cancellationToken); break;
            default:
                ClearDisplay();
                DetailsText.Text = $"{entry.PathName}{Environment.NewLine}種類: {entry.Kind}{Environment.NewLine}GUID: {entry.Guid}";
                SetStatus("このファイル形式は一覧で確認できます。画像・Prefab・FBX はプレビューできます。"); break;
        }
    }

    private async Task PreviewAutomaticCandidateAsync(string sourceName, long requestId, CancellationToken cancellationToken)
    {
        if (!_previewRequests.IsCurrent(requestId) || _catalog is null) return;
        var selected = AutomaticPreviewSelector.Select(_entries, sourceName);
        _updatingSelection = true;
        try { PrefabPicker.SelectedItem = (PrefabPicker.ItemsSource as IEnumerable<PrefabCandidate>)?.FirstOrDefault(x => Equals(x.Entry, selected)); }
        finally { _updatingSelection = false; }
        if (selected is null)
        {
            ClearDisplay();
            DetailsText.Text = $"{Path.GetFileName(sourceName)}{Environment.NewLine}プレビューできるPrefab、FBX、画像がありません。";
            SetStatus("自動表示できる素材はありません。");
            return;
        }
        SelectEntryInTree(selected, requestId);
        DetailsText.Text = $"自動選択: {selected.PathName}{Environment.NewLine}Prefab の階層と参照モデルを読み込んでいます…";
        await PreviewEntryAsync(selected, requestId, cancellationToken);
    }

    private void RefreshPrefabCandidates()
    {
        var candidates = _entries.Where(x => x.HasAsset && x.Extension == ".prefab").OrderBy(x => x.PathName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new PrefabCandidate(x, GetCandidateDisplay(x))).ToArray();
        PrefabPicker.ItemsSource = candidates;
        PrefabCandidateCount.Text = candidates.Length == 0 ? "Prefab 0件" : $"Prefab {candidates.Length:N0}件";
        PrefabPicker.IsEnabled = candidates.Length > 0;
    }

    private sealed record PrefabCandidate(UnityPackageEntry Entry, string Display);

    private static string GetCandidateDisplay(UnityPackageEntry entry)
    {
        var relativePath = (entry.AssetPathName ?? entry.PathName).Replace('\\', '/');
        var folders = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).TakeLast(1);
        var location = string.Join('/', folders);
        return $"{Path.GetFileName(entry.PathName)}  ·  {location}  ·  {Path.GetFileName(entry.PackageMemberPath ?? "ZIP")}";
    }

    private async void PrefabPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingSelection || _isOpeningSource || PrefabPicker.SelectedItem is not PrefabCandidate candidate || _catalog is null) return;
        var (requestId, cancellation) = BeginPreviewRequest();
        var token = cancellation.Token;
        SelectEntryInTree(candidate.Entry, requestId);
        try { await PreviewEntryAsync(candidate.Entry, requestId, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (_previewRequests.IsCurrent(requestId)) { ResetPreviewSelection(); SetStatus($"表示に失敗しました: {ex.Message}"); DetailsText.Text = ex.Message; } }
        finally { Interlocked.CompareExchange(ref _previewCancellation, null, cancellation); cancellation.Dispose(); CompletePreviewRequest(requestId); if (_previewRequests.IsCurrent(requestId)) SetBusy(false); }
    }

    private void SelectEntryInTree(UnityPackageEntry entry, long requestId)
    {
        var treeItems = _treeItems;
        _updatingSelection = true;
        bool selected;
        try { selected = AssetTreeItemView.SelectPath(treeItems, entry.PathName); }
        finally { _updatingSelection = false; }
        if (!selected) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_previewRequests.IsCurrent(requestId) || !ReferenceEquals(_treeItems, treeItems)) return;
            var view = AssetTreeItemView.FindPath(treeItems, entry.PathName);
            if (view is null) return;
            AssetTreeSelection.BringPathIntoView(AssetTree, view);
        }));
    }

    private async Task LoadPrefabAsync(UnityPackageEntry? entry, string? externalPath, long requestId, CancellationToken cancellationToken)
    {
        if (!_previewRequests.IsCurrent(requestId)) return;
        var catalog = _catalog;
        if (catalog is null) return;
        ClearDisplay();
        var sourceName = externalPath ?? entry?.PathName ?? "Prefab";
        ViewerEmpty.Text = "Prefab を解析して FBX モデルを準備しています…";
        DetailsText.Text = $"{sourceName}{Environment.NewLine}Prefab と参照アセットを読み込んでいます…";
        SetStatus($"{Path.GetFileName(sourceName)} の Prefab と FBX を読み込み中…");
        var preview = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = externalPath is null
                ? entry is null ? null : catalog.ReadAsset(entry, cancellationToken: cancellationToken)
                : File.ReadAllBytes(externalPath);
            if (bytes is null) throw new InvalidDataException("Prefab の内容を読み取れません。");
            var rootTransformIds = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            var result = UnityPrefabParser.Parse(Encoding.UTF8.GetString(bytes), sourceGuid =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rootTransformIds.TryGetValue(sourceGuid, out var cached)) return cached;
                long? rootTransformId = null;
                try
                {
                    var sourceBytes = catalog.ReadAssetByGuid(sourceGuid, cancellationToken: cancellationToken, packageMemberPath: entry?.PackageMemberPath, referenceEntry: entry);
                    if (sourceBytes is not null)
                        rootTransformId = UnityPrefabParser.FindRootTransformFileId(Encoding.UTF8.GetString(sourceBytes));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
                rootTransformIds[sourceGuid] = rootTransformId;
                return rootTransformId;
            });
            cancellationToken.ThrowIfCancellationRequested();
            var tree = result.Roots.Select(x => new PrefabNodeView(x, cancellationToken: cancellationToken)).ToArray();
            // Importer loads the entire FBX scene. A skinned renderer's leaf transform often
            // contains the FBX rest-pose axis conversion; applying it to the whole scene tilts it.
            // Keep explicit transforms on static MeshFilter references and nested prefab instances.
            var models = UnityPrefabParser.GetModelPlacements(result.Roots, useRootTransformForSkinnedMeshRenderers: true);
            return new PrefabPreviewData(result, tree, models);
        }, cancellationToken);
        if (!_previewRequests.IsCurrent(requestId)) return;
        PrefabTree.ItemsSource = preview.Tree;
        var warnings = new List<string>(preview.Parse.Warnings);
        var modelsToLoad = preview.Models;
        ViewerEmpty.Text = modelsToLoad.Count == 0 ? "FBX 参照がありません。Prefab の階層を表示しています" : "FBX モデルを読み込み中…";

        var models = new List<HelixToolkitScene>();
        var materialInfoCache = new Dictionary<string, UnityMaterialInfo?>(StringComparer.OrdinalIgnoreCase);
        var displayedModelGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < modelsToLoad.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_previewRequests.IsCurrent(requestId)) return;
            var placement = modelsToLoad[index];
            var node = placement.Node;
            var guid = placement.Guid;
            if (placement.UsesRootTransformApproximation)
                warnings.Add($"{node.Name}: Skinned FBX is shown as a whole-scene approximation using the Prefab root transform; per-renderer and bone-local placement is not reproduced.");
            if (displayedModelGuids.Contains(guid))
            {
                var fileIds = placement.MeshFileIds.Count == 0 ? "unknown" : string.Join(", ", placement.MeshFileIds);
                warnings.Add($"{node.Name}: FBX {guid} is already shown once; Mesh fileID(s) {fileIds} could not be isolated, so this repeated whole-scene instance was skipped.");
                continue;
            }
            SetStatus($"{entry?.PathName ?? Path.GetFileName(externalPath)} の FBX を読み込み中… ({index + 1}/{modelsToLoad.Count})");
            var packageMemberPath = entry?.PackageMemberPath;
            var modelPath = catalog.ResolvePath(guid, packageMemberPath, entry);
            if (modelPath is null)
            {
                warnings.Add($"{node.Name}: 参照 GUID {guid} の素材が見つかりません。");
                continue;
            }
            if (!modelPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                if (node.Mesh is null && node.SourcePrefab is not null)
                    warnings.Add($"{node.Name}: nested Prefab reference {guid} resolves to a non-FBX asset and is not expanded by this viewer ({modelPath}).");
                else
                    warnings.Add($"{node.Name}: FBX 以外の参照は 3D 表示できません ({modelPath})。");
                continue;
            }
            try
            {
                var modelEntry = catalog.FindByGuid(guid, packageMemberPath, entry);
                var loaded = await LoadPackageModelAsyncCore(guid, placement.Transform, placement.Materials, packageMemberPath, entry, modelEntry, cancellationToken, materialInfoCache, requestId, placement.RendererAssignments, isPrefabPreview: true, materialOverrides: placement.MaterialOverrides);
                if (!_previewRequests.IsCurrent(requestId)) return;
                displayedModelGuids.Add(guid);
                models.Add(loaded.Scene);
                HideSkeletonHelpers(loaded.Scene.Root);
                _currentScenes.Add(loaded.Scene);
                SceneModels.AddNode(loaded.Scene.Root);
                _activeSceneRequestId = requestId;
                warnings.AddRange(loaded.Warnings.Select(x => $"{node.Name}: {x}"));
                if (placement.MeshFileIds.Count > 0)
                    warnings.Add($"{node.Name}: Unity Mesh fileID(s) {string.Join(", ", placement.MeshFileIds)} could not be mapped to individual FBX meshes; the complete FBX scene is shown as an approximation.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add($"{node.Name}: {ex.Message}"); }
        }

        if (!_previewRequests.IsCurrent(requestId)) return;
        var details = new StringBuilder()
            .AppendLine(externalPath ?? entry?.PathName ?? "Prefab")
            .AppendLine($"Prefab 階層: {CountNodes(preview.Parse.Roots):N0} 個")
            .AppendLine($"FBX 参照: {CountModelReferences(preview.Parse.Roots):N0} 個")
            .AppendLine($"FBX モデル表示: {models.Count:N0} 個");
        var distinctWarnings = warnings.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctWarnings.Length > 0)
            details.AppendLine($"確認できない内容: {distinctWarnings.Length:N0} 件").AppendLine().AppendLine("詳細").AppendLine(string.Join(Environment.NewLine, distinctWarnings));
        if (models.Count > 0)
        {
            Viewport.Visibility = Visibility.Visible;
            CameraHelp.Visibility = Visibility.Visible;
            ViewerEmpty.Visibility = Visibility.Collapsed;
            FrameModels(models);
        }
        else ViewerEmpty.Text = "表示できる FBX がありません。Prefab の階層と確認できない内容を表示しています";
        DetailsText.Text = details.ToString();
        SetStatus(models.Count > 0 ? "参照 FBX を表示しました。テクスチャとシェーダーは簡易表示です。" : "Prefab の構造を読み込みました。");
    }

    private async Task LoadPackageModelAsync(UnityPackageEntry entry, Matrix4x4 transform, IReadOnlyList<UnityAssetReference> materials, long requestId, CancellationToken cancellationToken)
    {
        if (!_previewRequests.IsCurrent(requestId)) return;
        var loaded = await LoadPackageModelAsyncCore(entry.Guid, transform, materials, entry.PackageMemberPath, entry, entry, cancellationToken, requestId: requestId);
        if (!_previewRequests.IsCurrent(requestId)) return;
        ClearSceneModels();
        HideSkeletonHelpers(loaded.Scene.Root);
        _currentScenes.Add(loaded.Scene);
        SceneModels.AddNode(loaded.Scene.Root);
        _activeSceneRequestId = requestId;
        Viewport.Visibility = Visibility.Visible;
        CameraHelp.Visibility = Visibility.Visible;
        ViewerEmpty.Visibility = Visibility.Collapsed;
        FrameModels([loaded.Scene]);
        PrefabTree.ItemsSource = null;
        DetailsText.Text = $"{entry.PathName}{Environment.NewLine}種類: Model{Environment.NewLine}GUID: {entry.Guid}";
        if (loaded.Warnings.Count > 0) DetailsText.Text += Environment.NewLine + Environment.NewLine + "確認できない内容" + Environment.NewLine + string.Join(Environment.NewLine, loaded.Warnings);
        SetStatus(loaded.Warnings.Count == 0 ? "FBX を表示しています。" : "FBX を表示しました。一部の参照は確認できませんでした。");
    }

    private async Task<ModelLoadResult> LoadPackageModelAsyncCore(
        string guid,
        Matrix4x4 transform,
        IReadOnlyList<UnityAssetReference> materials,
        string? packageMemberPath = null,
        UnityPackageEntry? referenceEntry = null,
        UnityPackageEntry? modelEntry = null,
        CancellationToken cancellationToken = default,
        IDictionary<string, UnityMaterialInfo?>? materialInfoCache = null,
        long requestId = 0,
        IReadOnlyList<PrefabRendererMaterialAssignment>? rendererAssignments = null,
        bool isPrefabPreview = false,
        IReadOnlyList<PrefabMaterialOverride>? materialOverrides = null)
    {
        var catalog = _catalog ?? throw new InvalidOperationException("Asset catalog is not open.");
        var temp = EnsureTemporaryRoot(requestId);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelPath = modelEntry is null
                ? catalog.StageAsset(guid, temp, cancellationToken: cancellationToken, packageMemberPath: packageMemberPath, referenceEntry: referenceEntry)
                : catalog.StageAsset(modelEntry, temp, cancellationToken);
            StageSiblingImages(catalog, temp, guid, packageMemberPath, referenceEntry, modelEntry, cancellationToken);
            var textureBindings = new List<TextureBinding>();
            var warnings = new List<string>();
            foreach (var material in materials)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (material.Guid is null)
                {
                    if (material.FileId != 0) warnings.Add($"Material fileID {material.FileId} has no GUID and cannot be resolved.");
                    continue;
                }

                var materialPath = catalog.ResolvePath(material.Guid, packageMemberPath, referenceEntry);
                if (materialPath is null)
                {
                    warnings.Add($"Material GUID {material.Guid} was not found.");
                    continue;
                }
                UnityMaterialInfo? materialInfo;
                if (materialInfoCache is not null && materialInfoCache.TryGetValue(material.Guid, out var cachedMaterialInfo))
                {
                    materialInfo = cachedMaterialInfo;
                }
                else
                {
                    materialInfo = catalog.ReadMaterialInfo(material.Guid, cancellationToken, packageMemberPath, referenceEntry);
                    materialInfoCache?.Add(material.Guid, materialInfo);
                }
                if (materialInfo is null)
                {
                    warnings.Add($"Material data is missing: {materialPath}");
                    continue;
                }

                var materialName = materialInfo.Name ?? Path.GetFileNameWithoutExtension(materialPath);
                foreach (var textureGuid in materialInfo.UnsupportedTextureGuids)
                {
                    var unsupportedPath = catalog.ResolvePath(textureGuid, packageMemberPath, referenceEntry);
                    warnings.Add(unsupportedPath is null
                        ? $"Texture GUID {textureGuid} referenced by material '{materialName}' was not found."
                        : $"Texture {unsupportedPath} is referenced by a material slot that this simplified viewer does not render.");
                }
                foreach (var textureReference in materialInfo.SupportedTextures)
                {
                    var textureGuid = textureReference.Guid;
                    var textureSourcePath = catalog.ResolvePath(textureGuid, packageMemberPath, referenceEntry);
                    if (textureSourcePath is null)
                    {
                        warnings.Add($"Texture GUID {textureGuid} referenced by material '{materialName}' was not found.");
                        continue;
                    }

                    var textureExtension = Path.GetExtension(textureSourcePath);
                    if (textureExtension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
                        || !new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".tga" }.Contains(textureExtension, StringComparer.OrdinalIgnoreCase))
                    {
                        warnings.Add($"Texture format is not supported for 3D display: {textureSourcePath}");
                        continue;
                    }

                    var texturePath = catalog.StageImage(textureGuid, temp, cancellationToken, packageMemberPath, referenceEntry);
                    if (texturePath is null)
                    {
                        warnings.Add($"Texture data is missing: {textureSourcePath}");
                        continue;
                    }
                    if (textureExtension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                        texturePath = ConvertTgaToPng(texturePath);
                    textureBindings.Add(new TextureBinding(
                        material.Guid,
                        materialName,
                        Path.GetFileNameWithoutExtension(textureSourcePath),
                        textureReference.Slot,
                        texturePath));
                }
            }

            foreach (var materialOverride in materialOverrides ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = $"target renderer fileID {materialOverride.TargetFileId}, material slot {materialOverride.MaterialSlot}";
                if (materialOverride.Material.Guid is not { } overrideGuid)
                {
                    warnings.Add($"Nested Prefab override for {target} has no material GUID; it was not applied to the FBX.");
                    continue;
                }

                var overridePath = catalog.ResolvePath(overrideGuid, packageMemberPath, referenceEntry);
                if (overridePath is null)
                {
                    warnings.Add($"Nested Prefab override for {target} references missing material GUID {overrideGuid}; it was not applied to the FBX.");
                    continue;
                }

                UnityMaterialInfo? overrideInfo;
                if (materialInfoCache is not null && materialInfoCache.TryGetValue(overrideGuid, out var cachedOverrideInfo))
                {
                    overrideInfo = cachedOverrideInfo;
                }
                else
                {
                    overrideInfo = catalog.ReadMaterialInfo(overrideGuid, cancellationToken, packageMemberPath, referenceEntry);
                    materialInfoCache?.Add(overrideGuid, overrideInfo);
                }

                if (overrideInfo is null)
                {
                    warnings.Add($"Nested Prefab override for {target} could not read material {overridePath}; it was not applied to the FBX.");
                    continue;
                }

                var overrideName = overrideInfo.Name ?? Path.GetFileNameWithoutExtension(overridePath);
                var resolvedOverrideTextures = overrideInfo.SupportedTextures
                    .Select(texture => (texture.Guid, Path: catalog.ResolvePath(texture.Guid, packageMemberPath, referenceEntry)))
                    .ToArray();
                var texturePaths = resolvedOverrideTextures
                    .Where(texture => texture.Path is not null)
                    .Select(texture => texture.Path!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var textureDescription = texturePaths.Length > 0
                    ? $"resolved texture(s) {string.Join(", ", texturePaths)}"
                    : overrideInfo.SupportedTextures.Count > 0
                        ? $"supported texture reference(s) could not be resolved: {string.Join(", ", resolvedOverrideTextures.Select(x => x.Guid))}"
                        : overrideInfo.UnsupportedTextureGuids.Count > 0
                            ? $"unsupported texture slot reference(s): {string.Join(", ", overrideInfo.UnsupportedTextureGuids)}"
                            : "no supported texture reference";
                warnings.Add($"Nested Prefab override for {target} references material '{overrideName}' ({textureDescription}); it was not applied because the FBX submesh could not be identified safely.");
            }

            using var importer = new Importer();
            var configuration = PreviewImporterConfiguration.Create();
            var scene = importer.Load(modelPath, configuration);
            if (scene is null) throw new InvalidDataException("FBX を読み込めませんでした。");
            cancellationToken.ThrowIfCancellationRequested();
            scene.Root.ModelMatrix *= transform;
            scene.Root.UpdateAllTransformMatrix();
            warnings.AddRange(ApplyUnityTextures(scene.Root, textureBindings, materials, rendererAssignments ?? [], isPrefabPreview, materialOverrides?.Count > 0));
            return new ModelLoadResult(scene, warnings);
        }, cancellationToken);
    }

    private static void StageSiblingImages(UnityAssetCatalog catalog, string temporaryRoot, string modelGuid, string? packageMemberPath, UnityPackageEntry? referenceEntry, UnityPackageEntry? modelEntry, CancellationToken cancellationToken)
    {
        modelEntry ??= catalog.FindByGuid(modelGuid, packageMemberPath, referenceEntry);
        if (modelEntry is null) return;
        var sourceDirectory = Path.GetDirectoryName(modelEntry.PathName.Replace('/', Path.DirectorySeparatorChar));
        foreach (var image in catalog.Entries.Where(x => x.Kind == "Image" && string.Equals(Path.GetDirectoryName(x.PathName.Replace('/', Path.DirectorySeparatorChar)), sourceDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { catalog.StageAsset(image, temporaryRoot, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
        }
    }

    private static IReadOnlyList<string> ApplyUnityTextures(
        HelixToolkit.SharpDX.Model.Scene.SceneNode root,
        IReadOnlyList<TextureBinding> textureBindings,
        IReadOnlyList<UnityAssetReference> materialReferences,
        IReadOnlyList<PrefabRendererMaterialAssignment> rendererAssignments,
        bool isPrefabPreview,
        bool hasUnappliedMaterialOverrides = false)
    {
        if (textureBindings.Count == 0)
            return isPrefabPreview
                ? [hasUnappliedMaterialOverrides
                    ? "Nested Prefab material override textures were inspected for diagnostics but not applied because their FBX submesh targets could not be identified safely."
                    : UnityMaterialPreviewDiagnostics.GetMissingSupportedTextureWarning(materialReferences.Count)]
                : [];
        var meshNodes = EnumerateSceneNodes(root).OfType<HelixToolkit.SharpDX.Model.Scene.MeshNode>().ToArray();
        var warnings = new List<string>();
        var applied = new HashSet<TextureBinding>();
        foreach (var mesh in meshNodes)
        {
            var materialName = mesh.Material?.Name;
            var matchingRenderers = rendererAssignments
                .Where(x => UnityMaterialNameMatcher.HaveSameNormalizedName(x.RendererName, mesh.Name))
                .ToArray();
            var scopedBindings = matchingRenderers.Length == 1
                ? textureBindings.Where(binding => matchingRenderers[0].Materials.Any(reference => string.Equals(reference.Guid, binding.MaterialGuid, StringComparison.OrdinalIgnoreCase)))
                : textureBindings;
            var matching = scopedBindings.Where(x =>
                UnityMaterialNameMatcher.AreEquivalent(x.MaterialName, materialName)
                || UnityMaterialNameMatcher.AreEquivalent(x.MaterialName, mesh.Name)
                || UnityMaterialNameMatcher.AreEquivalent(x.TextureName, materialName)
                || UnityMaterialNameMatcher.AreEquivalent(x.TextureName, mesh.Name)).ToArray();
            var paths = matching
                .OrderBy(x => TextureSlotPriority(x.Slot))
                .Select(x => x.AssetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 1)
            {
                SetDiffuseTexture(mesh, paths[0]);
                foreach (var item in matching) applied.Add(item);
            }
            else if (paths.Length > 1)
                warnings.Add($"Several textures match FBX material '{mesh.Name}'; no texture was selected automatically.");
        }

        var allReferencedMaterialsHaveOneTexture = materialReferences.Count == 1
            && textureBindings.Select(x => x.AssetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        if (applied.Count == 0 && allReferencedMaterialsHaveOneTexture)
        {
            var path = textureBindings[0].AssetPath;
            foreach (var mesh in meshNodes) SetDiffuseTexture(mesh, path);
            foreach (var item in textureBindings) applied.Add(item);
        }

        foreach (var binding in textureBindings.Except(applied))
            warnings.Add($"Texture '{binding.TextureName}' for material '{binding.MaterialName}' could not be matched to an FBX material and was left unused.");
        return warnings;
    }

    private static int TextureSlotPriority(string slot) => slot switch
    {
        "_BaseMap" or "_BaseColorMap" or "_BaseColorTexture" => 0,
        "_MainTex" => 1,
        _ => 2
    };

    private static void SetDiffuseTexture(HelixToolkit.SharpDX.Model.Scene.MeshNode mesh, string path) =>
        mesh.Material = CreateTexturePreviewMaterial(path);

    private static HelixToolkit.SharpDX.Model.PhongMaterialCore CreateTexturePreviewMaterial(string path) =>
        new()
        {
            // PhongMaterialCore defaults AmbientColor to 0.66 and SpecularColor to 0.5.
            // With the white viewport lights those defaults wash out the sampled diffuse map.
            AmbientColor = new HelixToolkit.Maths.Color4(0.08f, 0.08f, 0.08f, 1f),
            DiffuseColor = new HelixToolkit.Maths.Color4(1, 1, 1, 1),
            EmissiveColor = new HelixToolkit.Maths.Color4(0, 0, 0, 1),
            SpecularColor = new HelixToolkit.Maths.Color4(0.06f, 0.06f, 0.06f, 1f),
            SpecularShininess = 24f,
            DiffuseMap = HelixToolkit.SharpDX.TextureModel.Create(path)
        };

    private static IEnumerable<HelixToolkit.SharpDX.Model.Scene.SceneNode> EnumerateSceneNodes(HelixToolkit.SharpDX.Model.Scene.SceneNode root)
    {
        yield return root;
        foreach (var child in root.Items)
            foreach (var nested in EnumerateSceneNodes(child)) yield return nested;
    }

    private static int CountNodes(IEnumerable<PrefabNode> nodes) => nodes.Sum(x => 1 + CountNodes(x.Children));

    private static int CountModelReferences(IEnumerable<PrefabNode> nodes) => nodes.Sum(x =>
        (string.IsNullOrWhiteSpace(x.Mesh?.Guid ?? x.SourcePrefab?.Guid) ? 0 : 1) + CountModelReferences(x.Children));

    private async Task ShowImageAsync(UnityPackageEntry entry, long requestId, CancellationToken cancellationToken)
    {
        if (entry.Extension.Equals(".psd", StringComparison.OrdinalIgnoreCase))
        {
            ClearDisplay();
            DetailsText.Text = $"{entry.PathName}{Environment.NewLine}PSD 画像のプレビューには対応していません。PNG、JPEG、BMP、GIF、TGA、TIFF に変換して確認してください。";
            SetStatus("PSD は一覧に表示できますが、画像プレビューには対応していません。");
            return;
        }

        var catalog = _catalog ?? throw new InvalidOperationException("Asset catalog is not open.");
        var bitmap = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = catalog.ReadAsset(entry, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("画像データが見つかりません。");
            return DecodeImage(bytes, entry.PathName);
        }, cancellationToken);
        if (!_previewRequests.IsCurrent(requestId)) return;

        ClearSceneModels();
        AssetImage.Source = bitmap;
        ImagePanel.Visibility = Visibility.Visible;
        ViewerEmpty.Visibility = Visibility.Collapsed;
        PrefabTree.ItemsSource = null;
        DetailsText.Text = $"{entry.PathName}{Environment.NewLine}{bitmap.PixelWidth} × {bitmap.PixelHeight}";
        SetStatus("画像を表示しています。");
    }

    private static BitmapSource DecodeImage(byte[] bytes, string pathName)
    {
        BitmapSource bitmap;
        if (Path.GetExtension(pathName).Equals(".tga", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = TgaDecoder.Decode(bytes);
            bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra32Pixels, decoded.Width * 4);
        }
        else
        {
            var decoded = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.StreamSource = stream;
            decoded.EndInit();
            bitmap = decoded;
        }
        bitmap.Freeze();
        return bitmap;
    }

    private void ClearDisplay()
    {
        ClearSceneModels();
        AssetImage.Source = null;
        ImagePanel.Visibility = Visibility.Collapsed;
        Viewport.Visibility = Visibility.Collapsed;
        CameraHelp.Visibility = Visibility.Collapsed;
        ViewerEmpty.Visibility = Visibility.Visible;
        ViewerEmpty.Text = "FBX・Prefab・画像を選択すると、ここに表示される";
        PrefabTree.ItemsSource = null;
    }

    private void FrameModels(IEnumerable<HelixToolkitScene> scenes)
    {
        var found = false;
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var scene in scenes)
        {
            scene.Root.UpdateAllTransformMatrix();
            if (!scene.Root.TryGetBound(out var bounds)) continue;
            minimum = Vector3.Min(minimum, bounds.Minimum);
            maximum = Vector3.Max(maximum, bounds.Maximum);
            found = true;
        }

        if (!found || Viewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
        {
            Viewport.ZoomExtents();
            return;
        }

        var center = (minimum + maximum) * 0.5f;
        var extent = MathF.Max((maximum - minimum).Length(), 1f);
        var direction = Vector3.Normalize(new Vector3(0.28f, 0.12f, 1f));
        var distance = extent * 1.45f;
        camera.Position = new Point3D(center.X + direction.X * distance, center.Y + direction.Y * distance, center.Z + direction.Z * distance);
        camera.LookDirection = new Vector3D(-direction.X * distance, -direction.Y * distance, -direction.Z * distance);
        camera.UpDirection = new Vector3D(0, 1, 0);
        _flightSpeed = Math.Max(extent * 0.14, 1);
    }

    private void Viewport_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F)
        {
            if (_currentScenes.Count > 0) FrameModels(_currentScenes);
            e.Handled = true;
            return;
        }
        if (!_looking || e.Key is not (Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.LeftShift or Key.RightShift)) return;
        _flightKeys.Add(e.Key);
        if (_flightKeys.Count == 1)
        {
            _lastMovementTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
        }
        e.Handled = true;
    }

    private void Viewport_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        _flightKeys.Remove(e.Key);
        if (_flightKeys.Count == 0) CompositionTarget.Rendering -= OnRendering;
        e.Handled = true;
    }

    private void Viewport_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _looking = true;
        _lastLookPoint = e.GetPosition(Viewport);
        Viewport.Focus();
        Mouse.Capture(Viewport, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void Viewport_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _looking = false;
        _flightKeys.Clear();
        CompositionTarget.Rendering -= OnRendering;
        if (Mouse.Captured == Viewport) Mouse.Capture(null);
        e.Handled = true;
    }

    private void Viewport_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_looking || Viewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera) return;
        var point = e.GetPosition(Viewport);
        var delta = point - _lastLookPoint;
        _lastLookPoint = point;
        var look = camera.LookDirection;
        var length = look.Length;
        if (length < 0.001) return;
        var direction = Vector3.Normalize(new Vector3((float)look.X, (float)look.Y, (float)look.Z));
        var yaw = Math.Atan2(direction.X, direction.Z) - delta.X * 0.004;
        var pitch = Math.Clamp(Math.Asin(Math.Clamp(direction.Y, -0.999, 0.999)) - delta.Y * 0.004, -1.52, 1.52);
        var cosPitch = Math.Cos(pitch);
        camera.LookDirection = new Vector3D(Math.Sin(yaw) * cosPitch * length, Math.Sin(pitch) * length, Math.Cos(yaw) * cosPitch * length);
        camera.UpDirection = new Vector3D(0, 1, 0);
        e.Handled = true;
    }

    private void Viewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_looking) return;
        _flightSpeed = Math.Clamp(_flightSpeed * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.1, 10000);
        SetStatus($"移動速度: {_flightSpeed:0.##}　｜　右ドラッグ中のホイールで速度変更");
        e.Handled = true;
    }

    private void Viewport_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => StopCameraInput();

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e) => StopCameraInput();

    private void Window_Deactivated(object sender, EventArgs e) => StopCameraInput();

    private void StopCameraInput()
    {
        _flightKeys.Clear();
        CompositionTarget.Rendering -= OnRendering;
        _looking = false;
        if (Mouse.Captured == Viewport) Mouse.Capture(null);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_looking || _flightKeys.Count == 0 || Viewport.Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera) return;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp((now - _lastMovementTimestamp) / (double)Stopwatch.Frequency, 0, 0.05);
        _lastMovementTimestamp = now;
        var look = camera.LookDirection;
        var forward = new Vector3D(look.X, look.Y, look.Z);
        if (forward.LengthSquared < 0.0001) return;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0));
        if (right.LengthSquared < 0.0001) right = new Vector3D(1, 0, 0);
        right.Normalize();
        var up = new Vector3D(0, 1, 0);
        var movement = new Vector3D();
        if (_flightKeys.Contains(Key.W)) movement += forward;
        if (_flightKeys.Contains(Key.S)) movement -= forward;
        if (_flightKeys.Contains(Key.D)) movement += right;
        if (_flightKeys.Contains(Key.A)) movement -= right;
        if (_flightKeys.Contains(Key.E)) movement += up;
        if (_flightKeys.Contains(Key.Q)) movement -= up;
        if (movement.LengthSquared < 0.0001) return;
        movement.Normalize();
        var shift = _flightKeys.Contains(Key.LeftShift) || _flightKeys.Contains(Key.RightShift);
        var displacement = movement * _flightSpeed * (shift ? 4 : 1) * elapsed;
        camera.Position += displacement;
    }

    private static void HideSkeletonHelpers(HelixToolkit.SharpDX.Model.Scene.SceneNode node)
    {
        if (node.Name?.StartsWith("HxSK_", StringComparison.Ordinal) == true)
        {
            node.Visible = false;
            return;
        }
        foreach (var child in node.Items) HideSkeletonHelpers(child);
    }

    private void ClearSceneModels()
    {
        var previousRequestId = _activeSceneRequestId;
        _activeSceneRequestId = null;
        SceneModels.Clear();
        _currentScenes.Clear();
        StopCameraInput();
        Viewport.Visibility = Visibility.Collapsed;
        CameraHelp.Visibility = Visibility.Collapsed;
        ImagePanel.Visibility = Visibility.Collapsed;
        ViewerEmpty.Visibility = Visibility.Visible;
        if (previousRequestId is long requestId
            && _requestTemporaryRoots.TryGetValue(requestId, out var state)
            && !state.InFlight)
            CleanupRequestTemporaryFiles(requestId, state);
    }

    private string EnsureTemporaryRoot(long requestId)
    {
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        if (!_requestTemporaryRoots.TryGetValue(requestId, out var state))
        {
            var root = Path.Combine(Path.GetTempPath(), "UnityPackageExplorer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            state = new PreviewTemporaryState(root);
            _requestTemporaryRoots.Add(requestId, state);
        }
        return state.Path;
    }

    private string ConvertTgaToPng(string tgaPath)
    {
        var decoded = TgaDecoder.Decode(File.ReadAllBytes(tgaPath));
        var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra32Pixels, decoded.Width * 4);
        bitmap.Freeze();
        var pngPath = Path.ChangeExtension(tgaPath, ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(pngPath);
        encoder.Save(stream);
        return pngPath;
    }

    private void CleanupTemporaryFiles()
    {
        foreach (var (requestId, state) in _requestTemporaryRoots.ToArray())
            CleanupRequestTemporaryFiles(requestId, state);
    }

    private void CompletePreviewRequest(long requestId)
    {
        if (!_requestTemporaryRoots.TryGetValue(requestId, out var state)) return;
        state.InFlight = false;
        if (_activeSceneRequestId != requestId) CleanupRequestTemporaryFiles(requestId, state);
    }

    private void CleanupRequestTemporaryFiles(long requestId, PreviewTemporaryState state)
    {
        if (!_requestTemporaryRoots.Remove(requestId)) return;
        if (Directory.Exists(state.Path))
        {
            try { Directory.Delete(state.Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        OpenButton.IsEnabled = !_isOpeningSource;
        ExtractButton.IsEnabled = !busy && _packagePath is not null;
        MoveToCategoryButton.IsEnabled = !busy && CategoryPicker.SelectedItem is string;
        if (message is not null) SetStatus(message);
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
        PreviewProgress.Visibility = busy && !_isOpeningSource ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message) => StatusLabel.Text = message;

    private void ResetPreviewSelection()
    {
        _selectedPreviewEntry = null;
        _updatingSelection = true;
        try { PrefabPicker.SelectedItem = null; }
        finally { _updatingSelection = false; }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Unity アセットを選択",
            Filter = "Unity アセット|*.unitypackage;*.zip;*.prefab|UnityPackage|*.unitypackage|ZIP|*.zip|Prefab|*.prefab|すべてのファイル|*.*"
        };
        if (dialog.ShowDialog(this) == true) await OpenPathAsync(dialog.FileName);
    }

    private static string GetCategoryRoot(string path)
    {
        var parent = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return Categories.Contains(parent.Name, StringComparer.OrdinalIgnoreCase) && parent.Parent is not null
            ? parent.Parent.FullName
            : parent.FullName;
    }

    private void CategoryPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        MoveToCategoryButton.IsEnabled = !_isBusy && CategoryPicker.SelectedItem is string;
    }

    private void MoveToCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _catalog is null || CategoryPicker.SelectedItem is not string category) return;
        var root = GetCategoryRoot(_sourcePath);
        var source = Path.GetFullPath(_sourcePath);
        try
        {
            var destination = UnityZipCategorizer.MoveToExistingCategory(source, root, category);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"すでに「{category}」にあります。");
                return;
            }
            _catalog.RebindZipPath(destination);
            _sourcePath = destination;
            SourceLabel.Text = Path.GetFileName(destination);
            CategoryPanel.Visibility = Visibility.Collapsed;
            SetStatus($"「{category}」へ移動しました: {destination}");
        }
        catch (Exception ex)
        {
            SetStatus($"分類先への移動に失敗しました: {ex.Message}");
        }
    }

    private void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_packagePath is null) return;
        var dialog = new OpenFolderDialog { Title = "展開先フォルダーを選択", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var target = Path.Combine(dialog.FolderName, Path.GetFileNameWithoutExtension(_packagePath) + "_extracted");
            var output = new UnityPackageReader(_packagePath).ExtractTo(target);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{output}\"") { UseShellExecute = true });
            SetStatus($"展開しました: {output}");
        }
        catch (Exception ex)
        {
            SetStatus($"展開に失敗しました: {ex.Message}");
            MessageBox.Show(this, ex.Message, "UnityPackage Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_updatingFilter) return;
        if (_isOpeningSource) { _filterChangedWhileOpening = true; return; }
        CancelActivePreview();
        await RefreshTreeAsync();
    }

    private async void KindFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (KindFilter.SelectedItem is string kind) _selectedKind = kind;
        if (_isOpeningSource) { if (!_updatingFilter) _filterChangedWhileOpening = true; return; }
        if (!_updatingFilter && IsLoaded)
        {
            CancelActivePreview();
            await RefreshTreeAsync();
        }
    }
}
