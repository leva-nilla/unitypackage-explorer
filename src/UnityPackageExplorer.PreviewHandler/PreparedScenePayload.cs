using UnityPackageExplorer.Rendering;

namespace UnityPackageExplorer.PreviewHandler;

internal sealed class PreparedScenePayload(GeometryPreview preview, string tempRoot, IReadOnlyList<string> fallbackItems, int fallbackItemCount) : IDisposable
{
    private GeometryPreview? _preview = preview;
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
        var preview = Interlocked.Exchange(ref _preview, null);
        var root = Interlocked.Exchange(ref _tempRoot, null);
        if (preview is not null)
            preview.DisposeAfterRelease(root is null ? null : () => PreviewHandlerCom.SafeDeleteOwnedTemp(root));
        else if (root is not null)
            PreviewHandlerCom.SafeDeleteOwnedTemp(root);
    }
}

/// <summary>Phase-2 result carrier. Owns nothing: the geometry preview is owned by the surface once attached.</summary>
internal sealed class MaterialApplyPayload(GeometryPreview owner, MaterialApplyPlan plan, IReadOnlyList<string> warnings) : IDisposable
{
    private GeometryPreview? _owner = owner;
    private MaterialApplyPlan? _plan = plan;
    private IReadOnlyList<string>? _warnings = warnings;

    public void Attach(ExplorerPreviewSurface surface)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(MaterialApplyPayload));
        var plan = _plan ?? throw new ObjectDisposedException(nameof(MaterialApplyPayload));
        var warnings = _warnings ?? [];
        surface.ApplyMaterialPlan(owner, plan, warnings);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null);
        Interlocked.Exchange(ref _plan, null);
        Interlocked.Exchange(ref _warnings, null);
    }
}
