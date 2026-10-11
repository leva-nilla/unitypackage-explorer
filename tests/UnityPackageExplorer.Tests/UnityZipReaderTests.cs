using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityZipReaderTests
{
    [Fact]
    public void OpensNestedUnityPackageAndLooseAssetsWithoutExtractingThem()
    {
        const string packageGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string looseGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-zip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "bundle.zip");
        try
        {
            using (var package = CreateUnityPackage((packageGuid + "/pathname", "Assets/Avatar/Avatar.prefab"), (packageGuid + "/asset", "prefab bytes")))
            using (var secondPackage = CreateUnityPackage((packageGuid + "/pathname", "Assets/Alt/Avatar.prefab"), (packageGuid + "/asset", "second prefab bytes")))
            using (var file = File.Create(zipPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var packageOutput = archive.CreateEntry("Packages/Avatar.unitypackage").Open()) package.CopyTo(packageOutput);
                using (var secondPackageOutput = archive.CreateEntry("Packages/Alt.unitypackage").Open()) secondPackage.CopyTo(secondPackageOutput);
                using (var looseOutput = archive.CreateEntry("Extras/Readme.txt").Open())
                    looseOutput.Write(Encoding.UTF8.GetBytes("loose bytes"));
                archive.CreateEntry("Extras/Empty.txt");
                using (var metaOutput = archive.CreateEntry("Extras/Readme.txt.meta").Open())
                    metaOutput.Write(Encoding.UTF8.GetBytes($"fileFormatVersion: 2\nguid: {looseGuid}\n"));
            }

            var members = new UnityZipReader(zipPath).ReadIndex();
            var catalog = UnityAssetCatalog.OpenZip(zipPath);
            var packageEntries = catalog.Entries.Where(x => x.Extension == ".prefab").ToArray();
            var packageEntry = Assert.Single(packageEntries, x => x.PackageMemberPath == "Packages/Avatar.unitypackage");
            var secondPackageEntry = Assert.Single(packageEntries, x => x.PackageMemberPath == "Packages/Alt.unitypackage");
            var looseEntry = Assert.Single(catalog.Entries, x => x.PathName == "Extras/Readme.txt");
            var emptyEntry = Assert.Single(catalog.Entries, x => x.PathName == "Extras/Empty.txt");

            Assert.Contains(members, x => x.FullName == "Packages/Avatar.unitypackage");
            Assert.Equal("prefab bytes", Encoding.UTF8.GetString(catalog.ReadAsset(packageEntry)!));
            Assert.Equal("second prefab bytes", Encoding.UTF8.GetString(catalog.ReadAsset(secondPackageEntry)!));
            Assert.Equal("Assets/Alt/Avatar.prefab", catalog.ResolvePath(packageGuid, "Packages/Alt.unitypackage"));
            Assert.Equal("loose bytes", Encoding.UTF8.GetString(catalog.ReadAssetByGuid(looseGuid)!));
            Assert.Equal("Assets/Avatar/Avatar.prefab", catalog.ResolvePath(packageGuid, "Packages/Avatar.unitypackage"));
            Assert.Equal("Extras/Readme.txt", looseEntry.PathName);
            Assert.Empty(catalog.ReadAsset(emptyEntry)!);
            Assert.DoesNotContain(Directory.EnumerateDirectories(root), x => Path.GetFileName(x) == "Packages");
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsStreamBackedZipMembersWithoutCopyingTheArchiveToDisk()
    {
        byte[] zipBytes;
        using (var file = new MemoryStream())
        {
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            using (var output = archive.CreateEntry("readme.txt").Open())
                output.Write(Encoding.UTF8.GetBytes("zip stream content"));
            zipBytes = file.ToArray();
        }
        var reader = new UnityZipReader(() => new MemoryStream(zipBytes, writable: false));

        Assert.Contains(reader.ReadIndex(), x => x.FullName == "readme.txt");
        Assert.Equal("zip stream content", Encoding.UTF8.GetString(reader.ReadMember("readme.txt")));
    }

    [Fact]
    public void OpensNestedUnityPackageLargerThanTheBufferedReadLimitWithoutExtractingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-large-zip-member-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "bundle.zip");
        const string packageGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const long assetLength = UnityZipReader.MaximumMemberBytes + 1;
        try
        {
            using (var file = File.Create(zipPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var member = archive.CreateEntry("Packages/large.unitypackage", CompressionLevel.Optimal).Open())
                using (var gzip = new GZipStream(member, CompressionLevel.NoCompression, leaveOpen: true))
                using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{packageGuid}/pathname")
                    {
                        DataStream = new MemoryStream(Encoding.UTF8.GetBytes("Assets/Materials/Large.mat"), writable: false)
                    });
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{packageGuid}/asset")
                    {
                        DataStream = new ZeroFilledReadStream(assetLength)
                    });
                }

                using var looseAsset = archive.CreateEntry("Extras/Readme.txt").Open();
                looseAsset.Write(Encoding.UTF8.GetBytes("still readable"));
            }

            var zipReader = new UnityZipReader(zipPath);
            var indexedMember = Assert.Single(zipReader.ReadIndex(), x => x.FullName == "Packages/large.unitypackage");
            Assert.True(indexedMember.Length > UnityZipReader.MaximumMemberBytes);

            var catalog = UnityAssetCatalog.OpenZip(zipReader);
            var nestedAsset = Assert.Single(catalog.Entries, x => x.PackageMemberPath == "Packages/large.unitypackage");
            var looseAssetEntry = Assert.Single(catalog.Entries, x => x.ZipMemberPath == "Extras/Readme.txt");

            Assert.Equal("Assets/Materials/Large.mat", nestedAsset.AssetPathName);
            Assert.True(nestedAsset.HasAsset);
            Assert.Equal("still readable", Encoding.UTF8.GetString(catalog.ReadAsset(looseAssetEntry)!));
            var bufferedReadError = Assert.Throws<InvalidDataException>(() => zipReader.ReadMember(indexedMember.FullName));
            Assert.Contains("read limit", bufferedReadError.Message);
            Assert.Empty(catalog.Warnings);
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeepsLooseZipFilesAvailableWhenAnInnerUnityPackageIsCorrupt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-bad-zip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "bundle.zip");
        try
        {
            using (var file = File.Create(zipPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var package = archive.CreateEntry("broken.unitypackage").Open()) package.Write([1, 2, 3, 4]);
                using (var loose = archive.CreateEntry("Extras/keep.txt").Open()) loose.Write(Encoding.UTF8.GetBytes("still readable"));
            }

            var catalog = UnityAssetCatalog.OpenZip(zipPath);

            Assert.Contains(catalog.Warnings, x => x.Contains("broken.unitypackage", StringComparison.Ordinal));
            var entry = Assert.Single(catalog.Entries, x => x.PathName == "Extras/keep.txt");
            Assert.Equal("still readable", Encoding.UTF8.GetString(catalog.ReadAsset(entry)!));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReportsEmptyNestedUnityPackagesInsteadOfSilentlyIgnoringThem()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-empty-zip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "bundle.zip");
        try
        {
            using (var file = File.Create(zipPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                archive.CreateEntry("Packages/empty.unitypackage");

            var catalog = UnityAssetCatalog.OpenZip(zipPath);

            Assert.Contains(catalog.Warnings, x => x.Contains("Packages/empty.unitypackage", StringComparison.Ordinal)
                && x.Contains("empty", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolvesDuplicateLooseGuidFromTheReferencingLooseZipAsset()
    {
        const string duplicateGuid = "cccccccccccccccccccccccccccccccc";
        const string prefabGuid = "dddddddddddddddddddddddddddddddd";
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-duplicate-guid-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "bundle.zip");
        var stagedRoot = Path.Combine(root, "staged");
        try
        {
            using var innerPackage = CreateUnityPackage(
                (duplicateGuid + "/pathname", "Assets/Inner/Model.fbx"),
                (duplicateGuid + "/asset", "inner FBX bytes"));
            using (var file = File.Create(zipPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var packageOutput = archive.CreateEntry("Nested/inner.unitypackage").Open()) innerPackage.CopyTo(packageOutput);
                using (var model = archive.CreateEntry("Loose/Model.fbx").Open()) model.Write(Encoding.UTF8.GetBytes("loose FBX bytes"));
                using (var modelMeta = archive.CreateEntry("Loose/Model.fbx.meta").Open()) modelMeta.Write(Encoding.UTF8.GetBytes($"guid: {duplicateGuid}\n"));
                using (var prefabOutput = archive.CreateEntry("Loose/Avatar.prefab").Open()) prefabOutput.Write(Encoding.UTF8.GetBytes("prefab"));
                using (var prefabMeta = archive.CreateEntry("Loose/Avatar.prefab.meta").Open()) prefabMeta.Write(Encoding.UTF8.GetBytes($"guid: {prefabGuid}\n"));
            }

            var catalog = UnityAssetCatalog.OpenZip(zipPath);
            var looseModel = Assert.Single(catalog.Entries, x => x.ZipMemberPath == "Loose/Model.fbx");
            var prefab = Assert.Single(catalog.Entries, x => x.Guid == prefabGuid);
            var modelBytes = catalog.ReadAssetByGuid(duplicateGuid, referenceEntry: prefab);
            var stagedModel = catalog.StageAsset(looseModel, stagedRoot);

            Assert.Equal("Loose/Model.fbx", catalog.ResolvePath(duplicateGuid, referenceEntry: prefab));
            Assert.Equal("loose FBX bytes", Encoding.UTF8.GetString(modelBytes!));
            Assert.Equal("loose FBX bytes", File.ReadAllText(stagedModel));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static MemoryStream CreateUnityPackage(params (string Name, string Content)[] entries)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false)
                });
            }
        }
        output.Position = 0;
        return output;
    }

    private sealed class ZeroFilledReadStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => _position = value is >= 0 && value <= length ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => ReadZeroFilled(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadZeroFilled(buffer);

        private int ReadZeroFilled(Span<byte> buffer)
        {
            var read = (int)Math.Min(buffer.Length, length - _position);
            buffer[..read].Clear();
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = position;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
