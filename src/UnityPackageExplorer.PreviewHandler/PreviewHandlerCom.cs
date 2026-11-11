using System.Drawing;
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

    public int Initialize(string filePath, uint mode)
    {
        ResetForInitialize();
        _path = Path.GetFullPath(filePath);
        _stream = null;
        return 0;
    }

    public int Initialize(System.Runtime.InteropServices.ComTypes.IStream stream, uint mode)
    {
        ResetForInitialize();
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _path = null;
        return 0;
    }

    public int SetSite(object? site)
    {
        _site = site;
        return 0;
    }

    public int GetSite(ref Guid interfaceId, out IntPtr site)
    {
        site = IntPtr.Zero;
        if (_site is null) return unchecked((int)0x80004002);
        var unknown = Marshal.GetIUnknownForObject(_site);
        try { return Marshal.QueryInterface(unknown, in interfaceId, out site); }
        finally { Marshal.Release(unknown); }
    }

    public int GetWindow(out IntPtr window)
    {
        window = _panel?.IsHandleCreated == true ? _panel.Handle : IntPtr.Zero;
        return window == IntPtr.Zero ? unchecked((int)0x80004005) : 0;
    }

    public int ContextSensitiveHelp(int enterMode) => unchecked((int)0x80004001);

    public int SetWindow(IntPtr parentWindow, ref NativeRect rect)
    {
        _parent = parentWindow;
        _bounds = rect;
        PositionPanel();
        return 0;
    }

    public int SetRect(ref NativeRect rect)
    {
        _bounds = rect;
        PositionPanel();
        return 0;
    }

    public int DoPreview()
    {
        if (string.IsNullOrWhiteSpace(_path) && _stream is null) return unchecked((int)0x80070002);
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = new CancellationTokenSource();
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        var previousRoot = _requestTempRoot;
        Task[] previousJobs;
        lock (_activeJobsGate) { previousJobs = _activeJobs.ToArray(); _activeJobs.Clear(); }
        if (previousRoot is not null)
        {
            _requestTempRoot = null;
            _ = Task.WhenAll(previousJobs).ContinueWith(_ => SafeDeleteOwnedTemp(previousRoot), TaskScheduler.Default);
        }
        _requestTempRoot = _stream is null ? null : CreateRequestTempRoot();
        var requestId = Interlocked.Increment(ref _generation);
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
        if (_parent != IntPtr.Zero) { NativeMethods.SetParent(_panel.Handle, _parent); PositionPanel(); }
        _panel.Visible = true;
        var cancellationToken = _requestCancellation.Token;
        var path = _path;
        var stream = _stream;
        _activeRequest = LoadRequestAsync(requestId, path, stream, _requestTempRoot, cancellationToken);
        TrackJob(_activeRequest);
        return 0;
    }

    public int Unload()
    {
        Interlocked.Increment(ref _generation);
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        Interlocked.Increment(ref _selectionGeneration);
        var requestRoot = _requestTempRoot;
        _requestTempRoot = null;
        if (requestRoot is not null)
        {
            Task[] jobs;
            lock (_activeJobsGate) { jobs = _activeJobs.ToArray(); _activeJobs.Clear(); }
            if (jobs.Length == 0) SafeDeleteOwnedTemp(requestRoot);
            else _ = Task.WhenAll(jobs).ContinueWith(_ => SafeDeleteOwnedTemp(requestRoot), TaskScheduler.Default);
        }
        _surface?.Dispose();
        _surface = null;
        _panel?.Dispose();
        _panel = null;
        _path = null;
        _stream = null;
        _parent = IntPtr.Zero;
        _site = null;
        return 0;
    }

    private async Task LoadRequestAsync(long requestId, string? path, System.Runtime.InteropServices.ComTypes.IStream? stream, string? tempRoot, CancellationToken cancellationToken)
    {
        using var loadBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loadBudget.CancelAfter(TimeSpan.FromSeconds(30));
        var loadToken = loadBudget.Token;
        try
        {
            if (stream is not null)
            {
                var marshaled = MarshalStream(stream);
                if (tempRoot is null) throw new InvalidOperationException("Request temp root was not created.");
                var copyTask = Task.Run(() => CopyMarshaledStream(marshaled, tempRoot, loadToken));
                TrackJob(copyTask);
                try { path = await copyTask.WaitAsync(loadToken).ConfigureAwait(false); }
                catch
                {
                    var ownedRoot = tempRoot;
                    _ = copyTask.ContinueWith(_ => SafeDeleteOwnedTemp(ownedRoot), TaskScheduler.Default);
                    if (ReferenceEquals(_requestTempRoot, ownedRoot)) _requestTempRoot = null;
                    throw;
                }
            }
            if (path is null) throw new FileNotFoundException("Preview source is unavailable.");
            var catalogTask = Task.Run(() => OpenCatalog(path, loadToken), loadToken);
            TrackJob(catalogTask);
            UnityAssetCatalog catalog;
            try { catalog = await catalogTask.WaitAsync(loadToken).ConfigureAwait(false); }
            catch
            {
                try { await catalogTask.ConfigureAwait(false); } catch { }
                throw;
            }
            var entries = catalog.Entries.Where(x => x.HasAsset && x.Extension == ".prefab").ToArray();
            var selected = AutomaticPreviewSelector.Select(catalog.Entries, path);
            PostToUi(requestId, () =>
            {
                var ordered = selected is not null && entries.Contains(selected) ? new[] { selected }.Concat(entries.Where(x => x != selected)) : entries;
                if (_surface is not null) _surface.CandidateSelected += entry => StartSelected(requestId, path, catalog, entry, cancellationToken);
                _surface?.SetCandidates(ordered);
                if (selected is not null) _surface?.ShowStatus($"読み込み中: {selected.PathName}");
            });
            if (selected is null) PostToUi(requestId, () => _surface?.ShowStatus(BuildSummary(path, catalog), catalog.Warnings));
            else if (selected.Extension != ".prefab") StartSelected(requestId, path, catalog, selected, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { PostToUi(requestId, () => _surface?.ShowStatus($"{Path.GetFileName(path)} · 読み込みが30秒を超えました。", ["安全のため処理を取り消しました。"])); }
        catch (Exception ex) { PostToUi(requestId, () => _surface?.ShowStatus("プレビューを作成できませんでした。", [ex.Message])); }
    }

    private void ResetForInitialize()
    {
        Interlocked.Increment(ref _generation);
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        Interlocked.Increment(ref _selectionGeneration);
        var root = _requestTempRoot;
        _requestTempRoot = null;
        if (root is not null)
        {
            Task[] jobs;
            lock (_activeJobsGate) { jobs = _activeJobs.ToArray(); _activeJobs.Clear(); }
            if (jobs.Length == 0) SafeDeleteOwnedTemp(root);
            else _ = Task.WhenAll(jobs).ContinueWith(_ => SafeDeleteOwnedTemp(root), TaskScheduler.Default);
        }
        _surface?.Dispose();
        _surface = null;
        _panel?.Dispose();
        _panel = null;
        _path = null;
        _stream = null;
    }

    private void StartSelected(long requestId, string path, UnityAssetCatalog catalog, UnityPackageEntry entry, CancellationToken requestToken)
    {
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        _selectionCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        var selectionId = Interlocked.Increment(ref _selectionGeneration);
        var selectionSource = _selectionCancellation;
        var task = RunSelectedAndReleaseAsync(requestId, selectionId, path, catalog, entry, selectionSource);
        _activeRequest = task;
        TrackJob(task);
    }

    private async Task RunSelectedAndReleaseAsync(long requestId, long selectionId, string path, UnityAssetCatalog catalog, UnityPackageEntry entry, CancellationTokenSource selectionSource)
    {
        try { await LoadSelectedAsync(requestId, selectionId, path, catalog, entry, selectionSource.Token).ConfigureAwait(false); }
        finally
        {
            if (ReferenceEquals(_selectionCancellation, selectionSource)) _selectionCancellation = null;
            selectionSource.Dispose();
        }
    }

    private async Task LoadSelectedAsync(long requestId, long selectionId, string path, UnityAssetCatalog catalog, UnityPackageEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            if (entry.Extension == ".prefab" || entry.Extension == ".fbx")
            {
                var tempRoot = CreateRequestTempRoot();
                var load = LightweightPreviewLoader.LoadAsync(catalog, entry, tempRoot, cancellationToken, stageUnselectedSiblingImages: false);
                PreparedPreview prepared;
                try { prepared = await load.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch
                {
                    try
                    {
                        var lateResult = await load.ConfigureAwait(false);
                        lateResult.Dispose();
                    }
                    catch { }
                    SafeDeleteOwnedTemp(tempRoot);
                    throw;
                }
                PostToSelectionUi(requestId, selectionId,
                    () => { if (_surface is { } surface) surface.ShowPrepared(prepared, $"{Path.GetFileName(path)} · {entry.PathName}", tempRoot); else { prepared.Dispose(); SafeDeleteOwnedTemp(tempRoot); } },
                    () => { prepared.Dispose(); SafeDeleteOwnedTemp(tempRoot); });
                if (prepared.Scenes.Count == 0)
                {
                    var fallback = AutomaticPreviewSelector.Select(catalog.Entries.Where(candidate => candidate.Kind == "Image"), path);
                    if (fallback is not null)
                    {
                        var fallbackTask = Task.Run(() => catalog.ReadAsset(fallback, cancellationToken: cancellationToken, maximumBytes: 64L * 1024 * 1024), cancellationToken);
                        TrackJob(fallbackTask);
                        var imageBytes = await fallbackTask.ConfigureAwait(false);
                        using var fallbackStream = imageBytes is null ? null : new MemoryStream(imageBytes);
                        using var fallbackImage = fallbackStream is null ? null : new Bitmap(fallbackStream);
                        if (fallbackImage is not null)
                        {
                            var previewBitmap = ScaleImage((Bitmap)fallbackImage.Clone(), 2048);
                            PostToSelectionUi(requestId, selectionId,
                                () => { if (_surface is { } surface) surface.ShowImage(previewBitmap, $"{Path.GetFileName(path)} · {fallback.PathName}", prepared.Warnings); else previewBitmap.Dispose(); },
                                previewBitmap.Dispose);
                        }
                    }
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
                PostToSelectionUi(requestId, selectionId,
                    () => { if (_surface is { } surface) surface.ShowImage(previewBitmap, $"{Path.GetFileName(path)} · {entry.PathName}", catalog.Warnings); else previewBitmap.Dispose(); },
                    previewBitmap.Dispose);
            }
            else PostToUi(requestId, () => _surface?.ShowStatus($"{Path.GetFileName(path)} · {entry.PathName}", catalog.Warnings));
        }
        catch (OperationCanceledException) when (requestId != Volatile.Read(ref _generation) || selectionId != Volatile.Read(ref _selectionGeneration) || _requestCancellation?.IsCancellationRequested == true) { }
        catch (OperationCanceledException) { PostToSelectionUi(requestId, selectionId, () => _surface?.ShowStatus(BuildSummary(path, catalog), [$"{entry.PathName}: 読み込みが30秒を超えました。", "安全のため処理を取り消しました。"]), static () => { }); }
        catch (Exception ex) { PostToSelectionUi(requestId, selectionId, () => _surface?.ShowStatus($"{entry.PathName} を表示できませんでした。", [ex.Message]), static () => { }); }
    }

    private void PostToUi(long requestId, Action action)
    {
        var panel = _panel;
        if (panel?.IsDisposed == true) return;
        try
        {
            if (panel?.IsHandleCreated == true) panel.BeginInvoke((Action)(() => { if (requestId == Volatile.Read(ref _generation) && !panel.IsDisposed) action(); }));
        }
        catch (InvalidOperationException) { }
    }

    private void PostToSelectionUi(long requestId, long selectionId, Action action, Action rejected)
    {
        var panel = _panel;
        if (panel?.IsDisposed == true) { rejected(); return; }
        try
        {
            if (panel?.IsHandleCreated == true)
                panel.BeginInvoke((Action)(() =>
                {
                    if (requestId == Volatile.Read(ref _generation) && selectionId == Volatile.Read(ref _selectionGeneration) && !panel.IsDisposed) action();
                    else rejected();
                }));
            else rejected();
        }
        catch (InvalidOperationException) { rejected(); }
    }

    private void TrackJob(Task job)
    {
        lock (_activeJobsGate) _activeJobs.Add(job);
        _ = job.ContinueWith(completed => { lock (_activeJobsGate) _activeJobs.Remove(completed); }, TaskScheduler.Default);
    }

    private static string BuildSummary(string path, UnityAssetCatalog catalog)
    {
        var kinds = catalog.Entries.Where(x => x.HasAsset).GroupBy(x => x.Kind).OrderBy(x => x.Key)
            .Select(x => $"{x.Key} {x.Count():N0}");
        return $"{Path.GetFileName(path)} · {catalog.Entries.Count(x => x.HasAsset):N0} assets · {string.Join(" / ", kinds)}";
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

    private static UnityAssetCatalog OpenCatalog(string path, CancellationToken token) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".unitypackage" => UnityAssetCatalog.OpenPackage(path, token),
        ".zip" => UnityAssetCatalog.OpenZip(path, token),
        ".rar" => UnityAssetCatalog.OpenRar(path, token),
        ".prefab" => UnityAssetCatalog.OpenProjectPrefab(path, token),
        _ => throw new InvalidDataException("対応形式: .unitypackage / .zip / .rar / .prefab")
    };

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

    private static string CopyMarshaledStream(IntPtr marshaledStream, string tempRoot, CancellationToken cancellationToken)
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
                var name = string.Empty;
                try { stream.Stat(out var stat, 1); name = stat.pwcsName ?? string.Empty; } catch (COMException) { }
                using var input = new ComStreamReader(stream);
                var extension = Path.GetExtension(name).ToLowerInvariant();
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
                const long maximumSourceBytes = 1024L * 1024 * 1024;
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
                var buffer = new byte[1024 * 1024];
                long total = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    total += count;
                    if (total > maximumSourceBytes) throw new InvalidDataException("IStream source exceeds the 1 GiB bounded preview spool limit.");
                    output.Write(buffer, 0, count);
                }
                return path;
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
        _panel?.Focus();
        return 0;
    }

    public int QueryFocus(out IntPtr window)
    {
        window = NativeMethods.GetFocus();
        return 0;
    }

    public int TranslateAccelerator(ref NativeMessage message) => 1; // S_FALSE: the host can handle the key.

    private void PositionPanel()
    {
        if (_panel?.IsHandleCreated != true || _parent == IntPtr.Zero) return;
        NativeMethods.SetParent(_panel.Handle, _parent);
        NativeMethods.SetWindowPos(
            _panel.Handle,
            IntPtr.Zero,
            _bounds.Left,
            _bounds.Top,
            Math.Max(0, _bounds.Right - _bounds.Left),
            Math.Max(0, _bounds.Bottom - _bounds.Top),
            0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
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
