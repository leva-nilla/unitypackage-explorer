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
        fixture.Zip(fixture.Sources[0], ("モデル.unitypackage", modelPackage), ("説明.txt", "clothing"u8.ToArray()));
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
        Assert.Equal(4, zip.Entries.Count);
        using (var stream = zip.GetEntry("衣装/モデル.unitypackage")!.Open())
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
    [InlineData("../outside.txt")]
    [InlineData("/root.txt")]
    [InlineData("C:/root.txt")]
    [InlineData("folder/CON.txt")]
    [InlineData("folder/a:stream")]
    [InlineData("folder/a.")]
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
        fixture.Zip(fixture.Sources[0], ("same.txt", [1]), ("SAME.txt", [2]));
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
                    if (value.Stage == "元ZIPをフォルダーへ移動しています" && ++moveCount == 2)
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
                    if (value.Stage != "元ZIPをフォルダーへ移動しています" || ++moveCount != 2) return;
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
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
            foreach (var (name, value) in new[] { ("pathname", path), ("asset", content), ("asset.meta", "fileFormatVersion: 2\nguid: " + guid + "\n") })
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(value));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, guid + "/" + name) { DataStream = data });
            }
        return bytes.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "UPX-bundle-tests-" + Guid.NewGuid().ToString("N"));
        public string[] Sources { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Sources = [Path.Combine(Root, "衣装.zip"), Path.Combine(Root, "素材.zip")];
            foreach (var source in Sources) Zip(source, ("readme.txt", "unchanged"u8.ToArray()));
        }
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
