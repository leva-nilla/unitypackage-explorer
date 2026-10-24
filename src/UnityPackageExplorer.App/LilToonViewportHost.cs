using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace UnityPackageExplorer.App;

internal sealed class LilToonViewportHost : Grid, IAsyncDisposable
{
    private readonly WebView2 _webView = new() { Visibility = Visibility.Visible };
    private readonly string _userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityPackageExplorer", "WebView2");
    private Task? _initialization;
    private Task _suspension = Task.CompletedTask;
    private bool _navigationReady;
    private long? _mappedRequest;
    private TaskCompletionSource<JsonElement>? _sceneResponse;

    public event EventHandler<string>? Warning;

    public LilToonViewportHost()
    {
        Background = new SolidColorBrush(Color.FromRgb(20, 25, 31));
        Children.Add(_webView);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _ = SendClearAsync();
                _suspension = SuspendWhenHiddenAsync();
            }
        };
    }

    public Task InitializeAsync()
    {
        if (_initialization is not null) return _initialization;
        _initialization = InitializeCoreAsync();
        return _initialization;
    }

    public async Task<JsonElement> LoadSceneAsync(long requestId, string sceneFilePath, string assetRoot, CancellationToken cancellationToken)
    {
        await InitializeAsync().WaitAsync(cancellationToken);
        await _suspension.WaitAsync(cancellationToken);
        var core = _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        if (core.IsSuspended) core.Resume();
        _sceneResponse?.TrySetCanceled();
        ClearMappedRequest(core);
        var assetsHost = $"assets-{requestId}.upe.local";
        // Keep the page and scene resources on the same virtual origin. WebView2
        // cross-origin fetch rules otherwise differ from the browser test server.
        var pageRoot = Path.Combine(assetRoot, ".liltoon-viewer");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(pageRoot);
            foreach (var name in new[] { "index.html", "viewer.js", "viewer.css" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(Path.Combine(AppContext.BaseDirectory, "WebViewer", name), Path.Combine(pageRoot, name), overwrite: true);
            }
        }, cancellationToken);
        core.SetVirtualHostNameToFolderMapping(assetsHost, assetRoot, CoreWebView2HostResourceAccessKind.Allow);
        _mappedRequest = requestId;
        var fileName = Uri.EscapeDataString(Path.GetFileName(sceneFilePath));
        var uri = $"https://{assetsHost}/{fileName}";
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sceneResponse = response;
        using var registration = cancellationToken.Register(() => response.TrySetCanceled(cancellationToken));
        await NavigateAsync(new Uri($"https://{assetsHost}/.liltoon-viewer/index.html"), cancellationToken);
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "loadScene", requestId, url = uri }));
        return await response.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
    }

    public async Task ClearAsync()
    {
        if (_webView.CoreWebView2 is { } core)
        {
            await SendClearAsync();
            ClearMappedRequest(core);
        }
    }

    private async Task InitializeCoreAsync()
    {
        var viewerRoot = Path.Combine(AppContext.BaseDirectory, "WebViewer");
        var index = Path.Combine(viewerRoot, "index.html");
        if (!File.Exists(index)) throw new FileNotFoundException("lilToon viewer files are missing.", index);
        Directory.CreateDirectory(_userDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
        await _webView.EnsureCoreWebView2Async(environment);
        var core = _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAllowedOrigin(args.Request.Uri))
                args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Length: 0");
        };
        core.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !IsAllowedOrigin(args.Uri)
                || uri.AbsolutePath != "/.liltoon-viewer/index.html") args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.WebMessageReceived += OnWebMessageReceived;
        _webView.NavigationCompleted += (_, args) =>
        {
            _navigationReady = args.IsSuccess;
            if (!args.IsSuccess) Warning?.Invoke(this, "lilToon renderer page could not be loaded.");
        };
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var source)
                || source.Scheme != Uri.UriSchemeHttps
                || !IsAllowedOrigin(args.Source)
                || source.AbsolutePath != "/.liltoon-viewer/index.html") return;
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("requestId", out var requestElement)
                || !requestElement.TryGetInt64(out var requestId)
                || requestId != _mappedRequest) return;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "sceneReady" || type == "sceneError") _sceneResponse?.TrySetResult(root.Clone());
        }
        catch (JsonException) { }
    }

    private async Task NavigateAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (_navigationReady && _webView.Source == uri) return;
        _navigationReady = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException("lilToon renderer page could not be loaded."));
        }
        _webView.NavigationCompleted += OnCompleted;
        try
        {
            _webView.Source = uri;
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally { _webView.NavigationCompleted -= OnCompleted; }
    }

    private Task SendClearAsync()
    {
        var core = _webView.CoreWebView2;
        if (core is null || _mappedRequest is not { } requestId) return Task.CompletedTask;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "clearScene", requestId }));
        _sceneResponse?.TrySetCanceled();
        _sceneResponse = null;
        return Task.CompletedTask;
    }

    private async Task SuspendWhenHiddenAsync()
    {
        try
        {
            if (_webView.CoreWebView2 is { } core && !core.IsSuspended)
                await core.TrySuspendAsync();
        }
        catch (InvalidOperationException) { }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void ClearMappedRequest(CoreWebView2 core)
    {
        if (_mappedRequest is not { } requestId) return;
        core.ClearVirtualHostNameToFolderMapping($"assets-{requestId}.upe.local");
        _mappedRequest = null;
    }

    private bool IsAllowedOrigin(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        return _mappedRequest is { } requestId
            && uri.Host.Equals($"assets-{requestId}.upe.local", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        try { await ClearAsync(); } catch { }
        _webView.Dispose();
    }
}
