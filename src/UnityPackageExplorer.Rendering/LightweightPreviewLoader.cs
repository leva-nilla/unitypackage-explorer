using System.IO;
using System.Numerics;
using System.Text;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using UnityPackageExplorer.Core;
using MeshNode = HelixToolkit.SharpDX.Model.Scene.MeshNode;

namespace UnityPackageExplorer.Rendering;

public sealed record RenderingMaterialBinding(
    string MaterialGuid,
    string MaterialName,
    string TextureName,
    string Slot,
    string? AssetPath,
    UnityMaterialColor? Color,
    UnityMaterialRenderMode RenderMode,
    float? Cutoff)
{
    public LilToonMaterialData? LilToon { get; init; }
    public IReadOnlyDictionary<string, string> LilToonTexturePaths { get; init; } = new Dictionary<string, string>();
}

/// <summary>Owns imported scene roots. Dispose it before deleting its temporary asset directory.</summary>
public sealed class PreparedPreview : IDisposable
{
    private int _disposed;
    internal PreparedPreview(IReadOnlyList<HelixToolkitScene> scenes, IReadOnlyList<RenderingMaterialBinding> bindings, IReadOnlyList<string> warnings)
    { Scenes = scenes; MaterialBindings = bindings; Warnings = warnings; }
    public IReadOnlyList<HelixToolkitScene> Scenes { get; }
    public IReadOnlyList<RenderingMaterialBinding> MaterialBindings { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var scene in Scenes) scene.Root.Dispose();
    }
}

/// <summary>Material application instruction for one mesh. Resolution only; no scene mutation happens until applied on the UI thread.</summary>
public sealed record MeshMaterialSpec(
    MeshNode Mesh,
    string? TexturePath,
    UnityMaterialColor? Color,
    UnityMaterialRenderMode Mode,
    float? Cutoff,
    bool Visible,
    bool IsTransparent);

/// <summary>Material plan for one imported scene.</summary>
public sealed record SceneMaterialPlan(
    HelixToolkitScene Scene,
    IReadOnlyList<MeshMaterialSpec> Specs,
    IReadOnlyList<RenderingMaterialBinding> Bindings,
    IReadOnlyDictionary<MeshNode, RenderingMaterialBinding> ExportBindings);

/// <summary>Phase-2 result: material specs to apply on the UI thread, plus material warnings.</summary>
public sealed record MaterialApplyPlan(IReadOnlyList<SceneMaterialPlan> Plans, IReadOnlyList<string> Warnings);

/// <summary>One imported scene whose materials are resolved later.</summary>
public sealed record GeometryPendingMaterials(
    HelixToolkitScene Scene,
    UnityPackageEntry ModelEntry,
    IReadOnlyList<UnityAssetReference> MaterialReferences,
    IReadOnlyList<PrefabRendererMaterialAssignment> RendererAssignments,
    IReadOnlyList<MeshNode> Meshes,
    /// <summary>Meshes whose assimp placeholder material was neutralized during phase 1; unmatched ones are hidden in phase 2.</summary>
    IReadOnlySet<MeshNode> PlaceholderMeshes,
    bool IsPrefabPreview,
    bool HasUnappliedMaterialOverrides)
{
    public ImportedModelSelection? Selection { get; init; }
}

/// <summary>Phase-1 result: geometry only. Materials are resolved by CompleteMaterialsAsync while the scenes are being displayed.</summary>
public sealed class GeometryPreview : IDisposable
{
    private int _disposed;
    private readonly object _lifetimeGate = new();
    private int _materialReaders;
    private bool _resourcesReleased;
    private List<Action>? _afterRelease;
    internal GeometryPreview(IReadOnlyList<HelixToolkitScene> scenes, IReadOnlyList<GeometryPendingMaterials> pending, IReadOnlyList<string> warnings)
    { Scenes = scenes; Pending = pending; Warnings = warnings; }
    public IReadOnlyList<HelixToolkitScene> Scenes { get; }
    public IReadOnlyList<GeometryPendingMaterials> Pending { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public void Dispose()
        => DisposeAfterRelease(null);

    /// <summary>Disposes geometry and runs cleanup only after active material readers finish.</summary>
    public void DisposeAfterRelease(Action? cleanup)
    {
        lock (_lifetimeGate)
        {
            if (cleanup is not null)
            {
                if (_resourcesReleased) { cleanup(); return; }
                (_afterRelease ??= []).Add(cleanup);
            }
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_materialReaders == 0) ReleaseResources();
        }
    }
    internal void BeginMaterials()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _materialReaders++;
        }
    }
    internal void EndMaterials()
    {
        lock (_lifetimeGate)
        {
            if (--_materialReaders == 0 && IsDisposed)
                ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        try { foreach (var scene in Scenes) scene.Root.Dispose(); }
        finally
        {
            _resourcesReleased = true;
            if (_afterRelease is { } callbacks)
            {
                _afterRelease = null;
                foreach (var callback in callbacks) callback();
            }
        }
    }
}

