using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using UnityPackageExplorer.Rendering;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.PreviewHandler;

[ComVisible(true)]
[Guid(ClassId)]
[ProgId("UnityPackageExplorer.PreviewHandler")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PreviewHandlerCom : IPreviewHandler, IOleWindow, IInitializeWithFile, IInitializeWithStream, IObjectWithSite
{
    public const string ClassId = "70C67CC6-949B-4B5D-A8F2-7844D5C29E76";
    public const string AppId = "C50AF32D-4778-4B45-A2D3-FB0944F70271";
    private string? _path;
    private IntPtr _parent;
    private NativeRect _bounds;
    private Panel? _panel;
    private object? _site;
    private System.Runtime.InteropServices.ComTypes.IStream? _stream;
    private ExplorerPreviewSurface? _surface;
    private CancellationTokenSource? _requestCancellation;
    private long _generation;
    private long _selectionGeneration;
    private CancellationTokenSource? _selectionCancellation;
    private string? _requestTempRoot;
    private Task? _activeRequest;
    private readonly List<Task> _activeJobs = [];
    private readonly object _activeJobsGate = new();
    private UnityAssetCatalog? _requestCatalog;
    private readonly List<string> _selectionTempRoots = [];
    private readonly object _selectionGate = new();
    private readonly object _comCallGate = new();
    private PendingUiPayloadQueue _pendingUiPayloads = new();
    private PreviewStaDispatcher? _uiDispatcher;

    public int Initialize(string filePath, uint mode)
    {
        lock (_comCallGate)
        {
            ResetForInitializeOnUiThread();
            _path = Path.GetFullPath(filePath);
            _stream = null;
            return 0;
        }
    }

    public int Initialize(System.Runtime.InteropServices.ComTypes.IStream stream, uint mode)
    {
        lock (_comCallGate)
        {
            PreviewLog.Write("Initialize(stream)");
            ResetForInitializeOnUiThread();
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _path = null;
            return 0;
        }
    }

    public int SetSite(object? site)
    {
        lock (_comCallGate) { _site = site; return 0; }
    }

    public int GetSite(ref Guid interfaceId, out IntPtr site)
    {
        lock (_comCallGate)
        {
            site = IntPtr.Zero;
            if (_site is null) return unchecked((int)0x80004002);
            var unknown = Marshal.GetIUnknownForObject(_site);
            try { return Marshal.QueryInterface(unknown, in interfaceId, out site); }
            finally { Marshal.Release(unknown); }
        }
    }

    public int GetWindow(out IntPtr window)
    {
        lock (_comCallGate)
        {
            var dispatcher = _uiDispatcher;
            window = dispatcher is null ? IntPtr.Zero : dispatcher.Invoke(() => _panel?.IsHandleCreated == true ? _panel.Handle : IntPtr.Zero);
            return window == IntPtr.Zero ? unchecked((int)0x80004005) : 0;
        }
    }

    public int ContextSensitiveHelp(int enterMode) => unchecked((int)0x80004001);

    public int SetWindow(IntPtr parentWindow, ref NativeRect rect)
    {
        lock (_comCallGate)
        {
            var bounds = rect;
            PreviewLog.Write($"SetWindow parent=0x{parentWindow:X} rect={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom} parentRect={DescribeWindowRect(parentWindow)}");
            return GetOrCreateUiDispatcher().Invoke(() => SetWindowCore(parentWindow, bounds));
        }
    }

    public int SetRect(ref NativeRect rect)
    {
        lock (_comCallGate)
        {
            var bounds = rect;
            PreviewLog.Write($"SetRect rect={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}");
            var dispatcher = _uiDispatcher;
            if (dispatcher is null) { _bounds = bounds; return 0; }
            return dispatcher.Invoke(() => { _bounds = bounds; PositionPanel(); return 0; });
        }
    }

    public int DoPreview()
    {
        lock (_comCallGate)
        {
            if (string.IsNullOrWhiteSpace(_path) && _stream is null) return unchecked((int)0x80070002);
            PreviewLog.Write("DoPreview enter");
            return GetOrCreateUiDispatcher().Invoke(DoPreviewCore);
        }
    }

    private int DoPreviewCore()
    {
        PreviewLog.Write($"DoPreviewCore path={_path ?? "(stream)"}");
        var requestId = Interlocked.Increment(ref _generation);
        var initialDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 30d);
        ResetPendingUiPayloads();
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = new CancellationTokenSource();
        CancelSelection();
        ReleaseRequestResources();
        _requestTempRoot = _stream is null ? null : CreateRequestTempRoot();
        _panel?.Dispose();
        _surface?.Dispose();
        _surface = new ExplorerPreviewSurface();
        _surface.Host.BackColor = Color.FromArgb(22, 27, 34);
        _surface.Host.Padding = new Padding(0);
        _panel = new PreviewHostPanel { BackColor = Color.FromArgb(22, 27, 34), Padding = new Padding(0) };
        _panel.Controls.Add(_surface.Host);
        _surface.Host.Dock = DockStyle.Fill;
        _surface.ShowStatus("索引を準備しています…");
        _panel.CreateControl();
        if (_parent != IntPtr.Zero) { NativeMethods.SetParent(_panel.Handle, _parent); PreviewLog.Write($"DoPreviewCore SetParent panel=0x{_panel.Handle:X} parent=0x{_parent:X}"); PositionPanel(); }
        _panel.Visible = true;
        var cancellationToken = _requestCancellation.Token;
        var path = _path;
        var stream = _stream;
        _activeRequest = LoadRequestAsync(requestId, path, stream, _requestTempRoot, initialDeadline, cancellationToken);
        TrackJob(_activeRequest);
        return 0;
    }

    public int Unload()
    {
        lock (_comCallGate)
        {
            PreviewLog.Write("Unload");
            var dispatcher = _uiDispatcher;
            if (dispatcher is null) { UnloadCore(); return 0; }
            try { dispatcher.Invoke(UnloadCore); }
            finally
            {
                _uiDispatcher = null;
                dispatcher.Dispose();
            }
            return 0;
        }
    }

    private void UnloadCore()
    {
        Interlocked.Increment(ref _generation);
        ResetPendingUiPayloads();
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        CancelSelection();
        Interlocked.Increment(ref _selectionGeneration);
        ReleaseRequestResources();
        _surface?.Dispose();
        _surface = null;
        _panel?.Dispose();
        _panel = null;
        _path = null;
        _stream = null;
        _parent = IntPtr.Zero;
        _site = null;
    }

    private async Task LoadRequestAsync(long requestId, string? path, System.Runtime.InteropServices.ComTypes.IStream? stream, string? tempRoot, long initialDeadline, CancellationToken cancellationToken)
    {
        var displayName = path is null ? null : Path.GetFileName(path);
        var selectorSourceName = path;
        using var loadBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loadBudget.CancelAfter(RemainingBudget(initialDeadline));
        var loadToken = loadBudget.Token;
        try
        {
            if (stream is not null)
            {
                var marshaled = MarshalStream(stream);
                if (tempRoot is null) throw new InvalidOperationException("Request temp root was not created.");
                var copyTask = Task.Run(() => CopyMarshaledStream(marshaled, tempRoot, loadToken));
                TrackJob(copyTask);
                var spoolResult = await copyTask.ConfigureAwait(false);
                path = spoolResult.ReadPath;
                displayName = spoolResult.DisplayName ?? Path.GetFileName(path);
                selectorSourceName = spoolResult.SelectorSourceName;
            }
            if (path is null) throw new FileNotFoundException("Preview source is unavailable.");
            var catalogTask = Task.Run(() => OpenCatalog(path, loadToken), loadToken);
            TrackJob(catalogTask);
            var catalog = await catalogTask.ConfigureAwait(false);
            lock (_activeJobsGate)
            {
                if (cancellationToken.IsCancellationRequested || requestId != Volatile.Read(ref _generation))
                {
                    catalog.Dispose();
                    return;
                }
                _requestCatalog = catalog;
            }
            var entries = catalog.Entries.Where(x => x.HasAsset && x.Extension == ".prefab").ToArray();
            var selected = AutomaticPreviewSelector.Select(GetPreviewableEntries(catalog.Entries), selectorSourceName ?? path);
            PostToUi(requestId, () =>
            {
                var ordered = selected is not null && entries.Contains(selected) ? new[] { selected }.Concat(entries.Where(x => x != selected)) : entries;
                var initialSelection = true;
                if (_surface is not null) _surface.CandidateSelected += entry =>
                {
                    var budget = initialSelection ? RemainingBudget(initialDeadline) : TimeSpan.FromSeconds(30);
                    initialSelection = false;
                    StartSelected(requestId, path, displayName ?? Path.GetFileName(path), catalog, entry, cancellationToken, budget);
                };
                _surface?.SetCandidates(ordered);
                if (selected is not null) _surface?.ShowStatus($"読み込み中: {selected.PathName}");
            });
            if (selected is null) PostToUi(requestId, () => _surface?.ShowContentList(BuildSummary(path, catalog, displayName), catalog.Entries.Where(x => x.HasAsset).Select(x => x.PathName).Take(25).ToArray(), catalog.Entries.Count(x => x.HasAsset), catalog.Warnings));
            else if (selected.Extension != ".prefab") PostToUi(requestId, () => StartSelected(requestId, path, displayName ?? Path.GetFileName(path), catalog, selected, cancellationToken, RemainingBudget(initialDeadline)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { PreviewLog.Write($"LoadRequest timeout {displayName ?? path}"); PostToUi(requestId, () => _surface?.ShowStatus($"{displayName ?? Path.GetFileName(path)} · 読み込みが30秒を超えました。", ["安全のため処理を取り消しました。"])); }
        catch (Exception ex) { PreviewLog.Write("LoadRequest failed", ex); PostToUi(requestId, () => _surface?.ShowStatus("プレビューを作成できませんでした。", [ex.Message, DescribeBaseException(ex)])); }
    }

    private void ResetForInitialize()
    {
        Interlocked.Increment(ref _generation);
        ResetPendingUiPayloads();
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        CancelSelection();
        Interlocked.Increment(ref _selectionGeneration);
        ReleaseRequestResources();
        _surface?.Dispose();
        _surface = null;
        _panel?.Dispose();
        _panel = null;
        _path = null;
        _stream = null;
    }

    private void ResetForInitializeOnUiThread()
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is null) ResetForInitialize();
        else dispatcher.Invoke(ResetForInitialize);
    }

    private PreviewStaDispatcher GetOrCreateUiDispatcher()
        => _uiDispatcher ??= new PreviewStaDispatcher();

    private int SetWindowCore(IntPtr parentWindow, NativeRect rect)
    {
        _parent = parentWindow;
        _bounds = rect;
        PositionPanel();
        return 0;
    }

    private void StartSelected(long requestId, string path, string displayName, UnityAssetCatalog catalog, UnityPackageEntry entry, CancellationToken requestToken, TimeSpan? budget = null)
    {
        lock (_selectionGate)
        {
            if (requestId != Volatile.Read(ref _generation) || requestToken.IsCancellationRequested) return;
            if (_selectionCancellation is { } previous)
            {
                _selectionCancellation = null;
                previous.Cancel();
                previous.Dispose();
            }
            var selectionSource = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            selectionSource.CancelAfter(budget ?? TimeSpan.FromSeconds(30));
            _selectionCancellation = selectionSource;
            var selectionId = Interlocked.Increment(ref _selectionGeneration);
            var task = RunSelectedAndReleaseAsync(requestId, selectionId, path, displayName, catalog, entry, selectionSource);
            _activeRequest = task;
            TrackJob(task);
        }
    }

    private async Task RunSelectedAndReleaseAsync(long requestId, long selectionId, string path, string displayName, UnityAssetCatalog catalog, UnityPackageEntry entry, CancellationTokenSource selectionSource)
    {
        try { await LoadSelectedAsync(requestId, selectionId, path, displayName, catalog, entry, selectionSource.Token).ConfigureAwait(false); }
        finally
        {
            lock (_selectionGate)
            {
                if (ReferenceEquals(_selectionCancellation, selectionSource))
                {
                    _selectionCancellation = null;
                    selectionSource.Dispose();
                }
            }
        }
    }

    private void CancelSelection()
    {
        lock (_selectionGate)
        {
            if (_selectionCancellation is not { } current) return;
            _selectionCancellation = null;
            current.Cancel();
            current.Dispose();
        }
    }

    private async Task LoadSelectedAsync(long requestId, long selectionId, string path, string displayName, UnityAssetCatalog catalog, UnityPackageEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            if (entry.Extension == ".prefab" || entry.Extension == ".fbx")
            {
                AssimpNativeLoader.EnsureLoaded();
                PreviewLog.Write($"LoadSelected prefab {entry.PathName} assimp preloaded");
                string tempRoot;
                lock (_activeJobsGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tempRoot = CreateRequestTempRoot();
                    _selectionTempRoots.Add(tempRoot);
                }
                var geometryTask = LightweightPreviewLoader.LoadGeometryAsync(catalog, entry, tempRoot, cancellationToken, stageUnselectedSiblingImages: false);
                TrackJob(geometryTask);
                var geometry = await DeferredTaskCleanup.WaitAsync(
                    geometryTask,
                    cancellationToken,
                    static result => result.Dispose(),
                    () =>
                    {
                        SafeDeleteOwnedTemp(tempRoot);
                        lock (_activeJobsGate) _selectionTempRoots.Remove(tempRoot);
                    }).ConfigureAwait(false);
                PreviewLog.Write($"LoadSelected geometry scenes={geometry.Scenes.Count} warnings={geometry.Warnings.Count} :: {string.Join(" | ", geometry.Warnings)}");
                var fallbackItems = catalog.Entries.Where(x => x.HasAsset).Select(x => x.PathName).Take(25).ToArray();
                var geometryPayload = new PreparedScenePayload(geometry, tempRoot, fallbackItems, catalog.Entries.Count(x => x.HasAsset));
                PostOwnedToSelectionUi(requestId, selectionId, geometryPayload,
                    payload => { if (_surface is { } surface) payload.Attach(surface, $"{entry.PathName} · {BuildSummary(path, catalog, displayName)} · マテリアル適用中…"); else payload.Dispose(); });
                if (geometry.Scenes.Count == 0)
                {
                    var fallback = AutomaticPreviewSelector.Select(GetPreviewableEntries(catalog.Entries).Where(candidate => candidate.Kind == "Image"), displayName);
                    if (fallback is not null)
                    {
                        var fallbackTask = Task.Run(() => catalog.ReadAsset(fallback, cancellationToken: cancellationToken, maximumBytes: 64L * 1024 * 1024), cancellationToken);
                        TrackJob(fallbackTask);
                        var imageBytes = await fallbackTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                        using var fallbackStream = imageBytes is null ? null : new MemoryStream(imageBytes);
                        using var fallbackImage = fallbackStream is null ? null : new Bitmap(fallbackStream);
                        if (fallbackImage is not null)
                        {
                            var previewBitmap = ScaleImage((Bitmap)fallbackImage.Clone(), 2048);
                            PostOwnedToSelectionUi(requestId, selectionId, previewBitmap,
                                bitmap => { if (_surface is { } surface) surface.ShowImage(bitmap, $"{fallback.PathName} · {BuildSummary(path, catalog, displayName)}", geometry.Warnings); else bitmap.Dispose(); });
                        }
                    }
                    return;
                }
                if (geometry.IsDisposed || cancellationToken.IsCancellationRequested) return;
                try
                {
                    using var materialBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    materialBudget.CancelAfter(TimeSpan.FromSeconds(30));
                    var materialTask = LightweightPreviewLoader.CompleteMaterialsAsync(geometry, catalog, entry, tempRoot, materialBudget.Token);
                    TrackJob(materialTask);
                    var plan = await DeferredTaskCleanup.WaitAsync(
                        materialTask,
                        materialBudget.Token,
                        disposeLateResult: null,
                        () => SafeDeleteOwnedTemp(tempRoot)).ConfigureAwait(false);
                    PreviewLog.Write($"LoadSelected materials specs={plan.Plans.Sum(x => x.Specs.Count)} warnings={plan.Warnings.Count} :: {string.Join(" | ", plan.Warnings)}");
                    PostOwnedToSelectionUi(requestId, selectionId, new MaterialApplyPayload(geometry, plan, plan.Warnings),
                        payload =>
                        {
                            if (_surface is { } surface) payload.Attach(surface);
                            else { payload.Dispose(); SafeDeleteOwnedTemp(tempRoot); }
                        });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    PreviewLog.Write($"LoadSelected material phase failed {entry.PathName}", ex);
                    PostToSelectionUi(requestId, selectionId, () => _surface?.MarkMaterialPhaseFinished(), static () => { });
                }
                return;
            }
            var imageTask = Task.Run(() => catalog.ReadAsset(entry, cancellationToken: cancellationToken, maximumBytes: 64L * 1024 * 1024), cancellationToken);
            TrackJob(imageTask);
            var bytes = await imageTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            using var imageStream = bytes is null ? null : new MemoryStream(bytes);
            using var image = imageStream is null ? null : new Bitmap(imageStream);
            if (image is not null)
            {
                var previewBitmap = ScaleImage((Bitmap)image.Clone(), 2048);
                PostOwnedToSelectionUi(requestId, selectionId, previewBitmap,
                    bitmap => { if (_surface is { } surface) surface.ShowImage(bitmap, $"{entry.PathName} · {BuildSummary(path, catalog, displayName)}", catalog.Warnings); else bitmap.Dispose(); });
            }
            else PostToUi(requestId, () => _surface?.ShowStatus($"{displayName} · {entry.PathName}", catalog.Warnings));
        }
        catch (OperationCanceledException) when (requestId != Volatile.Read(ref _generation) || selectionId != Volatile.Read(ref _selectionGeneration) || _requestCancellation?.IsCancellationRequested == true) { }
        catch (OperationCanceledException) { PostToSelectionUi(requestId, selectionId, () => _surface?.ShowStatus(BuildSummary(path, catalog, displayName), [$"{entry.PathName}: 読み込みが30秒を超えました。", "安全のため処理を取り消しました。"]), static () => { }); }
        catch (Exception ex) { PreviewLog.Write($"LoadSelected failed {entry.PathName}", ex); PostToSelectionUi(requestId, selectionId, () => _surface?.ShowStatus($"{entry.PathName} を表示できませんでした。", [ex.Message, DescribeBaseException(ex)]), static () => { }); }
    }

    private static string DescribeBaseException(Exception exception)
    {
        var baseException = exception.GetBaseException();
        return baseException is null || string.Equals(baseException.Message, exception.Message, StringComparison.Ordinal)
            ? string.Empty
            : $"内側の例外: {baseException.Message}";
    }

    private void PostToUi(long requestId, Action action)
    {
        var dispatcher = Volatile.Read(ref _uiDispatcher);
        dispatcher?.Post(() =>
        {
            try
            {
                if (requestId == Volatile.Read(ref _generation)) action();
            }
            catch (Exception ex) { PreviewLog.Write("PostToUi action failed", ex); }
        });
    }

    private void PostToSelectionUi(long requestId, long selectionId, Action action, Action rejected)
    {
        var dispatcher = Volatile.Read(ref _uiDispatcher);
        if (dispatcher?.Post(() =>
        {
            try
            {
                if (requestId == Volatile.Read(ref _generation) && selectionId == Volatile.Read(ref _selectionGeneration)) action();
                else rejected();
            }
            catch (Exception ex) { PreviewLog.Write("PostToSelectionUi action failed", ex); }
        }) != true) rejected();
    }

    private void PostOwnedToSelectionUi<T>(long requestId, long selectionId, T payload, Action<T> consume) where T : class, IDisposable
    {
        if (requestId != Volatile.Read(ref _generation) || selectionId != Volatile.Read(ref _selectionGeneration))
        {
            payload.Dispose();
            return;
        }
        var queue = Volatile.Read(ref _pendingUiPayloads);
        var lease = queue.Enqueue(payload, consume);
        if (!lease.IsPending) return;
        if (requestId != Volatile.Read(ref _generation) || selectionId != Volatile.Read(ref _selectionGeneration))
        {
            lease.Dispose();
            return;
        }
        var dispatcher = Volatile.Read(ref _uiDispatcher);
        if (dispatcher?.Post(() =>
        {
            try
            {
                if (requestId == Volatile.Read(ref _generation) && selectionId == Volatile.Read(ref _selectionGeneration))
                {
                    PreviewLog.Write($"PostOwnedToSelectionUi consume {typeof(T).Name}");
                    lease.TryConsume();
                }
                else lease.Dispose();
            }
            catch (Exception ex) { PreviewLog.Write($"PostOwnedToSelectionUi {typeof(T).Name} failed", ex); }
        }) != true) lease.Dispose();
    }

    private void ResetPendingUiPayloads()
    {
        var previous = Interlocked.Exchange(ref _pendingUiPayloads, new PendingUiPayloadQueue());
        previous.Dispose();
    }

    private void TrackJob(Task job)
    {
        lock (_activeJobsGate) _activeJobs.Add(job);
        _ = job.ContinueWith(completed => { lock (_activeJobsGate) _activeJobs.Remove(completed); }, TaskScheduler.Default);
    }

    private static string BuildSummary(string path, UnityAssetCatalog catalog, string? displayName = null)
    {
        var kinds = catalog.Entries.Where(x => x.HasAsset).GroupBy(x => x.Kind).OrderBy(x => x.Key)
            .Select(x => $"{x.Key} {x.Count():N0}");
        return $"{displayName ?? Path.GetFileName(path)} · {catalog.Entries.Count(x => x.HasAsset):N0} assets · {string.Join(" / ", kinds)}";
    }

    private static TimeSpan RemainingBudget(long deadline)
    {
        var remaining = (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        return TimeSpan.FromSeconds(Math.Max(0, remaining));
    }

    private static IEnumerable<UnityPackageEntry> GetPreviewableEntries(IEnumerable<UnityPackageEntry> entries)
    {
        var supportedImages = new HashSet<string>([".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"], StringComparer.OrdinalIgnoreCase);
        return entries.Where(entry => entry.Kind != "Image" || supportedImages.Contains(entry.Extension));
    }

    private static Bitmap ScaleImage(Bitmap source, int maximumSize)
    {
        if (source.Width <= maximumSize && source.Height <= maximumSize) return source;
        var scale = Math.Min((double)maximumSize / source.Width, (double)maximumSize / source.Height);
        var resized = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, resized.Width, resized.Height);
        }
        source.Dispose();
        return resized;
    }

    private void ReleaseRequestResources()
    {
        Task[] jobs;
        UnityAssetCatalog? catalog;
        string[] roots;
        lock (_activeJobsGate)
        {
            jobs = _activeJobs.ToArray();
            _activeJobs.Clear();
            catalog = _requestCatalog;
            _requestCatalog = null;
            roots = _selectionTempRoots.Concat(_requestTempRoot is null ? [] : new[] { _requestTempRoot }).ToArray();
            _selectionTempRoots.Clear();
            _requestTempRoot = null;
            _activeRequest = null;
        }
        _ = Task.WhenAll(jobs).ContinueWith(completed =>
        {
            _ = completed.Exception;
            catalog?.Dispose();
            foreach (var root in roots) SafeDeleteOwnedTemp(root);
            PreviewLog.Write($"Request cleanup catalogs={(catalog is null ? 0 : 1)} roots={roots.Length}");
        }, TaskScheduler.Default);
    }

    private static UnityAssetCatalog OpenCatalog(string path, CancellationToken token)
    {
        // Explorer preview handlers run at low integrity and cannot write to the normal TEMP.
        var spillDirectory = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "UnityPackageExplorer", "ExpandedCache"));
        Directory.CreateDirectory(spillDirectory);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".unitypackage" => UnityAssetCatalog.OpenPackage(path, token, expandedCacheSpillDirectory: spillDirectory),
            ".zip" => UnityAssetCatalog.OpenZip(path, token, expandedCacheSpillDirectory: spillDirectory),
            ".rar" => UnityAssetCatalog.OpenRar(path, token, expandedCacheSpillDirectory: spillDirectory),
            ".prefab" => UnityAssetCatalog.OpenProjectPrefab(path, token),
            _ => throw new InvalidDataException("対応形式: .unitypackage / .zip / .rar / .prefab")
        };
    }

    private static string CreateRequestTempRoot()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "UnityPackageExplorer", "PreviewRequests", Guid.NewGuid().ToString("N"));
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        return root;
    }

    internal static void SafeDeleteOwnedTemp(string root)
    {
        var owner = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "UnityPackageExplorer", "PreviewRequests")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(owner, StringComparison.OrdinalIgnoreCase)) return;
        try { Directory.Delete(full, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly Guid IidIStream = new("0000000c-0000-0000-C000-000000000046");
    private static IntPtr MarshalStream(System.Runtime.InteropServices.ComTypes.IStream stream)
    {
        var unknown = Marshal.GetIUnknownForObject(stream);
        try
        {
            var iid = IidIStream;
            Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(in iid, unknown, out var marshaled));
            return marshaled;
        }
        finally { Marshal.Release(unknown); }
    }

    private static PreviewSourceSpoolResult CopyMarshaledStream(IntPtr marshaledStream, string tempRoot, CancellationToken cancellationToken)
    {
        var initResult = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
        if (initResult < 0)
        {
            Marshal.Release(marshaledStream);
            Marshal.ThrowExceptionForHR(initResult);
        }
        try
        {
            var iid = IidIStream;
            Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(marshaledStream, in iid, out var unknown));
            var rcw = Marshal.GetObjectForIUnknown(unknown);
            Marshal.Release(unknown);
            try
            {
                var stream = (System.Runtime.InteropServices.ComTypes.IStream)rcw;
                var displayName = PreviewSourceName.ReadStreamDisplayName(stream);
                using var input = new ComStreamReader(stream);
                var extension = Path.GetExtension(displayName ?? string.Empty).ToLowerInvariant();
                if (extension is not (".unitypackage" or ".zip" or ".rar" or ".prefab"))
                {
                    var header = new byte[512];
                    var count = input.Read(header, 0, header.Length);
                    input.Position = 0;
                    extension = count >= 4 && header[0] == 'P' && header[1] == 'K' ? ".zip"
                        : count >= 7 && Encoding.ASCII.GetString(header, 0, 4) == "Rar!" ? ".rar"
                        : count >= 2 && header[0] == 0x1f && header[1] == 0x8b ? ".unitypackage"
                        : count >= 5 && Encoding.ASCII.GetString(header, 0, count).TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("%YAML", StringComparison.Ordinal) ? ".prefab"
                        : ".zip";
                }
                var path = Path.Combine(tempRoot, "source" + extension);
                const long maximumSourceBytes = 8L * 1024 * 1024 * 1024;
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
                var buffer = new byte[1024 * 1024];
                long total = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    total += count;
                    if (total > maximumSourceBytes) throw new InvalidDataException("IStream source exceeds the 8 GiB bounded preview spool limit.");
                    output.Write(buffer, 0, count);
                }
                return PreviewSourceName.CreateSpoolResult(path, displayName);
            }
            finally { if (Marshal.IsComObject(rcw)) Marshal.ReleaseComObject(rcw); }
        }
        finally { CoUninitialize(); }
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoMarshalInterThreadInterfaceInStream(in Guid riid, IntPtr unknown, out IntPtr stream);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, in Guid riid, out IntPtr unknown);

    public int SetFocus()
    {
        lock (_comCallGate)
        {
            var dispatcher = _uiDispatcher;
            if (dispatcher is not null) dispatcher.Invoke(() => _panel?.Focus());
            return 0;
        }
    }

    public int QueryFocus(out IntPtr window)
    {
        lock (_comCallGate)
        {
            var dispatcher = _uiDispatcher;
            window = dispatcher is null ? IntPtr.Zero : dispatcher.Invoke(NativeMethods.GetFocus);
            return 0;
        }
    }

    public int TranslateAccelerator(ref NativeMessage message) => 1; // S_FALSE: the host can handle the key.

    private void PositionPanel()
    {
        if (_panel?.IsHandleCreated != true || _parent == IntPtr.Zero) return;
        var parentResult = NativeMethods.SetParent(_panel.Handle, _parent);
        var parentGle = Marshal.GetLastWin32Error();
        var width = Math.Max(0, _bounds.Right - _bounds.Left);
        var height = Math.Max(0, _bounds.Bottom - _bounds.Top);
        var posOk = NativeMethods.SetWindowPos(
            _panel.Handle,
            IntPtr.Zero,
            _bounds.Left,
            _bounds.Top,
            width,
            height,
            0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
        var posGle = Marshal.GetLastWin32Error();
        if (NativeMethods.GetWindowRect(_panel.Handle, out var actual))
        {
            PreviewLog.Write($"PositionPanel panel=0x{_panel.Handle:X} requested={_bounds.Left},{_bounds.Top} {width}x{height} actual={actual.Left},{actual.Top} {actual.Right - actual.Left}x{actual.Bottom - actual.Top} parent={DescribeWindowRect(_parent)} setParent=0x{parentResult:X}(gle={parentGle}) setPos={posOk}(gle={posGle}) visible={_panel.Visible}");
        }
        else
        {
            PreviewLog.Write($"PositionPanel panel=0x{_panel.Handle:X} requested={_bounds.Left},{_bounds.Top} {width}x{height} GetWindowRect failed(gle={Marshal.GetLastWin32Error()}) parent={DescribeWindowRect(_parent)} setParent=0x{parentResult:X}(gle={parentGle}) setPos={posOk}(gle={posGle}) visible={_panel.Visible}");
        }
    }

    private static string DescribeWindowRect(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero) return "zero";
            var isValid = NativeMethods.IsWindow(window);
            NativeMethods.GetWindowThreadProcessId(window, out var ownerPid);
            var className = new System.Text.StringBuilder(256);
            _ = NativeMethods.GetClassName(window, className, className.Capacity);
            var style = NativeMethods.GetWindowLong(window, -16); // GWL_STYLE
            var rectText = "n/a";
            if (NativeMethods.GetWindowRect(window, out var rect)) rectText = $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})";
            else rectText += $", gle={Marshal.GetLastWin32Error()}";
            return $"isWindow={isValid} ownerPid={ownerPid} class={className} style=0x{style:X} rect={rectText}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    private static PreviewContent ReadStreamContent(System.Runtime.InteropServices.ComTypes.IStream stream)
    {
        string? name = null;
        try
        {
            stream.Stat(out var statistics, 0);
            name = statistics.pwcsName;
        }
        catch (Exception) { }

        var adapter = new ComStreamReader(stream);
        var extension = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
        if (extension is not (".unitypackage" or ".prefab" or ".rar"))
        {
            var header = new byte[512];
            var read = adapter.Read(header, 0, header.Length);
            adapter.Position = 0;
            if (read >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K'
                && ((header[2] == 0x03 && header[3] == 0x04) || (header[2] == 0x05 && header[3] == 0x06) || (header[2] == 0x07 && header[3] == 0x08))) extension = ".zip";
            else if (read >= 7 && header[0] == (byte)'R' && header[1] == (byte)'a' && header[2] == (byte)'r' && header[3] == (byte)'!'
                && header[4] == 0x1a && header[5] == 0x07 && (header[6] == 0x00 || (read >= 8 && header[6] == 0x01 && header[7] == 0x00))) extension = ".rar";
            else if (read >= 2 && header[0] == 0x1f && header[1] == 0x8b) extension = ".unitypackage";
            else if (read >= 262 && Encoding.ASCII.GetString(header, 257, 5) == "ustar") extension = ".unitypackage";
            else
            {
                var text = Encoding.UTF8.GetString(header, 0, read).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                if (text.StartsWith("%YAML", StringComparison.Ordinal) || text.StartsWith("--- !u!", StringComparison.Ordinal)) extension = ".prefab";
            }
        }

        var title = string.IsNullOrWhiteSpace(name) ? $"Unity Asset{extension}" : Path.GetFileName(name);
        if (extension == ".unitypackage")
            return PreviewContent.ReadPackage(title, new UnityPackageReader(() =>
            {
                var source = new ComStreamReader(stream);
                source.Position = 0;
                return source;
            }));
        if (extension == ".zip")
            return PreviewContent.ReadZip(title, new UnityZipReader(() =>
            {
                var source = new ComStreamReader(stream);
                source.Position = 0;
                return source;
            }));
        if (extension == ".rar")
            return PreviewContent.ReadRar(title, new UnityRarReader(() =>
            {
                var source = new ComStreamReader(stream);
                source.Position = 0;
                return source;
            }));
        if (extension == ".prefab")
        {
            adapter.Position = 0;
            using var reader = new StreamReader(adapter, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return PreviewContent.ReadPrefab(title, reader.ReadToEnd());
        }

        return PreviewContent.Read(title);
    }
}

