using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class CatalogIndexCacheTests
{
    private static string ShowcaseZip => Path.Combine(AppContext.BaseDirectory, "TestData", "UnityPackageExplorer-Showcase.zip");

    [Fact]
    public void OpenZipReusesCachedIndexForIdenticalArchive()
    {
        var cacheDir = CreateTemporaryDirectory();
        try
        {
            var first = UnityAssetCatalog.OpenZip(ShowcaseZip, default, new CatalogIndexCache(ShowcaseZip, cacheDir));
            var second = UnityAssetCatalog.OpenZip(ShowcaseZip, default, new CatalogIndexCache(ShowcaseZip, cacheDir));

            Assert.Equal(first.Entries, second.Entries);
            // Dynamic warnings (e.g. budget exhaustion) are deliberately not cached.
            Assert.Empty(second.Warnings);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void TryLoadRejectsMismatchedArchiveMembers()
    {
        var cacheDir = CreateTemporaryDirectory();
        try
        {
            var cache = new CatalogIndexCache(ShowcaseZip, cacheDir);
            var members = new[] { new UnityArchiveMember("sample.unitypackage", 1024, 1024) };
            cache.Store(new CatalogCachePayload(
                CatalogIndexCache.Version,
                members.Select(x => new CatalogCacheMember(x.FullName, x.Length)).ToArray(),
                [],
                [new CatalogCacheEntry("guid", "sample.unitypackage/asset", true, false)]));

            Assert.NotNull(cache.TryLoad(members));
            var changed = members.Select(x => new UnityArchiveMember(x.FullName, x.Length + 1, x.CompressedLength)).ToArray();
            Assert.Null(cache.TryLoad(changed));
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public void CachedKeyFollowsFileContent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var file = Path.Combine(root, "package.unitypackage");
            File.WriteAllText(file, "hello world");
            var cache = new CatalogIndexCache(file, root);
            cache.Store(new CatalogCachePayload(
                CatalogIndexCache.Version, [], [], [new CatalogCacheEntry("guid", "asset", true, false)]));
            Assert.NotNull(cache.TryLoadPackage());

            File.AppendAllText(file, "!");
            Assert.Null(new CatalogIndexCache(file, root).TryLoadPackage());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenPackageReusesCachedIndex()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = Path.Combine(root, "showcase.unitypackage");
            using (var archive = ZipFile.OpenRead(ShowcaseZip))
            {
                var entry = Assert.Single(archive.Entries, item => item.FullName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase));
                using var input = entry.Open();
                using var target = File.Create(package);
                input.CopyTo(target);
            }

            var first = UnityAssetCatalog.OpenPackage(package, default, new CatalogIndexCache(package, root));
            var second = UnityAssetCatalog.OpenPackage(package, default, new CatalogIndexCache(package, root));

            Assert.Equal(first.Entries, second.Entries);
            Assert.Empty(second.Warnings);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedPackageUsesConfiguredSpillDirectoryWithOrWithoutIndexCache(bool cached)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string memberName = "sample.unitypackage";
            var zipPath = Path.Combine(root, "sample.zip");
            var unavailableSpillDirectory = Path.Combine(root, "does-not-exist");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (var member = zip.CreateEntry(memberName).Open())
            using (var gzip = new GZipStream(member, CompressionLevel.Fastest))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{guid}/pathname")
                    { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("Assets/Item.prefab")) });
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{guid}/asset")
                    { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("payload")) });
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "padding")
                    { DataStream = new MemoryStream(new byte[65 * 1024 * 1024]) });
            }

            var cache = new CatalogIndexCache(zipPath, Path.Combine(root, "index"));
            if (cached)
            {
                var members = new UnityZipReader(zipPath).ReadIndex();
                cache.Store(new CatalogCachePayload(CatalogIndexCache.Version,
                    members.Select(item => new CatalogCacheMember(item.FullName, item.Length)).ToArray(),
                    [new CatalogCachePackage(memberName, [new CatalogCacheEntry(guid, "Assets/Item.prefab", true, false)])],
                    []));
            }

            var catalog = UnityAssetCatalog.OpenZip(zipPath, default, cached ? cache : null, unavailableSpillDirectory);
            if (cached)
            {
                var entry = Assert.Single(catalog.Entries);
                Assert.Equal("payload", Encoding.UTF8.GetString(catalog.ReadAsset(entry)!));
                Assert.Throws<DirectoryNotFoundException>(() => catalog.ReadAsset(entry));
            }
            else
            {
                Assert.Contains(catalog.Warnings, warning => warning.Contains(unavailableSpillDirectory, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Environment.CurrentDirectory, ".artifacts", $"cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
