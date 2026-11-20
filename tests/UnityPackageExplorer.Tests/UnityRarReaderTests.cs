using System.IO;
using System.Security.Cryptography;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityRarReaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Theory]
    [InlineData("Rar.multi.part03.rar")]
    [InlineData("Rar2.multi.r02")]
    [InlineData("Rar.multi.solid.part02.rar")]
    public void ReadsMembersAcrossVolumesWhenALaterVolumeWasSelected(string selected)
    {
        using var catalog = UnityAssetCatalog.OpenRar(Fixture(selected));
        Assert.True(catalog.IsSplitRar);
        var image = Assert.Single(catalog.Entries, entry => entry.Extension == ".jpg");

        Assert.Equal("B251C7501FB0F55DD4A92FEABE0A6F5733BC40A02679498155FAE9B30138FC53",
            Convert.ToHexString(SHA256.HashData(catalog.ReadAsset(image)!)));
        Assert.Throws<InvalidOperationException>(() => catalog.RebindArchivePath(Fixture("Rar5.solid.rar")));
    }

    [Theory]
    [InlineData("Rar.multi.part01.rar", "Rar.multi.part02.rar", "Rar.multi.part02.rar")]
    [InlineData("Rar.multi.part03.rar", "Rar.multi.part01.rar", "Rar.multi.part01.rar")]
    [InlineData("Rar.multi.part01.rar", "Rar.multi.part06.rar", "一部が見つからない")]
    [InlineData("Rar.multi.solid.part01.rar", "Rar.multi.solid.part06.rar", "一部が見つからない")]
    [InlineData("Rar2.multi.r02", "Rar2.multi.rar", "Rar2.multi.rar")]
    public void RejectsSetsWithMissingVolumes(string selected, string missing, string expectedMessage)
    {
        using var directory = ScopedDirectory.Create();
        var prefix = selected.StartsWith("Rar2.", StringComparison.Ordinal) ? "Rar2.multi."
            : selected.StartsWith("Rar.multi.solid.", StringComparison.Ordinal) ? "Rar.multi.solid.part" : "Rar.multi.part";
        var sourcePaths = Directory.GetFiles(Path.GetDirectoryName(Fixture(selected))!, prefix + "*")
            .Where(path => Path.GetFileName(path) != missing).ToArray();
        foreach (var path in sourcePaths)
            File.Copy(path, Path.Combine(directory.Path, Path.GetFileName(path)));

        var error = Assert.Throws<InvalidDataException>(() => new UnityRarReader(Path.Combine(directory.Path, selected)).ReadIndex());
        Assert.Contains(expectedMessage, error.Message);
        Assert.Equal(sourcePaths.Length, Directory.GetFiles(directory.Path).Length);
        foreach (var path in sourcePaths)
        {
            using var released = File.Open(Path.Combine(directory.Path, Path.GetFileName(path)), FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(new FileInfo(path).Length, released.Length);
        }
    }

    [Fact]
    public void OrdinaryRarWithPartLikeNameDoesNotReadAdjacentFiles()
    {
        using var directory = ScopedDirectory.Create();
        var selected = Path.Combine(directory.Path, "bundle.part02.rar");
        File.Copy(Fixture("Rar5.solid.rar"), selected);
        File.WriteAllText(Path.Combine(directory.Path, "bundle.part01.rar"), "unrelated");

        using var catalog = UnityAssetCatalog.OpenRar(selected);
        Assert.False(catalog.IsSplitRar);
        Assert.Contains(catalog.Entries, entry => entry.PathName == "jpg\\test.jpg");
    }

    [Fact]
    public void RedirectsSingleStreamPreviewOfSplitRarToTheApp()
    {
        var reader = new UnityRarReader(() => File.OpenRead(Fixture("Rar.multi.part01.rar")));

        var error = Assert.Throws<InvalidDataException>(() => reader.ReadIndex());
        Assert.Contains("UnityPackage Explorerで開いて", error.Message);
    }

    [Theory]
    [InlineData("Rar.Encrypted.rar")]
    [InlineData("Rar.encrypted_filesOnly.rar")]
    public void ReportsEncryptionWhetherHeadersOrOnlyMembersAreEncrypted(string name)
    {
        var error = Assert.Throws<InvalidDataException>(() => new UnityRarReader(Fixture(name)).ReadIndex());
        Assert.Contains("暗号化", error.Message);
    }

    [Fact]
    public void ReportsATruncatedRarHeaderAsCorrupt()
    {
        var reader = new UnityRarReader(() => new MemoryStream([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]));
        var error = Assert.Throws<InvalidDataException>(() => reader.ReadIndex());
        Assert.Contains("破損", error.Message);
    }

    [Fact]
    public void SolidMemberReadHonorsCancellationAndTheRequestedSizeLimit()
    {
        var reader = new UnityRarReader(Fixture("Rar5.solid.rar"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.ReadMember("jpg\\test.jpg", cancellation.Token));
        var sizeError = Assert.Throws<InvalidDataException>(() => reader.ReadMember("jpg\\test.jpg", 8));
        Assert.Contains("8 バイト", sizeError.Message);
    }

    private sealed class ScopedDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("upx-rar-").FullName;
        public static ScopedDirectory Create() => new();
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
