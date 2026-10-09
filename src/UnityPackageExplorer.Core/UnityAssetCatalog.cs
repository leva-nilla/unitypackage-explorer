using System.Text;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

public sealed class UnityAssetCatalog
{
    private const long MaximumMetaBytes = 1024 * 1024;
    private static readonly Regex MaterialNamePattern = new("(?m)^m_Name:\\s*(?<name>[^\\r\\n]*)", RegexOptions.Compiled);
    private static readonly Regex MaterialTextureBlockPattern = new("(?ms)^\\s*-\\s*(?<name>_[A-Za-z0-9]+):\\s*(?<body>.*?)(?=^\\s*-\\s*_[A-Za-z0-9]+:|^\\S|\\z)", RegexOptions.Compiled);
    private static readonly Regex MaterialTextureReferencePattern = new("(?m)^\\s*m_Texture:\\s*\\{\\s*fileID:\\s*-?\\d+\\s*,\\s*guid:\\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);
    private static readonly HashSet<string> SupportedMaterialTextureSlots = new(["_MainTex", "_BaseMap", "_BaseColorMap", "_BaseColorTexture"], StringComparer.OrdinalIgnoreCase);
    private readonly UnityPackageReader? _package;
    private readonly Dictionary<string, List<UnityPackageEntry>> _byGuid;
    private readonly Dictionary<string, UnityPackageEntry> _byPath;
    private readonly Dictionary<string, string>? _externalGuidPaths;
    private readonly string? _projectRoot;
    private readonly UnityZipReader? _zipReader;
    private readonly IReadOnlyDictionary<string, UnityPackageReader> _zipPackages;

    private UnityAssetCatalog(
        UnityPackageReader? package,
        IReadOnlyList<UnityPackageEntry> entries,
        Dictionary<string, string>? externalGuidPaths = null,
        string? assetRoot = null,
        UnityZipReader? zipReader = null,
        IReadOnlyDictionary<string, UnityPackageReader>? zipPackages = null,
        IReadOnlyList<string>? warnings = null)
    {
        _package = package;
        Entries = entries;
        _byGuid = entries.GroupBy(x => x.Guid, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        _byPath = entries.GroupBy(x => x.PathName, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        _externalGuidPaths = externalGuidPaths;
        _projectRoot = assetRoot;
        _zipReader = zipReader;
        _zipPackages = zipPackages ?? new Dictionary<string, UnityPackageReader>(StringComparer.OrdinalIgnoreCase);
        Warnings = warnings ?? [];
    }

    public IReadOnlyList<UnityPackageEntry> Entries { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool IsPackage => _package is not null;

    public void RebindZipPath(string path) => (_zipReader ?? throw new InvalidOperationException("This catalog is not reading a ZIP archive.")).RebindPath(path);

    public static UnityAssetCatalog OpenPackage(string path, CancellationToken cancellationToken = default)
    {
        var reader = new UnityPackageReader(path);
        return new UnityAssetCatalog(reader, reader.ReadIndex(cancellationToken));
    }

    public static UnityAssetCatalog OpenZip(string path, CancellationToken cancellationToken = default)
    {
        return OpenZip(new UnityZipReader(path), cancellationToken);
    }

    public static UnityAssetCatalog OpenZip(UnityZipReader zip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var members = zip.ReadIndex(cancellationToken);
        var packageMembers = members.Where(x => x.FullName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)).ToArray();
        var packageReaders = new Dictionary<string, UnityPackageReader>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<UnityPackageEntry>();
        var warnings = new List<string>();
        var nestedPackageBudget = new ArchiveExpansionBudget(UnityPackageReader.MaximumExpandedArchiveBytes);
        foreach (var member in packageMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageMemberPath = member.FullName;
            if (nestedPackageBudget.IsExhausted)
            {
                warnings.Add("ZIP内の残りの unitypackage は合計展開サイズ上限のため読み込みを省略しました。");
                break;
            }
            if (member.Length == 0)
            {
                warnings.Add($"{packageMemberPath}: unitypackage is empty and could not be read.");
                continue;
            }
            var reader = new UnityPackageReader(() => zip.OpenMember(packageMemberPath));
            try
            {
                var packageEntries = reader.ReadIndex(cancellationToken, nestedPackageBudget);
                packageReaders.Add(packageMemberPath, reader);
                entries.AddRange(packageEntries.Select(item => item with
                {
                    PathName = $"{packageMemberPath}/{item.PathName}",
                    PackageMemberPath = packageMemberPath,
                    AssetPathName = item.PathName
                }));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                warnings.Add($"{packageMemberPath}: 読み込めませんでした ({exception.Message})");
                if (nestedPackageBudget.IsExhausted)
                {
                    warnings.Add("ZIP内の残りの unitypackage は合計展開サイズ上限のため読み込みを省略しました。");
                    break;
                }
            }
        }

        var memberNames = members.Select(x => x.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members.Where(x => !x.FullName.EndsWith("/", StringComparison.Ordinal)
            && !x.FullName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            && !x.FullName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metaName = member.FullName + ".meta";
            string? guid = null;
            if (memberNames.Contains(metaName))
            {
                try { guid = UnityPackageReader.ReadGuid(Encoding.UTF8.GetString(zip.ReadMember(metaName, MaximumMetaBytes, cancellationToken))); }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    warnings.Add($"{metaName}: GUID metadata was ignored ({exception.Message})");
                }
            }
            guid ??= StableGuid(member.FullName);
            entries.Add(new UnityPackageEntry(guid, member.FullName, true, memberNames.Contains(metaName))
            {
                ZipMemberPath = member.FullName
            });
        }

        return new UnityAssetCatalog(null, entries, zipReader: zip, zipPackages: packageReaders, warnings: warnings);
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

    public byte[]? ReadAsset(UnityPackageEntry entry, string payload = "asset", CancellationToken cancellationToken = default, long maximumBytes = UnityPackageReader.MaximumAssetBytes)
    {
        if (maximumBytes is <= 0 or > UnityPackageReader.MaximumAssetBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var readLimit = payload == "asset.meta" ? Math.Min(maximumBytes, MaximumMetaBytes) : maximumBytes;
        if (entry.PackageMemberPath is not null && _zipPackages.TryGetValue(entry.PackageMemberPath, out var zipPackage))
            return zipPackage.ReadAsset(entry.Guid, payload, cancellationToken, readLimit);
        if (entry.ZipMemberPath is not null && _zipReader is not null)
        {
            var memberPath = payload == "asset.meta" ? entry.ZipMemberPath + ".meta" : entry.ZipMemberPath;
            try { return _zipReader.ReadMember(memberPath, readLimit, cancellationToken); }
            catch (FileNotFoundException) { return null; }
        }
        if (_package is not null) return _package.ReadAsset(entry.Guid, payload, cancellationToken, readLimit);
        var path = ResolvePath(entry.Guid, entry.PackageMemberPath) ?? entry.PathName;
        if (path is null) return null;
        if (payload == "asset.meta") path += ".meta";
        if (File.Exists(path) && new FileInfo(path).Length > readLimit)
            throw new InvalidDataException($"Asset exceeds the {readLimit / 1024 / 1024} MiB safety limit.");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public byte[]? ReadAssetByGuid(string guid, string payload = "asset", CancellationToken cancellationToken = default, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        var entry = FindByGuid(guid, packageMemberPath, referenceEntry);
        if (entry is not null) return ReadAsset(entry, payload, cancellationToken);
        if (_externalGuidPaths is not null && _externalGuidPaths.TryGetValue(guid, out var path))
            return File.Exists(payload == "asset.meta" ? path + ".meta" : path) ? File.ReadAllBytes(payload == "asset.meta" ? path + ".meta" : path) : null;
        return null;
    }

    public string? ResolvePath(string guid, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        if (_externalGuidPaths is not null && _externalGuidPaths.TryGetValue(guid, out var externalPath)) return externalPath;
        var entry = FindByGuid(guid, packageMemberPath, referenceEntry);
        return entry is null ? null : entry.AssetPathName ?? entry.PathName;
    }

    public UnityPackageEntry? FindByPath(string pathName) => _byPath.GetValueOrDefault(pathName);

    public UnityPackageEntry? FindByGuid(string guid, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        if (!_byGuid.TryGetValue(guid, out var candidates)) return null;
        packageMemberPath ??= referenceEntry?.PackageMemberPath;
        if (packageMemberPath is not null)
        {
            var scoped = candidates.FirstOrDefault(x => string.Equals(x.PackageMemberPath, packageMemberPath, StringComparison.OrdinalIgnoreCase));
            if (scoped is not null) return scoped;
            var outsideCandidates = candidates.Where(x => x.ZipMemberPath is not null || x.PackageMemberPath is not null).ToArray();
            return outsideCandidates.Length == 1 ? outsideCandidates[0] : null;
        }
        if (referenceEntry?.ZipMemberPath is not null)
        {
            var looseCandidates = candidates.Where(x => x.ZipMemberPath is not null).ToArray();
            if (looseCandidates.Length == 1) return looseCandidates[0];
            if (looseCandidates.Length > 1) return null;
            var packageCandidates = candidates.Where(x => x.PackageMemberPath is not null).ToArray();
            return packageCandidates.Length == 1 ? packageCandidates[0] : null;
        }
        return candidates.Count == 1 ? candidates[0] : candidates.FirstOrDefault(x => x.PackageMemberPath is not null) ?? candidates[0];
    }

    public IReadOnlyList<string> ReadMaterialTextureGuids(string materialGuid, CancellationToken cancellationToken = default, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        return ReadMaterialInfo(materialGuid, cancellationToken, packageMemberPath, referenceEntry)?.TextureGuids ?? [];
    }

    public IReadOnlyList<string> ReadUnsupportedMaterialTextureGuids(string materialGuid, CancellationToken cancellationToken = default, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        return ReadMaterialInfo(materialGuid, cancellationToken, packageMemberPath, referenceEntry)?.UnsupportedTextureGuids ?? [];
    }

    public string? ReadMaterialName(string materialGuid, CancellationToken cancellationToken = default, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        return ReadMaterialInfo(materialGuid, cancellationToken, packageMemberPath, referenceEntry)?.Name;
    }

    public UnityMaterialInfo? ReadMaterialInfo(string materialGuid, CancellationToken cancellationToken = default, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        var bytes = ReadAssetByGuid(materialGuid, cancellationToken: cancellationToken, packageMemberPath: packageMemberPath, referenceEntry: referenceEntry);
        if (bytes is null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var text = Encoding.UTF8.GetString(bytes);
        var nameMatch = MaterialNamePattern.Match(text);
        var name = nameMatch.Success ? nameMatch.Groups["name"].Value.Trim().Trim('"', '\'') : null;
        if (string.IsNullOrWhiteSpace(name)) name = null;

        var supported = MaterialTextureBlockPattern.Matches(text)
            .Cast<Match>()
            .Where(block => SupportedMaterialTextureSlots.Contains(block.Groups["name"].Value))
            .SelectMany(block => MaterialTextureReferencePattern.Matches(block.Groups["body"].Value).Cast<Match>())
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var supportedSet = supported.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unsupported = MaterialTextureReferencePattern.Matches(text)
            .Cast<Match>()
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .Where(guid => !supportedSet.Contains(guid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new UnityMaterialInfo(name, supported, unsupported);
    }

    public string StageAsset(string guid, string temporaryRoot, string? fallbackName = null, CancellationToken cancellationToken = default, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        var entry = FindByGuid(guid, packageMemberPath, referenceEntry);
        var sourcePath = ResolvePath(guid, packageMemberPath, referenceEntry) ?? entry?.PathName;
        if (sourcePath is null) throw new FileNotFoundException($"Unity asset GUID {guid} was not found.");
        var bytes = (entry is null ? ReadAssetByGuid(guid, cancellationToken: cancellationToken, packageMemberPath: packageMemberPath, referenceEntry: referenceEntry) : ReadAsset(entry, cancellationToken: cancellationToken))
            ?? throw new FileNotFoundException($"Unity asset bytes for {sourcePath} were not found.");
        var relative = _package is not null || entry?.PackageMemberPath is not null || entry?.ZipMemberPath is not null
            ? UnityPackageReader.GetSafeRelativePath(sourcePath)
            : Path.GetRelativePath(_projectRoot!, sourcePath);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            relative = fallbackName ?? $"{guid}.fbx";

        var target = Path.GetFullPath(Path.Combine(temporaryRoot, relative));
        var prefix = Path.GetFullPath(temporaryRoot) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Staged asset path escaped the temporary directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        var meta = entry is null
            ? ReadAssetByGuid(guid, "asset.meta", cancellationToken, packageMemberPath, referenceEntry)
            : ReadAsset(entry, "asset.meta", cancellationToken);
        if (meta is not null) File.WriteAllBytes(target + ".meta", meta);
        return target;
    }

    public string StageAsset(UnityPackageEntry entry, string temporaryRoot, CancellationToken cancellationToken = default)
    {
        var sourcePath = entry.AssetPathName ?? entry.PathName;
        var bytes = ReadAsset(entry, cancellationToken: cancellationToken)
            ?? throw new FileNotFoundException($"Unity asset bytes for {sourcePath} were not found.");
        var relative = _package is not null || entry.PackageMemberPath is not null || entry.ZipMemberPath is not null
            ? UnityPackageReader.GetSafeRelativePath(sourcePath)
            : Path.GetRelativePath(_projectRoot!, sourcePath);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            relative = $"{entry.Guid}{entry.Extension}";

        var target = Path.GetFullPath(Path.Combine(temporaryRoot, relative));
        var prefix = Path.GetFullPath(temporaryRoot) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Staged asset path escaped the temporary directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        var meta = ReadAsset(entry, "asset.meta", cancellationToken);
        if (meta is not null) File.WriteAllBytes(target + ".meta", meta);
        return target;
    }

    public string? StageImage(string guid, string temporaryRoot, CancellationToken cancellationToken = default, string? packageMemberPath = null, UnityPackageEntry? referenceEntry = null)
    {
        var entry = FindByGuid(guid, packageMemberPath, referenceEntry);
        var path = entry?.AssetPathName ?? entry?.PathName ?? ResolvePath(guid, packageMemberPath, referenceEntry);
        if (path is null) return null;
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        var bytes = entry is null
            ? ReadAssetByGuid(guid, cancellationToken: cancellationToken, packageMemberPath: packageMemberPath, referenceEntry: referenceEntry)
            : ReadAsset(entry, cancellationToken: cancellationToken);
        if (bytes is null) return null;
        var target = Path.Combine(temporaryRoot, $"textures", $"{guid}{extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        return target;
    }

    public static IReadOnlyList<AssetTreeItem> BuildTree(IEnumerable<UnityPackageEntry> entries, Func<UnityPackageEntry, bool>? predicate = null, CancellationToken cancellationToken = default)
    {
        var root = new MutableTreeItem(string.Empty, string.Empty);
        foreach (var entry in entries.Where(x => predicate?.Invoke(x) ?? true))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        return root.Children.Values.Select(x => x.ToImmutable(cancellationToken)).ToArray();
    }

    public static IEnumerable<AssetTreeItem> FilterTree(IEnumerable<AssetTreeItem> nodes, string search, string kind, CancellationToken cancellationToken = default)
    {
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var children = FilterTree(node.Children, search, kind, cancellationToken).ToArray();
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

    private static string StableGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private sealed class MutableTreeItem(string name, string pathName)
    {
        public string Name { get; } = name;
        public string PathName { get; } = pathName;
        public UnityPackageEntry? Entry { get; set; }
        public SortedDictionary<string, MutableTreeItem> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public AssetTreeItem ToImmutable(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AssetTreeItem(Name, PathName, Entry, Children.Values.Select(x => x.ToImmutable(cancellationToken)).ToArray());
        }
    }
}
