using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class CatalogCancellationCleanupTests
{
    [Fact]
    public void CancellationDuringLooseMetadataAfterNestedIndexReleasesItsSpill()
    {
        var root = Path.Combine(Path.GetTempPath(), $"upx-catalog-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var archive = new CancelAfterIndexedArchive(CreateLargePackage(), root, cancellation);
            Assert.ThrowsAny<OperationCanceledException>(() =>
                UnityAssetCatalog.OpenArchive(archive, cancellation.Token, expandedCacheSpillDirectory: root));
            Assert.True(archive.SpillObserved);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateLargePackage()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            const string guid = "0123456789abcdef0123456789abcdef";
            using var path = new MemoryStream(Encoding.UTF8.GetBytes("Assets/Big.fbx"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{guid}/pathname") { DataStream = path });
            using var asset = new MemoryStream(new byte[65 * 1024 * 1024], writable: false);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{guid}/asset") { DataStream = asset });
        }
        return output.ToArray();
    }

    private sealed class CancelAfterIndexedArchive(
        byte[] package, string spillRoot, CancellationTokenSource cancellation) : IUnityArchiveReader
    {
        public bool SpillObserved { get; private set; }
        public string FormatName => "Test";
        public IReadOnlyList<UnityArchiveMember> ReadIndex(CancellationToken cancellationToken = default) =>
            [new("first.unitypackage", package.Length, package.Length), new("loose.fbx", 1, 1), new("loose.fbx.meta", 1, 1)];
        public Stream OpenMember(string memberPath, long maximumBytes = UnityArchiveLimits.MaximumStreamedMemberBytes) =>
            new MemoryStream(package, writable: false);
        public byte[] ReadMember(string memberPath, CancellationToken cancellationToken = default) =>
            ReadMember(memberPath, UnityArchiveLimits.MaximumBufferedMemberBytes, cancellationToken);
        public byte[] ReadMember(string memberPath, long maximumBytes, CancellationToken cancellationToken = default)
        {
            SpillObserved = Directory.GetFiles(spillRoot).Length > 0;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }
        public void RebindPath(string path) => throw new NotSupportedException();
    }

}
