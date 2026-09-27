using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityPackageReaderTests
{
    [Fact]
    public void ReadsIndexAndRequestedAssetFromGzipTar()
    {
        using var package = TemporaryPackage.Create([
            ("a".PadLeft(32, '0') + "/pathname", "Assets/Props/Item.prefab"),
            ("a".PadLeft(32, '0') + "/asset", "prefab payload"),
            ("a".PadLeft(32, '0') + "/asset.meta", "guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        ]);

        var reader = new UnityPackageReader(package.Path);
        var index = reader.ReadIndex();

        var item = Assert.Single(index);
        Assert.Equal("Assets/Props/Item.prefab", item.PathName);
        Assert.True(item.HasAsset);
        Assert.True(item.HasMeta);
        Assert.Equal("prefab payload", Encoding.UTF8.GetString(reader.ReadAsset(item.Guid)!));
    }

    [Fact]
    public void ReadsPackageThroughAReopenedStreamFactory()
    {
        var guid = "a".PadLeft(32, '0');
        using var package = TemporaryPackage.Create([
            ($"{guid}/pathname", "Assets/Props/Item.prefab"),
            ($"{guid}/asset", "prefab payload")
        ]);
        var reader = new UnityPackageReader(() => new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.Read));

        var item = Assert.Single(reader.ReadIndex());

        Assert.Equal("prefab payload", Encoding.UTF8.GetString(reader.ReadAsset(item.Guid)!));
    }

    [Theory]
    [InlineData("Assets/Props/Item.prefab", "Assets\\Props\\Item.prefab")]
    [InlineData("Packages/com.example/Textures/a.png", "Packages\\com.example\\Textures\\a.png")]
    public void SafePathNormalizesUnitySeparators(string input, string expected) =>
        Assert.Equal(expected, UnityPackageReader.GetSafeRelativePath(input));

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("Assets/../../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("Assets/CON/file.txt")]
    public void SafePathRejectsTraversalAndReservedNames(string input) =>
        Assert.Throws<InvalidDataException>(() => UnityPackageReader.GetSafeRelativePath(input));

    [Fact]
    public void ReadsGuidFromMeta()
    {
        Assert.Equal("0123456789abcdef0123456789abcdef", UnityPackageReader.ReadGuid("fileFormatVersion: 2\nguid: 0123456789ABCDEF0123456789ABCDEF\n"));
        Assert.Null(UnityPackageReader.ReadGuid("fileFormatVersion: 2\n"));
    }

    [Fact]
    public void ExtractionRejectsTraversalWithoutWritingOutsideDestination()
    {
        var id = Guid.NewGuid().ToString("N");
        var guid = "b".PadLeft(32, '0');
        using var package = TemporaryPackage.Create([
            ($"{guid}/pathname", $"../unitypackage-outside-{id}.txt"),
            ($"{guid}/asset", "should not be written")
        ]);
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-extract-test-{id}");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, $"unitypackage-outside-{id}.txt");
        File.WriteAllText(outside, "preserve");
        try
        {
            Assert.Throws<InvalidDataException>(() => new UnityPackageReader(package.Path).ExtractTo(root));
            Assert.Equal("preserve", File.ReadAllText(outside));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public void ExtractionChoosesUnusedDestinationAndPreservesExistingFiles()
    {
        var id = Guid.NewGuid().ToString("N");
        var guid = "c".PadLeft(32, '0');
        using var package = TemporaryPackage.Create([
            ($"{guid}/pathname", "Assets/Props/Item.prefab"),
            ($"{guid}/asset", "new asset")
        ]);
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-extract-test-{id}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "keep.txt"), "preserve");
        try
        {
            var extracted = new UnityPackageReader(package.Path).ExtractTo(root);
            Assert.Equal(root + "-2", extracted);
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(root, "keep.txt")));
            Assert.Equal("new asset", File.ReadAllText(Path.Combine(extracted, "Assets", "Props", "Item.prefab")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(root + "-2")) Directory.Delete(root + "-2", recursive: true);
        }
    }

    [Fact]
    public void CorruptArchiveDoesNotCreateOutputOrChangeNeighborFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-corrupt-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "broken.unitypackage");
        var output = Path.Combine(root, "broken_extracted");
        var sentinel = Path.Combine(root, "keep.txt");
        var bytes = new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0x01, 0x02, 0x03 };
        File.WriteAllBytes(package, bytes);
        File.WriteAllText(sentinel, "preserve");
        try
        {
            Assert.ThrowsAny<Exception>(() => new UnityPackageReader(package).ExtractTo(output));
            Assert.False(Directory.Exists(output));
            Assert.Equal(bytes, File.ReadAllBytes(package));
            Assert.Equal("preserve", File.ReadAllText(sentinel));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TemporaryPackage : IDisposable
    {
        private TemporaryPackage(string path) => Path = path;
        public string Path { get; }

        public static TemporaryPackage Create(IEnumerable<(string Name, string Content)> entries)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"unitypackage-test-{Guid.NewGuid():N}.unitypackage");
            using var file = File.Create(path);
            using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: false);
            using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
            foreach (var (name, content) in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(bytes, writable: false)
                });
            }
            return new TemporaryPackage(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