/// <summary>Prepares model scenes and resolves nested prefab variants using the shared Core parser.</summary>
public static class LightweightPreviewLoader
{
    public static async Task<PreparedPreview> LoadAsync(
        UnityAssetCatalog catalog,
        UnityPackageEntry selectedEntry,
        string temporaryRoot,
        CancellationToken cancellationToken,
        bool stageUnselectedSiblingImages = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selectedEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        return await Task.Run(() => LoadCore(catalog, selectedEntry, temporaryRoot, cancellationToken, stageUnselectedSiblingImages), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Phase 1: parse the prefab, extract referenced FBX files and import geometry. No Unity material I/O.</summary>
    public static async Task<GeometryPreview> LoadGeometryAsync(
        UnityAssetCatalog catalog,
        UnityPackageEntry selectedEntry,
        string temporaryRoot,
        CancellationToken cancellationToken,
        bool stageUnselectedSiblingImages = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selectedEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        return await Task.Run(() => LoadGeometryCore(catalog, selectedEntry, temporaryRoot, cancellationToken, stageUnselectedSiblingImages), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Phase 2: resolve Unity materials for the imported scenes. Mutates no scene; returns specs to apply on the UI thread.</summary>
    public static async Task<MaterialApplyPlan> CompleteMaterialsAsync(
        GeometryPreview geometry,
        UnityAssetCatalog catalog,
        UnityPackageEntry selectedEntry,
        string temporaryRoot,
        CancellationToken cancellationToken,
        Func<string, RenderingMaterialBinding, IDictionary<string, string>, ICollection<string>, CancellationToken, string>? prepareTexture = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ObjectDisposedException.ThrowIf(geometry.IsDisposed, geometry);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selectedEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        geometry.BeginMaterials();
        try
        {
            return await Task.Run(() => CompleteMaterialsCore(geometry, catalog, selectedEntry, temporaryRoot, cancellationToken, prepareTexture), cancellationToken).ConfigureAwait(false);
        }
        finally { geometry.EndMaterials(); }
    }

    private static PreparedPreview LoadCore(UnityAssetCatalog catalog, UnityPackageEntry entry, string temporaryRoot, CancellationToken token, bool stageSiblings)
    {
        var geometry = LoadGeometryCore(catalog, entry, temporaryRoot, token, stageSiblings);
        try
        {
            var plan = CompleteMaterialsCore(geometry, catalog, entry, temporaryRoot, token);
            var bindings = new List<RenderingMaterialBinding>();
            var textureCache = new Dictionary<string, HelixToolkit.SharpDX.TextureModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var pending in geometry.Pending)
            {
                var scenePlan = plan.Plans.FirstOrDefault(x => ReferenceEquals(x.Scene, pending.Scene));
                if (scenePlan is null) continue;
                foreach (var spec in scenePlan.Specs) ApplyMeshMaterialSpec(spec, textureCache);
                bindings.AddRange(scenePlan.Bindings);
            }
            return new PreparedPreview(geometry.Scenes, bindings, geometry.Warnings.Concat(plan.Warnings).Distinct(StringComparer.Ordinal).ToArray());
        }
        catch
        {
            geometry.Dispose();
            throw;
        }
    }

    private static GeometryPreview LoadGeometryCore(UnityAssetCatalog catalog, UnityPackageEntry entry, string temporaryRoot, CancellationToken token, bool stageSiblings)
    {
        var warnings = new List<string>();
        var placements = new List<PrefabModelPlacement>();
        var directModel = entry.Extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase);
        if (entry.Extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = catalog.ReadAsset(entry, cancellationToken: token, maximumBytes: UnityPrefabParser.MaximumResolvedPrefabFileBytes)
                ?? throw new InvalidDataException("Prefab data is missing.");
            var parsed = ResolvePrefab(catalog, entry, bytes, token);
            warnings.AddRange(parsed.Warnings);
            placements.AddRange(UnityPrefabParser.GetModelPlacements(parsed.Roots, useRootTransformForSkinnedMeshRenderers: true));
        }
        else if (directModel)
        {
            placements.Add(new PrefabModelPlacement(entry.Guid, Matrix4x4.Identity, new PrefabNode { FileId = 0, Name = entry.PathName }, [], []));
        }
        else warnings.Add($"Selected asset is not a Prefab or FBX: {entry.PathName}");

        var scenes = new List<HelixToolkitScene>();
        var pending = new List<GeometryPendingMaterials>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var placement in placements)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(placement.Guid))
                {
                    var fileIds = placement.MeshFileIds.Count == 0 ? "unknown" : string.Join(", ", placement.MeshFileIds);
                    warnings.Add($"{placement.Node.Name}: FBX {placement.Guid} is already shown once; Mesh fileID(s) {fileIds} could not be isolated, so this repeated whole-scene instance was skipped.");
                    continue;
                }
                var modelEntry = catalog.FindByGuid(placement.Guid, entry.PackageMemberPath, entry);
                if (modelEntry is null || !modelEntry.Extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"{placement.Node.Name}: referenced FBX {placement.Guid} was not found.");
                    continue;
                }
                try
                {
                    var modelPath = catalog.StageAsset(modelEntry, temporaryRoot, token);
                    if (stageSiblings) StageSiblingImages(catalog, temporaryRoot, modelEntry, token);
                    var scene = ImportModel(modelPath, placement.Transform, token);
                    var selection = ResolveModelSelection(scene.Root, placement.Guid, placement.RendererAssignments, placement.MaterialOverrides, warnings);
                    var meshes = EnumerateMeshes(scene.Root).ToArray();
                    var isPrefabPreview = !directModel;
                    var placeholders = new HashSet<MeshNode>();
                    if (isPrefabPreview)
                    {
                        // Neutralize assimp's saturated placeholder colors up front so the first paint never shows them,
                        // while preserving the material name for phase-2 matching.
                        foreach (var mesh in meshes)
                        {
                            if (mesh.Material is not HelixToolkit.SharpDX.Model.PhongMaterialCore imported
                                || imported.DiffuseMap is not null
                                || !IsSaturatedPrimaryColor(imported.DiffuseColor)) continue;
                            var neutral = CreatePreviewMaterial(null, NeutralPreviewColor, 1f);
                            neutral.Name = imported.Name;
                            mesh.Material = neutral;
                            placeholders.Add(mesh);
                        }
                    }
                    pending.Add(new GeometryPendingMaterials(
                        scene, modelEntry, placement.Materials, placement.RendererAssignments, meshes, placeholders,
                        isPrefabPreview, selection.UnresolvedOverrides.Count > 0) { Selection = selection });
                    scenes.Add(scene);
                    if (placement.UsesRootTransformApproximation)
                        warnings.Add($"{placement.Node.Name}: Skinned FBX is shown as a whole-scene approximation using the Prefab root transform.");
                    if (placement.MeshFileIds.Count > 0 && !selection.MeshesIsolated)
                        warnings.Add($"{placement.Node.Name}: Unity Mesh fileID(s) {string.Join(", ", placement.MeshFileIds)} could not be mapped to individual FBX meshes; the complete FBX scene is shown as an approximation.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var baseException = ex.GetBaseException();
                    warnings.Add(baseException is null || string.Equals(baseException.Message, ex.Message, StringComparison.Ordinal)
                        ? $"{placement.Node.Name}: {ex.Message}"
                        : $"{placement.Node.Name}: {ex.Message} (内側の例外: {baseException.Message})");
                }
            }
            return new GeometryPreview(scenes, pending, warnings.Distinct(StringComparer.Ordinal).ToArray());
        }
        catch
        {
            foreach (var scene in scenes) scene.Root.Dispose();
            throw;
        }
    }

    private static MaterialApplyPlan CompleteMaterialsCore(GeometryPreview geometry, UnityAssetCatalog catalog, UnityPackageEntry entry, string temporaryRoot, CancellationToken token,
        Func<string, RenderingMaterialBinding, IDictionary<string, string>, ICollection<string>, CancellationToken, string>? prepareTexture = null)
    {
        var plans = new List<SceneMaterialPlan>();
        var warnings = new List<string>();
        var textureCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pending in geometry.Pending)
        {
            token.ThrowIfCancellationRequested();
            var remaps = ReadMaterialRemaps(catalog, pending.ModelEntry, warnings, token);
            var references = IncludeRemappedMaterials(pending.MaterialReferences.Concat(pending.Selection?.Materials.Values ?? []).Distinct().ToArray(), pending.Meshes, remaps);
            var bindings = PrepareMaterialBindings(catalog, entry, temporaryRoot, references, token, warnings, textureCache, prepareTexture is not null);
            plans.Add(ResolveMaterialPlan(pending, bindings, warnings, token, textureCache, prepareTexture, remaps));
        }
        return new MaterialApplyPlan(plans, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static PrefabParseResult ResolvePrefab(UnityAssetCatalog catalog, UnityPackageEntry? entry, byte[] prefabBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(prefabBytes);
        if (prefabBytes.LongLength > UnityPrefabParser.MaximumResolvedPrefabFileBytes)
            throw new InvalidDataException("Prefab text exceeds the per-file resolution limit.");
        var sources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = prefabBytes.LongLength;
        return UnityPrefabParser.ParseResolved(Encoding.UTF8.GetString(prefabBytes), entry?.Guid, (guid, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (sources.TryGetValue(guid, out var cached)) return cached;
            var sourceEntry = catalog.FindByGuid(guid, entry?.PackageMemberPath, entry);
            if (sourceEntry is null || sourceEntry.Extension != ".prefab") return sources[guid] = null;
            var remaining = UnityPrefabParser.MaximumResolvedPrefabBytes - totalBytes;
            if (remaining <= 0) throw new InvalidDataException("Nested Prefab expansion exceeds the total text budget.");
            var sourceBytes = catalog.ReadAsset(sourceEntry, cancellationToken: token,
                maximumBytes: Math.Min(remaining, UnityPrefabParser.MaximumResolvedPrefabFileBytes));
            if (sourceBytes is null) return sources[guid] = null;
            totalBytes += sourceBytes.LongLength;
            return sources[guid] = Encoding.UTF8.GetString(sourceBytes);
        }, cancellationToken);
    }

    public static HelixToolkitScene ImportModel(string modelPath, Matrix4x4 transform, CancellationToken token)
    {
        using var importer = new Importer();
        var configuration = new ImporterConfiguration
        {
            ImportMaterialType = MaterialType.BlinnPhong,
            CreateSkeletonForBoneSkinningMesh = false
        };
        // FBX materials can look identical before Unity's .mat remaps are applied.
        // Merging them here destroys the identity needed to resolve each submesh.
        configuration.AssimpPostProcessSteps &= ~SharpAssimp.PostProcessSteps.RemoveRedundantMaterials;
        var scene = importer.Load(modelPath, configuration) ?? throw new InvalidDataException("FBX could not be loaded.");
        try
        {
            token.ThrowIfCancellationRequested();
            ImportedModelIdentity.Attach(scene.Root, FbxMaterialLayout.Read(modelPath, token));
            scene.Root.ModelMatrix *= transform;
            scene.Root.UpdateAllTransformMatrix();
            return scene;
        }
        catch { scene.Root.Dispose(); throw; }
    }

    public static ImportedModelSelection ResolveModelSelection(
        HelixToolkit.SharpDX.Model.Scene.SceneNode root, string modelGuid,
        IReadOnlyList<PrefabRendererMaterialAssignment> assignments, IReadOnlyList<PrefabMaterialOverride> overrides,
        ICollection<string> warnings) => ImportedModelIdentity.Resolve(root, modelGuid, assignments, overrides, warnings);

    public static IReadOnlyDictionary<string, UnityAssetReference> ReadMaterialRemaps(
        UnityAssetCatalog catalog, UnityPackageEntry? modelEntry, ICollection<string> warnings, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var bytes = modelEntry is null ? null : catalog.ReadAsset(modelEntry, "asset.meta", token);
            return UnityModelImporterMetadata.ParseMaterialRemaps(bytes is null ? null : Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            warnings.Add($"FBX material remaps could not be read: {ex.Message}");
        }
        return new Dictionary<string, UnityAssetReference>(StringComparer.Ordinal);
    }

    public static IReadOnlyList<UnityAssetReference> IncludeRemappedMaterials(
        IReadOnlyList<UnityAssetReference> references, HelixToolkit.SharpDX.Model.Scene.SceneNode root,
        IReadOnlyDictionary<string, UnityAssetReference> remaps)
        => IncludeRemappedMaterials(references, EnumerateMeshes(root), remaps);

    private static IReadOnlyList<UnityAssetReference> IncludeRemappedMaterials(
        IReadOnlyList<UnityAssetReference> references, IEnumerable<MeshNode> meshes,
        IReadOnlyDictionary<string, UnityAssetReference> remaps)
        => references.Concat(meshes.Select(mesh => mesh.Material?.Name)
            .Where(name => name is not null && remaps.ContainsKey(name))
            .Select(name => remaps[name!])).Distinct().ToArray();

    private static IReadOnlyList<RenderingMaterialBinding> PrepareMaterialBindings(
        UnityAssetCatalog catalog,
        UnityPackageEntry referenceEntry,
        string temporaryRoot,
        IReadOnlyList<UnityAssetReference> references,
        CancellationToken token,
        ICollection<string> warnings,
        IDictionary<string, string> textureCache,
        bool prepareTextures)
    {
        var bindings = new List<RenderingMaterialBinding>();
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            if (reference.Guid is not { } guid)
            {
                if (reference.FileId != 0) warnings.Add($"Material fileID {reference.FileId} has no GUID and cannot be resolved.");
                continue;
            }
            var materialPath = catalog.ResolvePath(guid, referenceEntry.PackageMemberPath, referenceEntry);
            var info = catalog.ReadMaterialInfo(guid, token, referenceEntry.PackageMemberPath, referenceEntry);
            if (info is null)
            {
                warnings.Add(materialPath is null ? $"Material GUID {guid} was not found." : $"Material data is missing: {materialPath}");
                continue;
            }
            var materialName = info.Name ?? (materialPath is null ? guid : Path.GetFileNameWithoutExtension(materialPath));
            foreach (var feature in info.LilToon is null ? info.EnabledUnsupportedFeatures : [])
                warnings.Add($"Material '{materialName}' enables {feature}, which is not included in the simplified material preview.");
            if (info.LilToon is null && info.RenderMode == UnityMaterialRenderMode.Cutout)
                warnings.Add(info.Cutoff is null
                    ? $"Material '{materialName}' uses alpha cutout but has no readable cutoff; the texture alpha is not masked."
                    : $"Material '{materialName}' uses alpha cutout. It is shown with a binary texture-alpha approximation; depth and sorting may differ from Unity.");
            foreach (var unsupportedGuid in info.LilToon is null ? info.UnsupportedTextureGuids : [])
            {
                var unsupportedPath = catalog.ResolvePath(unsupportedGuid, referenceEntry.PackageMemberPath, referenceEntry);
                warnings.Add(unsupportedPath is null
                    ? $"Texture GUID {unsupportedGuid} referenced by material '{materialName}' was not found."
                    : $"Texture {unsupportedPath} is referenced by a material slot that this simplified viewer does not render.");
            }
            foreach (var texture in info.SupportedTextures)
            {
                token.ThrowIfCancellationRequested();
                var sourcePath = catalog.ResolvePath(texture.Guid, referenceEntry.PackageMemberPath, referenceEntry);
                if (sourcePath is null)
                {
                    warnings.Add($"Texture GUID {texture.Guid} referenced by material '{materialName}' was not found.");
                    continue;
                }
                var extension = Path.GetExtension(sourcePath);
                var isTga = extension.Equals(".tga", StringComparison.OrdinalIgnoreCase);
                if (!isTga && !new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    warnings.Add($"Texture format is not supported for 3D display: {sourcePath}");
                    continue;
                }
                if (isTga && !prepareTextures)
                {
                    warnings.Add($"Texture format is not supported for 3D display: {sourcePath}");
                    continue;
                }
                var staged = catalog.StageImage(texture.Guid, temporaryRoot, token, referenceEntry.PackageMemberPath, referenceEntry);
                if (staged is null)
                {
                    warnings.Add($"Texture data is missing: {sourcePath}");
                    continue;
                }
                if (isTga)
                {
                    if (!AlphaTexturePreparation.TryConvertTgaToPng(staged, materialName, textureCache, warnings, token, out var converted))
                        continue;
                    staged = converted;
                }
                bindings.Add(new RenderingMaterialBinding(guid, materialName, Path.GetFileNameWithoutExtension(sourcePath), texture.Slot, staged, info.Color, info.RenderMode, info.Cutoff)
                {
                    LilToon = info.LilToon
                });
            }
            if (!bindings.Any(binding => binding.MaterialGuid.Equals(guid, StringComparison.OrdinalIgnoreCase)))
                bindings.Add(new RenderingMaterialBinding(guid, materialName, string.Empty, string.Empty, null, info.Color, info.RenderMode, info.Cutoff) { LilToon = info.LilToon });
            if (info.RenderMode == UnityMaterialRenderMode.Unknown && info.Color is { A: < 1f })
                warnings.Add($"Material '{materialName}' has alpha color but no explicit transparent mode; it remains opaque in this preview.");
            else if (info.RenderMode == UnityMaterialRenderMode.Unknown && info.IsLilToonShaderAsset)
                warnings.Add($"Material '{materialName}' uses an unrecognized lilToon shader variant; its render mode is left opaque in this simplified preview.");
        }
        return bindings;
    }

    public static IReadOnlyDictionary<HelixToolkit.SharpDX.Model.Scene.MeshNode, RenderingMaterialBinding> ApplyUnityMaterials(
        HelixToolkit.SharpDX.Model.Scene.SceneNode root,
        IReadOnlyList<RenderingMaterialBinding> bindings,
        IReadOnlyList<UnityAssetReference> materialReferences,
        IReadOnlyList<PrefabRendererMaterialAssignment> rendererAssignments,
        bool isPrefabPreview,
        bool hasUnappliedMaterialOverrides,
        CancellationToken token,
        IDictionary<string, string>? alphaTextureCache,
        Func<string, RenderingMaterialBinding, IDictionary<string, string>, ICollection<string>, CancellationToken, string>? prepareTexture = null,
        ICollection<string>? warningSink = null,
        IReadOnlyDictionary<string, UnityAssetReference>? materialRemaps = null,
        ImportedModelSelection? selection = null)
    {
        var meshes = EnumerateMeshes(root).ToArray();
        var placeholders = new HashSet<MeshNode>(CollectPlaceholderMeshes(meshes));
        var warnings = warningSink ?? new List<string>();
        var resolution = ResolveSpecs(meshes, placeholders, bindings, materialReferences, rendererAssignments,
            isPrefabPreview, hasUnappliedMaterialOverrides, alphaTextureCache, prepareTexture, warnings, token, materialRemaps, selection);
        var textureCache = new Dictionary<string, HelixToolkit.SharpDX.TextureModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in resolution.Specs) ApplyMeshMaterialSpec(spec, textureCache);
        return resolution.ExportBindings;
    }

    /// <summary>Applies resolved material specs to the live scene. Intended to run on the UI thread.</summary>
    public static void ApplyMaterialSpecs(IEnumerable<MeshMaterialSpec> specs)
    {
        var textureCache = new Dictionary<string, HelixToolkit.SharpDX.TextureModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs) ApplyMeshMaterialSpec(spec, textureCache);
    }

    private sealed record MaterialResolution(
        IReadOnlyList<MeshMaterialSpec> Specs,
        IReadOnlyList<RenderingMaterialBinding> Bindings,
        IReadOnlyDictionary<MeshNode, RenderingMaterialBinding> ExportBindings);

    private static SceneMaterialPlan ResolveMaterialPlan(GeometryPendingMaterials pending, IReadOnlyList<RenderingMaterialBinding> bindings, ICollection<string> warnings, CancellationToken token,
        IDictionary<string, string>? alphaTextureCache = null,
        Func<string, RenderingMaterialBinding, IDictionary<string, string>, ICollection<string>, CancellationToken, string>? prepareTexture = null,
        IReadOnlyDictionary<string, UnityAssetReference>? materialRemaps = null)
    {
        var resolution = ResolveSpecs(pending.Meshes, pending.PlaceholderMeshes, bindings, pending.MaterialReferences,
            pending.RendererAssignments, pending.IsPrefabPreview, pending.HasUnappliedMaterialOverrides, alphaTextureCache, prepareTexture, warnings, token, materialRemaps, pending.Selection);
        return new SceneMaterialPlan(pending.Scene, resolution.Specs, resolution.Bindings, resolution.ExportBindings);
    }

    /// <summary>Mutates no scene node; produces per-mesh material instructions instead.</summary>
    private static MaterialResolution ResolveSpecs(
        IReadOnlyList<MeshNode> meshes,
        IReadOnlySet<MeshNode> placeholderMeshes,
        IReadOnlyList<RenderingMaterialBinding> bindings,
        IReadOnlyList<UnityAssetReference> materialReferences,
        IReadOnlyList<PrefabRendererMaterialAssignment> rendererAssignments,
        bool isPrefabPreview,
        bool hasUnappliedMaterialOverrides,
        IDictionary<string, string>? alphaTextureCache,
        Func<string, RenderingMaterialBinding, IDictionary<string, string>, ICollection<string>, CancellationToken, string>? prepareTexture,
        ICollection<string> warnings,
        CancellationToken token,
        IReadOnlyDictionary<string, UnityAssetReference>? materialRemaps,
        ImportedModelSelection? selection)
    {
        var exportBindings = new Dictionary<MeshNode, RenderingMaterialBinding>();
        var specs = new List<MeshMaterialSpec>();
        alphaTextureCache ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (hasUnappliedMaterialOverrides)
            warnings.Add("Nested Prefab material override textures were inspected for diagnostics but not applied because their FBX submesh targets could not be identified safely.");
        if (bindings.Count == 0)
        {
            if (isPrefabPreview)
            {
                foreach (var mesh in meshes)
                {
                    if (mesh.Material is not HelixToolkit.SharpDX.Model.PhongMaterialCore imported || imported.DiffuseMap is not null) continue;
                    if (imported.RenderDiffuseMap && imported.DiffuseColor.Alpha <= 0f)
                        specs.Add(CreateSpec(mesh, null, null, UnityMaterialRenderMode.Opaque, null));
                    else if (IsSaturatedPrimaryColor(imported.DiffuseColor))
                        specs.Add(CreateSpec(mesh, null, NeutralPreviewColor, UnityMaterialRenderMode.Opaque, null));
                }
                if (!hasUnappliedMaterialOverrides)
                    warnings.Add(UnityMaterialPreviewDiagnostics.GetMissingSupportedTextureWarning(materialReferences.Count));
            }
            return new MaterialResolution(specs, bindings, exportBindings);
        }
        var applied = new HashSet<RenderingMaterialBinding>();
        foreach (var mesh in meshes)
        {
            token.ThrowIfCancellationRequested();
            var name = mesh.Material?.Name;
            if (selection?.ScopedMeshes.Contains(mesh) == true && !selection.Materials.ContainsKey(mesh)) continue;
            var hasExplicit = selection?.Materials.TryGetValue(mesh, out _) == true;
            var matchedRenderers = rendererAssignments.Where(x => UnityMaterialNameMatcher.HaveSameNormalizedName(x.RendererName, mesh.Name)).ToArray();
            if (matchedRenderers.Length > 1 && !hasExplicit)
            {
                warnings.Add($"Several Unity renderers match FBX mesh '{mesh.Name}'; no material settings were applied.");
                continue;
            }
            var scoped = matchedRenderers.Length == 1 && !hasExplicit
                ? bindings.Where(binding => matchedRenderers[0].Materials.Any(reference => string.Equals(reference.Guid, binding.MaterialGuid, StringComparison.OrdinalIgnoreCase)))
                : bindings;
            IReadOnlyList<string> guids;
            if (hasExplicit)
                guids = selection!.Materials[mesh].Guid is { } explicitGuid ? [explicitGuid] : [];
            else if (matchedRenderers.Length == 1 && matchedRenderers[0].Materials.Count == 1)
                guids = matchedRenderers[0].Materials[0].Guid is { } rendererGuid ? [rendererGuid] : [];
            else if (matchedRenderers.Length == 0 && name is not null && materialRemaps?.TryGetValue(name, out var remap) == true)
                guids = remap.Guid is { } remapGuid ? [remapGuid] : [];
            else
                guids = UnityMaterialNameMatcher.FindMatchingMaterialGuids(scoped.Select(binding => (binding.MaterialGuid, binding.MaterialName, (string?)binding.TextureName)), name, mesh.Name);
            if (guids.Count != 1)
            {
                if (guids.Count > 1) warnings.Add($"Several Unity materials match FBX material '{mesh.Name}'; no material settings were applied.");
                continue;
            }
            var matching = scoped.Where(binding => binding.MaterialGuid.Equals(guids[0], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0) continue;
            var textured = matching.Where(binding => binding.AssetPath is not null)
                .OrderBy(binding => binding.Slot is "_BaseMap" or "_BaseColorMap" or "_BaseColorTexture" ? 0 : binding.Slot == "_MainTex" ? 1 : 2).ToArray();
            var priority = textured.Length == 0 ? int.MaxValue : textured[0].Slot is "_BaseMap" or "_BaseColorMap" or "_BaseColorTexture" ? 0 : textured[0].Slot == "_MainTex" ? 1 : 2;
            var paths = textured.Where(binding => (binding.Slot is "_BaseMap" or "_BaseColorMap" or "_BaseColorTexture" ? 0 : binding.Slot == "_MainTex" ? 1 : 2) == priority)
                .Select(binding => binding.AssetPath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > 1) { warnings.Add($"Several main textures match FBX material '{mesh.Name}'; no material settings were applied."); continue; }
            var binding = matching[0];
            var path = paths.SingleOrDefault();
            if (path is not null && prepareTexture is not null) path = prepareTexture(path, binding, alphaTextureCache, warnings, token);
            specs.Add(CreateSpec(mesh, path, binding.Color, binding.RenderMode, binding.Cutoff));
            exportBindings[mesh] = binding;
            foreach (var item in matching) applied.Add(item);
        }
        var importedNames = meshes.Select(mesh => mesh.Material?.Name).ToArray();
        var singleNamedMaterial = importedNames.Length > 0 && importedNames.All(name => !string.IsNullOrWhiteSpace(name))
            && importedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        var oneUnityMaterial = materialReferences.Count == 1 && bindings.Select(binding => binding.MaterialGuid).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        var fallbackPaths = bindings.Where(binding => binding.AssetPath is not null).Select(binding => binding.AssetPath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (exportBindings.Count == 0 && rendererAssignments.Count == 0 && (materialRemaps is null || materialRemaps.Count == 0)
            && (selection is null || selection.ScopedMeshes.Count == 0)
            && oneUnityMaterial && singleNamedMaterial && fallbackPaths.Length <= 1)
        {
            var fallback = bindings[0];
            var fallbackPath = fallbackPaths.SingleOrDefault();
            if (fallbackPath is not null && prepareTexture is not null)
                fallbackPath = prepareTexture(fallbackPath, fallback, alphaTextureCache, warnings, token);
            foreach (var mesh in meshes)
            {
                specs.Add(CreateSpec(mesh, fallbackPath, fallback.Color, fallback.RenderMode, fallback.Cutoff));
                exportBindings[mesh] = fallback;
            }
            foreach (var binding in bindings) applied.Add(binding);
        }
        foreach (var binding in bindings.Except(applied).DistinctBy(x => x.MaterialGuid, StringComparer.OrdinalIgnoreCase))
            warnings.Add($"Material '{binding.MaterialName}' could not be matched to an FBX material and was left unused.");
        if (isPrefabPreview)
        {
            var unmatched = meshes.Where(mesh => !exportBindings.ContainsKey(mesh)).ToArray();
            foreach (var mesh in unmatched)
            {
                if (!placeholderMeshes.Contains(mesh)) continue;
                // Hide saturated placeholder spheres (e.g. assimp's red) instead of leaving them visible.
                specs.Add(CreateSpec(mesh, null, NeutralPreviewColor, UnityMaterialRenderMode.Opaque, null) with { Visible = false });
            }
            if (unmatched.Length > 0)
                warnings.Add($"{unmatched.Length:N0} FBX mesh(es) had no matching Unity material.");
        }
        return new MaterialResolution(specs, bindings, exportBindings);
    }

    private static IEnumerable<MeshNode> CollectPlaceholderMeshes(IEnumerable<MeshNode> meshes)
    {
        foreach (var mesh in meshes)
        {
            if (mesh.Material is HelixToolkit.SharpDX.Model.PhongMaterialCore imported
                && imported.DiffuseMap is null
                && IsSaturatedPrimaryColor(imported.DiffuseColor)) yield return mesh;
        }
    }

    private static MeshMaterialSpec CreateSpec(MeshNode mesh, string? path, UnityMaterialColor? color, UnityMaterialRenderMode mode, float? cutoff)
    {
        var visible = true;
        if (path is null && UnityMaterialPreviewPolicy.CutoutThreshold(mode, cutoff) is { } threshold)
            visible = (color?.A ?? 1f) >= threshold;
        return new MeshMaterialSpec(mesh, path, color, mode, cutoff, visible, UnityMaterialPreviewPolicy.UsesTransparentPass(mode));
    }

    private static void ApplyMeshMaterialSpec(MeshMaterialSpec spec, IDictionary<string, HelixToolkit.SharpDX.TextureModel>? textureCache = null)
    {
        spec.Mesh.Material = CreatePreviewMaterial(spec.TexturePath, spec.Color, UnityMaterialPreviewPolicy.DisplayAlpha(spec.Color, spec.Mode), textureCache);
        spec.Mesh.IsTransparent = spec.IsTransparent;
        spec.Mesh.Visible = spec.Visible;
    }

    private static readonly UnityMaterialColor NeutralPreviewColor = new(0.72f, 0.72f, 0.72f, 1f);

    /// <summary>True when an imported FBX material is a saturated pure hue (e.g. assimp's red placeholder) that reads as an error.</summary>
    private static bool IsSaturatedPrimaryColor(HelixToolkit.Maths.Color4 color)
    {
        var max = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        var min = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        return max > 0.7f && max - min > 0.5f;
    }

    private static void SetMaterial(HelixToolkit.SharpDX.Model.Scene.MeshNode mesh, string? path, UnityMaterialColor? color, UnityMaterialRenderMode mode, float? cutoff)
    {
        var alpha = UnityMaterialPreviewPolicy.DisplayAlpha(color, mode);
        mesh.Material = CreatePreviewMaterial(path, color, alpha);
        mesh.IsTransparent = UnityMaterialPreviewPolicy.UsesTransparentPass(mode);
        if (path is null && UnityMaterialPreviewPolicy.CutoutThreshold(mode, cutoff) is { } threshold)
            mesh.Visible = (color?.A ?? 1f) >= threshold;
    }

    public static HelixToolkit.SharpDX.Model.PhongMaterialCore CreatePreviewMaterial(string? path, UnityMaterialColor? color, float alpha,
        IDictionary<string, HelixToolkit.SharpDX.TextureModel>? textureCache = null) =>
        new()
        {
            AmbientColor = new HelixToolkit.Maths.Color4(0.08f, 0.08f, 0.08f, 1),
            DiffuseColor = new HelixToolkit.Maths.Color4(color?.R ?? 1, color?.G ?? 1, color?.B ?? 1, alpha),
            EmissiveColor = new HelixToolkit.Maths.Color4(0, 0, 0, 1),
            SpecularColor = new HelixToolkit.Maths.Color4(0.06f, 0.06f, 0.06f, 1),
            SpecularShininess = 24,
            RenderDiffuseMap = path is not null,
            DiffuseMap = path is null ? null : ResolveTextureModel(path, textureCache)
        };

    private static HelixToolkit.SharpDX.TextureModel? ResolveTextureModel(string path, IDictionary<string, HelixToolkit.SharpDX.TextureModel>? cache)
    {
        if (cache is not null && cache.TryGetValue(path, out var cached)) return cached;
        var model = HelixToolkit.SharpDX.TextureModel.Create(path);
        if (model is null || cache is null) return model;
        cache.Add(path, model);
        return model;
    }

    private static IEnumerable<HelixToolkit.SharpDX.Model.Scene.MeshNode> EnumerateMeshes(HelixToolkit.SharpDX.Model.Scene.SceneNode root)
    {
        if (root is HelixToolkit.SharpDX.Model.Scene.MeshNode mesh) yield return mesh;
        foreach (var child in root.Items)
            foreach (var nested in EnumerateMeshes(child)) yield return nested;
    }

    private static void StageSiblingImages(UnityAssetCatalog catalog, string temporaryRoot, UnityPackageEntry modelEntry, CancellationToken token)
    {
        var sourceDirectory = Path.GetDirectoryName(modelEntry.PathName.Replace('/', Path.DirectorySeparatorChar));
        foreach (var image in catalog.Entries.Where(x => x.Kind == "Image" && string.Equals(Path.GetDirectoryName(x.PathName.Replace('/', Path.DirectorySeparatorChar)), sourceDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            try { catalog.StageAsset(image, temporaryRoot, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
        }
    }
}


