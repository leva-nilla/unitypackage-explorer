using System.Numerics;

namespace UnityPackageExplorer.Core;

public sealed record UnityPackageEntry(
    string Guid,
    string PathName,
    bool HasAsset,
    bool HasMeta)
{
    /// <summary>The outer ZIP member containing this UnityPackage, when browsing a ZIP.</summary>
    public string? PackageMemberPath { get; init; }

    /// <summary>The original asset path inside its UnityPackage.</summary>
    public string? AssetPathName { get; init; }

    /// <summary>The outer ZIP member containing a loose file, when this is not in a UnityPackage.</summary>
    public string? ZipMemberPath { get; init; }

    public string Extension => Path.GetExtension(PathName).ToLowerInvariant();
    public string Kind => Extension switch
    {
        ".prefab" => "Prefab",
        ".fbx" or ".obj" or ".blend" => "Model",
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tga" or ".psd" or ".tif" or ".tiff" => "Image",
        ".mat" => "Material",
        ".cs" => "Script",
        ".unity" => "Scene",
        _ => string.IsNullOrEmpty(Extension) ? "Folder" : Extension.TrimStart('.').ToUpperInvariant()
    };
}

public sealed record UnityAssetReference(long FileId, string? Guid);

public sealed record UnityMaterialInfo(
    string? Name,
    IReadOnlyList<string> TextureGuids,
    IReadOnlyList<string> UnsupportedTextureGuids,
    IReadOnlyList<UnityMaterialTextureReference> SupportedTextures);

public sealed record UnityMaterialTextureReference(string Slot, string Guid);

public sealed class PrefabNode
{
    public required long FileId { get; init; }
    public required string Name { get; set; }
    public long ParentFileId { get; set; }
    public Vector3Value Position { get; set; } = Vector3Value.Zero;
    public QuaternionValue Rotation { get; set; } = QuaternionValue.Identity;
    public Vector3Value Scale { get; set; } = Vector3Value.One;
    public List<string> Components { get; } = [];
    public UnityAssetReference? Mesh { get; set; }
    public List<UnityAssetReference> Materials { get; } = [];
    public UnityAssetReference? SourcePrefab { get; set; }
    public List<PrefabNode> Children { get; } = [];
}

public sealed record PrefabModelPlacement(
    string Guid,
    Matrix4x4 Transform,
    PrefabNode Node,
    IReadOnlyList<UnityAssetReference> Materials,
    IReadOnlyList<long> MeshFileIds)
{
    public IReadOnlyList<PrefabRendererMaterialAssignment> RendererAssignments { get; init; } = [];
}

/// <summary>Material references belonging to one Unity renderer and source Mesh subasset.</summary>
public sealed record PrefabRendererMaterialAssignment(
    long MeshFileId,
    string RendererName,
    IReadOnlyList<UnityAssetReference> Materials);

public readonly record struct Vector3Value(float X, float Y, float Z)
{
    public static Vector3Value Zero => new(0, 0, 0);
    public static Vector3Value One => new(1, 1, 1);
}

public readonly record struct QuaternionValue(float X, float Y, float Z, float W)
{
    public static QuaternionValue Identity => new(0, 0, 0, 1);
}

public sealed record PrefabParseResult(IReadOnlyList<PrefabNode> Roots, IReadOnlyList<string> Warnings);

public sealed record UnityPackageSummary(
    string FileName,
    int AssetCount,
    IReadOnlyDictionary<string, int> KindCounts,
    string? RepresentativeImagePath,
    IReadOnlyList<string> PrefabNames);

public sealed record AssetTreeItem(string Name, string PathName, UnityPackageEntry? Entry, IReadOnlyList<AssetTreeItem> Children)
{
    public bool IsFolder => Entry is null;
}
