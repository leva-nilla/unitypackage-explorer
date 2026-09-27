using System.Text;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

public sealed class UnityAssetCatalog
{
    private readonly UnityPackageReader? _package;
    private readonly Dictionary<string, UnityPackageEntry> _byGuid;
    private readonly Dictionary<string, UnityPackageEntry> _byPath;
    private readonly Dictionary<string, string>? _externalGuidPaths;
    private readonly string? _projectRoot;

    private UnityAssetCatalog(UnityPackageReader? package, IReadOnlyList<UnityPackageEntry> entries, Dictionary<string, string>? externalGuidPaths = null, string? assetRoot = null)
    {
        _package = package;
        Entries = entries;
        _byGuid = entries.GroupBy(x => x.Guid, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        _byPath = entries.GroupBy(x => x.PathName, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        _externalGuidPaths = externalGuidPaths;
        _projectRoot = assetRoot;
    }

    public IReadOnlyList<UnityPackageEntry> Entries { get; }
    public bool IsPackage => _package is not null;

    public static UnityAssetCatalog OpenPackage(string path, CancellationToken cancellationToken = default)
    {
        var reader = new UnityPackageReader(path);
        return new UnityAssetCatalog(reader, reader.ReadIndex(cancellationToken));
    }

    public static UnityAssetCatalog OpenProjectPrefab(string prefabPath, CancellationToken cancellationToken = default)
    {
        var fullPrefabPath = Path.GetFullPath(prefabPath);
        var contentRoot = FindUnityContentRoot(Path.GetDirectoryName(fullPrefabPath)!);
        var projectRoot = contentRoot is null ? Path.GetDirectoryName(fullPrefabPath)! : Directory.GetParent(contentRoot)!.FullName;
        var contentDirectories = contentRoot is null
            ? new[] { projectRoot }
            : new[] { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages"), Path.Combine(projectRoot, "Library", "PackageCache") }
                .Where(Directory.Exists).ToArray();
        var guidPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<UnityPackageEntry>();
        foreach (var directory in contentDirectories)
        {
            foreach (var metaPath in Directory.EnumerateFiles(directory, "*.meta", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var guid = UnityPackageReader.ReadGuid(File.ReadAllText(metaPath));
                    if (guid is null) continue;
                    var assetPath = metaPath[..^5];
                    var relative = Path.GetRelativePath(projectRoot, assetPath).Replace('\\', '/');
                    guidPaths[guid] = assetPath;
                    entries.Add(new UnityPackageEntry(guid, relative, File.Exists(assetPath), true));
                }
                catch (IOException) { }
            }
        }

        return new UnityAssetCatalog(null, entries, guidPaths, projectRoot);
    }

    public byte[]? ReadAsset(UnityPackageEntry entry, string payload = "asset", CancellationToken cancellationToken = default)
    {
        if (_package is not null) return _package.ReadAsset(entry.Guid, payload, cancellationToken);
        var path = ResolvePath(entry.Guid);
        if (path is null) return null;
        if (payload == "asset.meta") path += ".meta";
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public byte[]? ReadAssetByGuid(string guid, string payload = "asset", CancellationToken cancellationToken = default)
    {
        return _byGuid.TryGetValue(guid, out var entry) ? ReadAsset(entry, payload, cancellationToken) :
            _externalGuidPaths is not null && _externalGuidPaths.TryGetValue(guid, out var path)
                ? File.Exists(payload == "asset.meta" ? path + ".meta" : path) ? File.ReadAllBytes(payload == "asset.meta" ? path + ".meta" : path) : null
                : null;
    }

    public string? ResolvePath(string guid)
    {
        if (_externalGuidPaths is not null && _externalGuidPaths.TryGetValue(guid, out var externalPath)) return externalPath;
        return _byGuid.TryGetValue(guid, out var entry) ? entry.PathName : null;
    }

    public UnityPackageEntry? FindByPath(string pathName) => _byPath.GetValueOrDefault(pathName);

    public IReadOnlyList<string> ReadMaterialTextureGuids(string materialGuid, CancellationToken cancellationToken = default)
    {
        var bytes = ReadAssetByGuid(materialGuid, cancellationToken: cancellationToken);
        if (bytes is null) return [];
        var text = Encoding.UTF8.GetString(bytes);
        var blocks = Regex.Matches(text, "(?ms)^\\s*-\\s*(?<name>_[A-Za-z0-9]+):\\s*(?<body>.*?)(?=^\\s*-\\s*_[A-Za-z0-9]+:|^\\S|\\z)");
        var acceptedNames = new HashSet<string>(["_MainTex", "_BaseMap", "_BaseColorMap", "_BaseColorTexture"], StringComparer.OrdinalIgnoreCase);
        return blocks.Cast<Match>()
            .Where(x => acceptedNames.Contains(x.Groups["name"].Value))
            .SelectMany(x => Regex.Matches(x.Groups["body"].Value, "(?m)^\\s*m_Texture:\\s*\\{\\s*fileID:\\s*-?\\d+\\s*,\\s*guid:\\s*([0-9a-fA-F]{32})").Cast<Match>())
            .Select(x => x.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> ReadUnsupportedMaterialTextureGuids(string materialGuid, CancellationToken cancellationToken = default)
    {
        var bytes = ReadAssetByGuid(materialGuid, cancellationToken: cancellationToken);
        if (bytes is null) return [];
        var text = Encoding.UTF8.GetString(bytes);
        var supported = ReadMaterialTextureGuids(materialGuid, cancellationToken).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Regex.Matches(text, "(?m)^\\s*m_Texture:\\s*\\{\\s*fileID:\\s*-?\\d+\\s*,\\s*guid:\\s*([0-9a-fA-F]{32})")
            .Cast<Match>()
            .Select(x => x.Groups[1].Value.ToLowerInvariant())
            .Where(guid => !supported.Contains(guid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string? ReadMaterialName(string materialGuid, CancellationToken cancellationToken = default)
    {
        var bytes = ReadAssetByGuid(materialGuid, cancellationToken: cancellationToken);
        if (bytes is null) return null;
        var match = Regex.Match(Encoding.UTF8.GetString(bytes), "(?m)^m_Name:\\s*(?<name>[^\\r\\n]*)");
        if (!match.Success) return null;
        var name = match.Groups["name"].Value.Trim().Trim('"', '\'');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public string StageAsset(string guid, string temporaryRoot, string? fallbackName = null, CancellationToken cancellationToken = default)
    {
        var sourcePath = ResolvePath(guid);
        if (sourcePath is null) throw new FileNotFoundException($"Unity asset GUID {guid} was not found.");
        var bytes = ReadAssetByGuid(guid, cancellationToken: cancellationToken)
            ?? throw new FileNotFoundException($"Unity asset bytes for {sourcePath} were not found.");
        var relative = _package is not null
            ? UnityPackageReader.GetSafeRelativePath(sourcePath)
            : Path.GetRelativePath(_projectRoot!, sourcePath);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            relative = fallbackName ?? $"{guid}.fbx";

        var target = Path.GetFullPath(Path.Combine(temporaryRoot, relative));
        var prefix = Path.GetFullPath(temporaryRoot) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Staged asset path escaped the temporary directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        var meta = ReadAssetByGuid(guid, "asset.meta", cancellationToken);
        if (meta is not null) File.WriteAllBytes(target + ".meta", meta);
        return target;
    }

    public string? StageImage(string guid, string temporaryRoot, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(guid);
        if (path is null) return null;
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        var bytes = ReadAssetByGuid(guid, cancellationToken: cancellationToken);
        if (bytes is null) return null;
        var target = Path.Combine(temporaryRoot, $"textures", $"{guid}{extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        return target;
    }

    public static IReadOnlyList<AssetTreeItem> BuildTree(IEnumerable<UnityPackageEntry> entries, Func<UnityPackageEntry, bool>? predicate = null)
    {
        var root = new MutableTreeItem(string.Empty, string.Empty);
        foreach (var entry in entries.Where(x => predicate?.Invoke(x) ?? true))
        {
            var parts = entry.PathName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var path = new StringBuilder();
            for (var i = 0; i < parts.Length; i++)
            {
                if (path.Length > 0) path.Append('/');
                path.Append(parts[i]);
                if (!current.Children.TryGetValue(parts[i], out var next))
                {
                    next = new MutableTreeItem(parts[i], path.ToString());
                    current.Children.Add(parts[i], next);
                }
                current = next;
                if (i == parts.Length - 1) current.Entry = entry;
            }
        }
        return root.Children.Values.Select(x => x.ToImmutable()).ToArray();
    }

    public static IEnumerable<AssetTreeItem> FilterTree(IEnumerable<AssetTreeItem> nodes, string search, string kind)
    {
        foreach (var node in nodes)
        {
            var children = FilterTree(node.Children, search, kind).ToArray();
            var selfMatches = node.Entry is not null
                && (string.IsNullOrWhiteSpace(search) || node.PathName.Contains(search, StringComparison.OrdinalIgnoreCase))
                && (kind == "すべて" || node.Entry.Kind == kind);
            if (selfMatches || children.Length > 0)
                yield return node with { Children = children };
        }
    }

    private static string? FindUnityContentRoot(string start)
    {
        for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            if (string.Equals(current.Name, "Assets", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.Name, "Packages", StringComparison.OrdinalIgnoreCase)) return current.FullName;
        return null;
    }

    private sealed class MutableTreeItem(string name, string pathName)
    {
        public string Name { get; } = name;
        public string PathName { get; } = pathName;
        public UnityPackageEntry? Entry { get; set; }
        public SortedDictionary<string, MutableTreeItem> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public AssetTreeItem ToImmutable() => new(Name, PathName, Entry, Children.Values.Select(x => x.ToImmutable()).ToArray());
    }
}
