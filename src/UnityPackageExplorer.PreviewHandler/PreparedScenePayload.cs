using UnityPackageExplorer.Rendering;

namespace UnityPackageExplorer.PreviewHandler;

internal sealed class PreparedScenePayload(PreparedPreview preview, string tempRoot, IReadOnlyList<string> fallbackItems, int fallbackItemCount) : IDisposable
{
    private PreparedPreview? _preview = preview;
    private string? _tempRoot = tempRoot;
    private readonly IReadOnlyList<string> _fallbackItems = fallbackItems;
    private readonly int _fallbackItemCount = fallbackItemCount;

    public void Attach(ExplorerPreviewSurface surface, string summary)
    {
        var current = _preview ?? throw new ObjectDisposedException(nameof(PreparedScenePayload));
        var root = _tempRoot ?? throw new ObjectDisposedException(nameof(PreparedScenePayload));
        surface.ShowPrepared(current, summary, root, _fallbackItems, _fallbackItemCount);
        _preview = null;
        _tempRoot = null;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _preview, null)?.Dispose();
        var root = Interlocked.Exchange(ref _tempRoot, null);
        if (root is not null) PreviewHandlerCom.SafeDeleteOwnedTemp(root);
    }
}
