using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class ArchiveBundleTests
{
    [Fact]
    public async Task CombinesSplitPackagesWithoutChangingTheirBytesAndMovesOriginalsLast()
    {
        using var fixture = new Fixture();
        const string materialGuid = "11111111111111111111111111111111";
        var modelPackage = Package("22222222222222222222222222222222", "Assets/衣装.prefab", "Prefab");
        var materialPackage = Package(materialGuid, "Assets/赤.mat", "Material:\n  m_Name: Red\n");
        fixture.Zip(fixture.Sources[0], ("sub/モデル.unitypackage", modelPackage), ("説明.txt", "clothing"u8.ToArray()));
        fixture.Zip(fixture.Sources[1], ("素材.unitypackage", materialPackage), ("説明.txt", "materials"u8.ToArray()));
        var originalHashes = fixture.Sources.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();
        var plan = ArchiveBundleService.Plan(fixture.Sources, "変更した名前");
        var result = await ArchiveBundleService.CreateAsync(plan, new InlineProgress(value =>
        {
            if (value.Stage == "統合ZIPを検証しています")
                Assert.All(fixture.Sources, source => Assert.True(File.Exists(source)));
        }));
        Assert.Equal(Path.Combine(fixture.Root, "変更した名前"), result.DirectoryPath);
        Assert.Equal("変更した名前_統合.zip", Path.GetFileName(result.ArchivePath));
        Assert.Empty(result.Warnings);
        using var zip = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(2, zip.Entries.Count);
        Assert.Null(zip.GetEntry("説明.txt"));
        Assert.Null(zip.GetEntry("sub/モデル.unitypackage"));
        Assert.Null(zip.GetEntry("衣装/モデル.unitypackage"));
        using (var stream = zip.GetEntry("モデル.unitypackage")!.Open())
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            Assert.Equal(modelPackage, buffer.ToArray());
        }
        for (var index = 0; index < fixture.Sources.Length; index++)
        {
            Assert.False(File.Exists(fixture.Sources[index]));
            var moved = Path.Combine(result.DirectoryPath, Path.GetFileName(fixture.Sources[index]));
            Assert.Equal(originalHashes[index], SHA256.HashData(File.ReadAllBytes(moved)));
        }
        using var catalog = UnityAssetCatalog.OpenZip(result.ArchivePath);
        var prefab = Assert.Single(catalog.Entries, entry => entry.Extension == ".prefab");
        Assert.Equal("Red", catalog.ReadMaterialInfo(materialGuid, packageMemberPath: prefab.PackageMemberPath, referenceEntry: prefab)?.Name);
        Assert.Equal(3, Directory.GetFiles(result.DirectoryPath).Length);
        Assert.Empty(Directory.GetDirectories(result.DirectoryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundlesZipAndRarWithOrWithoutMergingUnityPackages(bool merge)
    {
        using var fixture = new Fixture(rar: true);
        const string prefabGuid = "11111111111111111111111111111111";
        const string materialGuid = "22222222222222222222222222222222";
        var prefab = Package(prefabGuid, "Assets/Clothes.prefab", "material: {fileID: 2100000, guid: " + materialGuid + ", type: 2}");
        var material = Package(materialGuid, "Assets/Material.mat", "Material:\n  m_Name: Mixed\n");
        fixture.Zip(fixture.Sources[0], ("folder/Clothes.unitypackage", prefab), ("ignored.png", [1, 2]));
        fixture.Rar("folder/" + (merge ? "Clothes" : "Material") + ".unitypackage", material);
        File.WriteAllText(fixture.Sources[1] + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var originalContents = fixture.Sources.Select(File.ReadAllBytes).ToArray();

        var result = await ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "出力", merge));

        Assert.Empty(result.Warnings);
        using (var zip = ZipFile.OpenRead(result.ArchivePath))
        {
            if (merge) Assert.Equal("出力.unitypackage", Assert.Single(zip.Entries).FullName);
            else
            {
                Assert.Equal(new[] { "Clothes.unitypackage", "Material.unitypackage" }, zip.Entries.Select(entry => entry.FullName));
                using var stored = zip.GetEntry("Material.unitypackage")!.Open();
                using var bytes = new MemoryStream();
                stored.CopyTo(bytes);
                Assert.Equal(material, bytes.ToArray());
            }
        }
        using var catalog = UnityAssetCatalog.OpenZip(result.ArchivePath);
        var actualPrefab = Assert.Single(catalog.Entries, entry => entry.Guid == prefabGuid);
        Assert.Contains(materialGuid, Encoding.UTF8.GetString(catalog.ReadAsset(actualPrefab)!));
        Assert.Equal("Mixed", catalog.ReadMaterialInfo(materialGuid, packageMemberPath: actualPrefab.PackageMemberPath,
            referenceEntry: actualPrefab)?.Name);
        Assert.Contains("ZoneId=3", File.ReadAllText(result.ArchivePath + ":Zone.Identifier"));
        var movedRar = Path.Combine(result.DirectoryPath, Path.GetFileName(fixture.Sources[1]));
        Assert.Contains("ZoneId=3", File.ReadAllText(movedRar + ":Zone.Identifier"));
        for (var i = 0; i < fixture.Sources.Length; i++)
        {
            Assert.False(File.Exists(fixture.Sources[i]));
            Assert.Equal(originalContents[i], File.ReadAllBytes(Path.Combine(result.DirectoryPath, Path.GetFileName(fixture.Sources[i]))));
        }
        Assert.Equal(3, Directory.GetFiles(result.DirectoryPath).Length);
        Assert.Empty(Directory.GetDirectories(result.DirectoryPath));
    }

    [Fact]
    public async Task KeepsIdenticalGuidOnlyOnceAndPreservesFolderAndPreview()
    {
        using var fixture = new Fixture();
        const string guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string folderGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var asset = AssetParts(guid, "Assets/Folder/Test.mat", "content").ToArray();
        fixture.Zip(fixture.Sources[0], ("first.unitypackage", Package([.. asset,
            (folderGuid + "/pathname", "Assets/Folder"),
            (folderGuid + "/asset.meta", "fileFormatVersion: 2\nguid: " + folderGuid + "\nfolderAsset: yes\n")])));
        fixture.Zip(fixture.Sources[1], ("second.unitypackage", Package([.. asset, (guid + "/preview.png", "preview payload")])));
        var result = await ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合", mergePackages: true));
        using var zip = ZipFile.OpenRead(result.ArchivePath);
        using var gzip = new GZipStream(Assert.Single(zip.Entries).Open(), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var contents = new Dictionary<string, string>();
        while (tar.GetNextEntry() is { } entry)
        {
            using var text = new StreamReader(entry.DataStream!);
            contents.Add(entry.Name, text.ReadToEnd());
        }
        Assert.Equal(6, contents.Count);
        Assert.Equal("content", contents[guid + "/asset"]);
        Assert.Equal("preview payload", contents[guid + "/preview.png"]);
        Assert.Contains("folderAsset: yes", contents[folderGuid + "/asset.meta"]);
    }

    [Theory]
    [InlineData(false, "Assets/Test.prefab", "Assets/TEST.prefab", "first", "second", "パス")]
    [InlineData(false, "Assets/Test", "Assets/Test/Child.prefab", "first", "second", "パス")]
    [InlineData(false, "Assets/Test", "Assets/Test.meta", "first", "second", "パス")]
    [InlineData(true, "Assets/Test.prefab", "Assets/Test.prefab", "first", "second", "GUID")]
    [InlineData(true, "Assets/Test.prefab", "Assets/Other.prefab", "same", "same", "GUID")]
    public async Task MergeConflictsLeaveEveryOriginalInPlace(bool reuseGuid, string firstPath, string secondPath,
        string firstContent, string secondContent, string message)
    {
        using var fixture = new Fixture();
        const string guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        fixture.Zip(fixture.Sources[0], ("first.unitypackage", Package(guid, firstPath, firstContent)));
        fixture.Zip(fixture.Sources[1], ("second.unitypackage", Package(reuseGuid ? guid : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", secondPath, secondContent)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合", mergePackages: true)));
        Assert.Contains(message, error.Message);
        fixture.AssertOriginals();
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Theory]
    [InlineData("Rar.multi.part01.rar", "分割RAR")]
    [InlineData("Rar.Encrypted.rar", "暗号化")]
    public async Task UnsupportedRarDoesNotTouchAnyOriginal(string fixtureName, string message)
    {
        using var fixture = new Fixture(rar: true);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", fixtureName), fixture.Sources[1]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合")));
        Assert.Contains(message, error.Message);
        fixture.AssertOriginals();
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Fact]
    public async Task RejectsPackageLinkInsideRar()
    {
        using var fixture = new Fixture(rar: true);
        fixture.Rar("link.unitypackage", "file"u8.ToArray(), attributes: 0xa1ff); // Unix symbolic link
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合")));
        Assert.Contains("リンク", error.Message);
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task ReadsAllPackageMembersOfASingleSolidRarInOrder()
    {
        using var fixture = new Fixture(rar: true);
        var lead = UnityZipReaderTests.CreateStoredRar4("ignored.txt", "ignored"u8.ToArray(), solid: true);
        var first = UnityZipReaderTests.CreateStoredRar4("first.unitypackage",
            Package("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Assets/First.mat", "first"));
        var second = UnityZipReaderTests.CreateStoredRar4("second.unitypackage",
            Package("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Assets/Second.mat", "second"));
        // Keep one signature/main header and one closing header. The stored
        // members exercise sequential solid handling without a RAR writer.
        File.WriteAllBytes(fixture.Sources[1], [.. lead.AsSpan(0, lead.Length - 7),
            .. first.AsSpan(20, first.Length - 27), .. second.AsSpan(20)]);
        var result = await ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合"));
        using var zip = ZipFile.OpenRead(result.ArchivePath);
        Assert.Equal(3, zip.Entries.Count);
        Assert.NotNull(zip.GetEntry("first.unitypackage"));
        Assert.NotNull(zip.GetEntry("second.unitypackage"));
        Assert.Null(zip.GetEntry("ignored.txt"));
    }

    [Fact]
    public async Task MergeRejectsAnAssetWithoutMatchingMetadata()
    {
        using var fixture = new Fixture();
        const string guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        fixture.Zip(fixture.Sources[1], ("wrong.unitypackage", Package([
            (guid + "/pathname", "Assets/Invalid.prefab"), (guid + "/asset", "data"),
            (guid + "/asset.meta", "guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n")
        ])));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合", mergePackages: true)));
        Assert.Contains(".meta のGUID", error.Message);
        fixture.AssertOriginals();
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Fact]
    public async Task ZipWithoutAnyPackageIsRejectedBeforeMovingOriginals()
    {
        using var fixture = new Fixture();
        fixture.Zip(fixture.Sources[1], ("説明.txt", "materials"u8.ToArray()));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合")));
        Assert.Contains(".unitypackage", error.Message);
        Assert.Contains(Path.GetFileName(fixture.Sources[1]), error.Message);
        fixture.AssertOriginals();
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Theory]
    [InlineData("../outside.unitypackage")]
    [InlineData("/root.unitypackage")]
    [InlineData("C:/root.unitypackage")]
    [InlineData("folder/CON.unitypackage")]
    [InlineData("folder/a:stream.unitypackage")]
    [InlineData("folder./a.unitypackage")]
    public async Task RejectsUnsafeMemberNamesBeforeMovingOriginals(string name)
    {
        using var fixture = new Fixture();
        fixture.Zip(fixture.Sources[0], (name, "unsafe"u8.ToArray()));
        var error = await Record.ExceptionAsync(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合")));
        Assert.True(error is ArgumentException or InvalidDataException, error?.ToString());
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task RejectsCaseInsensitiveDuplicateMembersAndCorruptArchives()
    {
        using var fixture = new Fixture();
        fixture.Zip(fixture.Sources[0], ("same.unitypackage", [1]), ("SAME.unitypackage", [2]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合")));
        fixture.AssertOriginals();
        File.WriteAllText(fixture.Sources[0], "not a ZIP");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合")));
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task DifferentPayloadOrMetadataForTheSameGuidDoesNotChooseAWinner()
    {
        using var fixture = new Fixture();
        const string guid = "11111111111111111111111111111111";
        fixture.Zip(fixture.Sources[0], ("first.unitypackage", Package(guid, "Assets/Color.mat", "red")));
        fixture.Zip(fixture.Sources[1], ("second.unitypackage", Package(guid, "Assets/Color.mat", "blue")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合")));
        Assert.Contains("GUID", error.Message);
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task CancellationAndMoveFailureRestoreOriginalLocations()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合"),
                new InlineProgress(value => { if (value.Stage == "統合ZIPを検証しています") cancellation.Cancel(); }), cancellation.Token));
        fixture.AssertOriginals();
        var moveCount = 0;
        await Assert.ThrowsAsync<IOException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合"),
                new InlineProgress(value =>
                {
                    if (value.Stage == "元ファイルをフォルダーへ移動しています" && ++moveCount == 2)
                        throw new IOException("simulated move failure");
                })));
        fixture.AssertOriginals();
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Fact]
    public async Task DestinationCreatedDuringWorkIsNeverOverwritten()
    {
        using var fixture = new Fixture();
        var plan = ArchiveBundleService.Plan(fixture.Sources, "統合");
        await Assert.ThrowsAsync<IOException>(() => ArchiveBundleService.CreateAsync(plan, new InlineProgress(value =>
        {
            if (value.Stage == "統合ZIPを検証しています")
            {
                Directory.CreateDirectory(plan.DestinationDirectory);
                File.WriteAllText(Path.Combine(plan.DestinationDirectory, "user.txt"), "keep");
            }
        })));
        fixture.AssertOriginals();
        Assert.Equal("keep", File.ReadAllText(Path.Combine(plan.DestinationDirectory, "user.txt")));
    }

    [Fact]
    public async Task FailedRollbackKeepsOriginalInRecoveryFolderAndDoesNotOverwriteNewFile()
    {
        using var fixture = new Fixture();
        var expected = File.ReadAllBytes(fixture.Sources[0]);
        var moveCount = 0;
        var error = await Assert.ThrowsAsync<ArchiveBundleRecoveryException>(() =>
            ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合"),
                new InlineProgress(value =>
                {
                    if (value.Stage != "元ファイルをフォルダーへ移動しています" || ++moveCount != 2) return;
                    File.WriteAllText(fixture.Sources[0], "new unrelated file");
                    throw new IOException("stop after first move");
                })));
        Assert.Equal("new unrelated file", File.ReadAllText(fixture.Sources[0]));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(error.RecoveryDirectory, Path.GetFileName(fixture.Sources[0]))));
        Assert.True(File.Exists(fixture.Sources[1]));
        Assert.Contains(error.RecoveryDirectory, error.Message);
    }

    [Fact]
    public async Task InternetZoneIsPreservedForOriginalsAndPropagatedToDerivedZip()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sources[0] + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var result = await ArchiveBundleService.CreateAsync(ArchiveBundleService.Plan(fixture.Sources, "統合"));
        Assert.Contains("ZoneId=3", File.ReadAllText(result.ArchivePath + ":Zone.Identifier"));
        Assert.Contains("ZoneId=3", File.ReadAllText(Path.Combine(result.DirectoryPath, "衣装.zip") + ":Zone.Identifier"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("other/name")]
    [InlineData("name.")]
    [InlineData("NUL")]
    public void InvalidFolderNamesNeverStartAnOperation(string name)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => ArchiveBundleService.Plan(fixture.Sources, name));
        fixture.AssertOriginals();
    }

    [Fact]
    public void ExistingTargetsAndMixedFoldersAreRejected()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "既存"));
        Assert.Throws<IOException>(() => ArchiveBundleService.Plan(fixture.Sources, "既存"));
        Assert.Throws<ArgumentException>(() => ArchiveBundleService.Plan([fixture.Sources[0]], "統合"));
        Assert.Throws<ArgumentException>(() => ArchiveBundleService.Plan([fixture.Sources[0], fixture.Sources[0]], "統合"));
        var other = Path.Combine(fixture.Root, "既存", "other.zip");
        File.WriteAllText(other, "keep");
        Assert.Throws<ArgumentException>(() => ArchiveBundleService.Plan([fixture.Sources[0], other], "統合"));
    }

    private sealed class InlineProgress(Action<ArchiveBundleProgress> report) : IProgress<ArchiveBundleProgress>
    {
        public void Report(ArchiveBundleProgress value) => report(value);
    }

    private static byte[] Package(string guid, string path, string content)
        => Package([.. AssetParts(guid, path, content)]);

    private static IEnumerable<(string Name, string Text)> AssetParts(string guid, string path, string content) =>
        [(guid + "/pathname", path), (guid + "/asset", content), (guid + "/asset.meta", "fileFormatVersion: 2\nguid: " + guid + "\n")];

    private static byte[] Package((string Name, string Text)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
            foreach (var (name, value) in entries)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(value));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data });
            }
        return bytes.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "UPX-bundle-tests-" + Guid.NewGuid().ToString("N"));
        public string[] Sources { get; }
        public Fixture(bool rar = false)
        {
            Directory.CreateDirectory(Root);
            Sources = [Path.Combine(Root, "衣装.zip"), Path.Combine(Root, rar ? "素材.rar" : "素材.zip")];
            for (var index = 0; index < (rar ? 1 : Sources.Length); index++)
            {
                var guid = new string((char)('1' + index), 32);
                Zip(Sources[index], ("readme.txt", "unchanged"u8.ToArray()),
                    (Path.GetFileNameWithoutExtension(Sources[index]) + ".unitypackage",
                        Package(guid, "Assets/fixture" + index + ".prefab", "Prefab")));
            }
        }
        public void Rar(string name, byte[] bytes, uint attributes = 0)
            => File.WriteAllBytes(Sources[1], UnityZipReaderTests.CreateStoredRar4(name, bytes, attributes: attributes));
        public void Zip(string path, params (string Name, byte[] Bytes)[] entries)
        {
            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                using var stream = zip.CreateEntry(entry.Name).Open();
                stream.Write(entry.Bytes);
            }
        }
        public void AssertOriginals() => Assert.All(Sources, path => Assert.True(File.Exists(path), path));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
