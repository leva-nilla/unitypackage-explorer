namespace UnityPackageExplorer.Core;

public sealed record UnityPackageEntry(
    string Guid,
    string PathName,
    bool HasAsset,
    bool HasMeta)
{
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