[ComVisible(true)]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithFile
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
}

[ComVisible(true)]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IStream stream, uint mode);
}

[ComVisible(true)]
[Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSite
{
    [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
    [PreserveSig] int GetSite(ref Guid interfaceId, out IntPtr site);
}

[ComVisible(true)]
[Guid("00000114-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleWindow
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp(int enterMode);
}

[ComVisible(true)]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandler
{
    [PreserveSig] int SetWindow(IntPtr parentWindow, ref NativeRect rect);
    [PreserveSig] int SetRect(ref NativeRect rect);
    [PreserveSig] int DoPreview();
    [PreserveSig] int Unload();
    [PreserveSig] int SetFocus();
    [PreserveSig] int QueryFocus(out IntPtr window);
    [PreserveSig] int TranslateAccelerator(ref NativeMessage message);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeMessage
{
    public IntPtr Window;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int X;
    public int Y;
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();
}

internal sealed class ComStreamReader(System.Runtime.InteropServices.ComTypes.IStream source) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            source.Stat(out var statistics, 1);
            return statistics.cbSize;
        }
    }
    public override long Position
    {
        get => Seek(0, SeekOrigin.Current);
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        var target = offset == 0 ? buffer : new byte[count];
        var readPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            Marshal.WriteInt32(readPointer, 0);
            source.Read(target, count, readPointer);
            var read = Math.Clamp(Marshal.ReadInt32(readPointer), 0, count);
            if (offset != 0 && read > 0) Array.Copy(target, 0, buffer, offset, read);
            return read;
        }
        finally { Marshal.FreeCoTaskMem(readPointer); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var positionPointer = Marshal.AllocCoTaskMem(sizeof(long));
        try
        {
            source.Seek(offset, (int)origin, positionPointer);
            return Marshal.ReadInt64(positionPointer);
        }
        finally { Marshal.FreeCoTaskMem(positionPointer); }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { }
}

internal sealed class PreviewHostPanel : Panel
{
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style |= 0x40000000; // WS_CHILD, required when hosted by Prevhost.
            return parameters;
        }
    }
}
