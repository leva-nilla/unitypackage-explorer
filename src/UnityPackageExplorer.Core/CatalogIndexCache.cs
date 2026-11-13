using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityPackageExplorer.Core;

public sealed record CatalogCacheMember(string Name, long Length);

public sealed record CatalogCacheEntry(string Guid, string PathName, bool HasAsset, bool HasMeta, string? AssetPathName = null);

public sealed record CatalogCachePackage(string MemberPath, IReadOnlyList<CatalogCacheEntry> Entries);

public sealed record CatalogCachePayload(
    int Version,
    IReadOnlyList<CatalogCacheMember> Members,
    IReadOnlyList<CatalogCachePackage> Packages,
    IReadOnlyList<CatalogCacheEntry> LooseMembers);

/// <summary>
/// Persistent cache of archive index entries, keyed by file name + size + SHA-256 of the
/// first and last 64 KiB. A hit skips nested unitypackage index scans and meta GUID reads.
/// Any cache failure degrades to the normal (uncached) flow.
/// </summary>
public sealed class CatalogIndexCache
{
    public const int Version = 1;
    private const int ProbeBytes = 64 * 1024;
    private const int MaximumFiles = 128;

    private readonly string _key;
    private readonly string _directory;

    /// <summary>Creates a cache handle using an explicit directory (used by tests).</summary>
    public CatalogIndexCache(string archivePath, string cacheDirectory)
    {
        _directory = cacheDirectory;
        _key = ComputeKey(archivePath);
    }

    /// <summary>Returns a cache handle for the file, or null when the key cannot be computed.</summary>
    public static CatalogIndexCache? ForArchive(string archivePath)
    {
        try
        {
            var directory = ResolveCacheDirectory();
            if (directory is null) return null;
            return new CatalogIndexCache(archivePath, directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Loads cached index data when the archived members match the cached snapshot.</summary>
    public CatalogCachePayload? TryLoad(IReadOnlyList<UnityArchiveMember> members)
    {
        var payload = TryLoadPackage();
        if (payload is null) return null;
        if (payload.Members.Count != members.Count) return null;
        for (var i = 0; i < members.Count; i++)
        {
            var cached = payload.Members[i];
            var live = members[i];
            if (!string.Equals(cached.Name, live.FullName, StringComparison.Ordinal) || cached.Length != live.Length)
                return null;
        }
        return payload;
    }

    /// <summary>Loads cached index data without member verification (the cache key already derives from file content).</summary>
    public CatalogCachePayload? TryLoadPackage()
    {
        if (_key.Length == 0) return null;
        try
        {
            var path = Path.Combine(_directory, _key + ".json");
            if (!File.Exists(path)) return null;
            var payload = System.Text.Json.JsonSerializer.Deserialize<CatalogCachePayload>(File.ReadAllBytes(path));
            return payload is not null && payload.Version == Version ? payload : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Stores the payload and evicts the oldest cache files beyond the cap. Failures are ignored.</summary>
    public void Store(CatalogCachePayload payload)
    {
        if (_key.Length == 0) return;
        try
        {
            Directory.CreateDirectory(_directory);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                payload with { Version = Version });
            File.WriteAllBytes(Path.Combine(_directory, _key + ".json"), bytes);
            Evict();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Evict()
    {
        var files = new DirectoryInfo(_directory).GetFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        foreach (var file in files.Skip(MaximumFiles))
        {
            try { file.Delete(); }
            catch (IOException) { }
        }
    }

    private string ComputeKey(string archivePath)
    {
        try
        {
            var name = Path.GetFileName(archivePath);
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var length = stream.Length;
            using var hashed = new MemoryStream();
            hashed.Write(Encoding.UTF8.GetBytes($"{name}|{length}|"));
            foreach (var probe in ReadProbe(stream, 0, ProbeBytes)) hashed.Write(probe);
            foreach (var probe in ReadProbe(stream, Math.Max(0, length - ProbeBytes), ProbeBytes)) hashed.Write(probe);
            return Convert.ToHexString(SHA256.HashData(hashed.ToArray())).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<byte[]> ReadProbe(FileStream stream, long offset, int maximumBytes)
    {
        if (stream.Length == 0) yield break;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(maximumBytes, (int)Math.Min(stream.Length, int.MaxValue))];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read > 0) yield return buffer[..read];
    }

    private static string? ResolveCacheDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(profile, "AppData", "LocalLow", "UnityPackageExplorer", "catalog-index-cache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityPackageExplorer", "catalog-index-cache"),
        };
        foreach (var directory in candidates)
        {
            try
            {
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}
