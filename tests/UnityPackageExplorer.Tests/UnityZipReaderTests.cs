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
                using (var metaOutput = archive.CreateEntry("Extras/Readme.txt.meta").Open())
                    metaOutput.Write(Encoding.UTF8.GetBytes($"fileFormatVersion: 2\nguid: {looseGuid}\n"));
            }

            var members = new UnityZipReader(zipPath).ReadIndex();
            var catalog = UnityAssetCatalog.OpenZip(zipPath);
            var packageEntries = catalog.Entries.Where(x => x.Extension == ".prefab").ToArray();
            var packageEntry = Assert.Single(packageEntries, x => x.PackageMemberPath == "Packages/Avatar.unitypackage");
            var secondPackageEntry = Assert.Single(packageEntries, x => x.PackageMemberPath == "Packages/Alt.unitypackage");
            var looseEntry = Assert.Single(catalog.Entries, x => x.Extension == ".txt");

            Assert.Contains(members, x => x.FullName == "Packages/Avatar.unitypackage");
            Assert.Equal("prefab bytes", Encoding.UTF8.GetString(catalog.ReadAsset(packageEntry)!));
            Assert.Equal("second prefab bytes", Encoding.UTF8.GetString(catalog.ReadAsset(secondPackageEntry)!));
            Assert.Equal("Assets/Alt/Avatar.prefab", catalog.ResolvePath(packageGuid, "Packages/Alt.unitypackage"));
            Assert.Equal("loose bytes", Encoding.UTF8.GetString(catalog.ReadAssetByGuid(looseGuid)!));
            Assert.Equal("Assets/Avatar/Avatar.prefab", catalog.ResolvePath(packageGuid, "Packages/Avatar.unitypackage"));
            Assert.Equal("Extras/Readme.txt", looseEntry.PathName);
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
}
