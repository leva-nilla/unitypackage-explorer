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

    [Fact]
    public void DisposesSourceWhenReadingThePackageSignatureFails()
    {
        var stream = new FailingReadStream();
        var reader = new UnityPackageReader(() => stream);

        Assert.Throws<IOException>(() => reader.ReadIndex());

        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public void RejectsPackagesThatExceedTheAggregateDecompressedSizeLimit()
    {
        using var package = TemporaryPackage.Create([
            ("a".PadLeft(32, '0') + "/pathname", "Assets/Props/Item.prefab"),
            ("a".PadLeft(32, '0') + "/asset", new string('x', 4096))
        ]);
        var reader = new UnityPackageReader(
            () => new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.Read),
            maximumExpandedArchiveBytes: 256);

        var exception = Assert.Throws<InvalidDataException>(() => reader.ReadIndex());

        Assert.Contains("decompressed size limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanApplyASmallerPerAssetReadLimit()
    {
        var guid = "a".PadLeft(32, '0');
        using var package = TemporaryPackage.Create([
            ($"{guid}/pathname", "Assets/Props/Item.prefab"),
            ($"{guid}/asset", new string('x', 4096))
        ]);
        var reader = new UnityPackageReader(package.Path);

        var exception = Assert.Throws<InvalidDataException>(() => reader.ReadAsset(guid, maximumBytes: 128));

        Assert.Contains("128 byte", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelsWhileDrainingAnUnselectedLargeTarEntry()
    {
        var guid = "a".PadLeft(32, '0');
        using var cancellation = new CancellationTokenSource();
        var stream = new CancellationTriggeredStream(CreateTarBytes($"{guid}/asset", 2 * 1024 * 1024), cancellation, 1024);
        var reader = new UnityPackageReader(() => stream);

        Assert.Throws<OperationCanceledException>(() => reader.ReadIndex(cancellation.Token));
        Assert.True(stream.CancellationWasTriggeredDuringRead);
    }

    [Fact]
    public void CancelsWhileCopyingTheRequestedLargeMaterialEntry()
    {
        var guid = "a".PadLeft(32, '0');
        using var cancellation = new CancellationTokenSource();
        var stream = new CancellationTriggeredStream(CreateTarBytes($"{guid}/asset", 2 * 1024 * 1024), cancellation, 1024);
        var reader = new UnityPackageReader(() => stream);

        Assert.Throws<OperationCanceledException>(() => reader.ReadAsset(guid, cancellationToken: cancellation.Token));
        Assert.True(stream.CancellationWasTriggeredDuringRead);
    }

    [Fact]
    public void SharesExpansionBudgetAcrossMultiplePackageReaders()
    {
        var guid = "a".PadLeft(32, '0');
        using var first = TemporaryPackage.Create([
            ($"{guid}/pathname", "Assets/Props/Item.prefab"),
            ($"{guid}/asset", new string('x', 4096))
        ]);
        using var second = TemporaryPackage.Create([
            ($"{guid}/pathname", "Assets/Props/Item.prefab"),
            ($"{guid}/asset", new string('y', 4096))
        ]);
        var budget = new ArchiveExpansionBudget(GetExpandedLength(first.Path) + 64);
        var firstReader = new UnityPackageReader(() => File.OpenRead(first.Path));
        var secondReader = new UnityPackageReader(() => File.OpenRead(second.Path));

        Assert.Single(firstReader.ReadIndex(expansionBudget: budget));
        Assert.Equal(new string('x', 4096), Encoding.UTF8.GetString(firstReader.ReadAsset(guid)!));
        var exception = Assert.Throws<InvalidDataException>(() => secondReader.ReadIndex(expansionBudget: budget));

        Assert.Contains("aggregate decompressed size limit", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    [InlineData("Assets/CONIN$/device.txt")]
    [InlineData("Assets/CONOUT$.log")]
    [InlineData("Assets/NUL.tar.gz")]
    [InlineData("Assets/COM1.foo.bar")]
    [InlineData("Assets/COM¹.txt")]
    [InlineData("Assets/COM²/file.txt")]
    [InlineData("Assets/COM³")]
    [InlineData("Assets/LPT¹.txt")]
    [InlineData("Assets/LPT²/file.txt")]
    [InlineData("Assets/LPT³")]
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
    public async Task ConcurrentExtractionsToTheSameDestinationChooseDifferentFolders()
    {
        var guid = "d".PadLeft(32, '0');
        using var package = TemporaryPackage.Create([
            ($"{guid}/pathname", "Assets/Props/Item.prefab"),
            ($"{guid}/asset", "new asset")
        ]);
        var destination = Path.Combine(Path.GetTempPath(), $"unitypackage-parallel-test-{Guid.NewGuid():N}");
        var trackedOutputs = new System.Collections.Concurrent.ConcurrentBag<string>();
        try
        {
            var outputs = await Task.WhenAll(
                Task.Run(() => ExtractAndTrack(package.Path, destination, trackedOutputs)),
                Task.Run(() => ExtractAndTrack(package.Path, destination, trackedOutputs)));

            Assert.Equal(2, outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(outputs, output => Assert.Equal("new asset", File.ReadAllText(Path.Combine(output, "Assets", "Props", "Item.prefab"))));
            Assert.All(outputs, output => Assert.Empty(Directory.EnumerateFiles(output, ".unitypackage-explorer-owner-*")));
        }
        finally
        {
            foreach (var output in trackedOutputs)
                if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static string ExtractAndTrack(string packagePath, string destination, System.Collections.Concurrent.ConcurrentBag<string> outputs)
    {
        var output = new UnityPackageReader(packagePath).ExtractTo(destination);
        outputs.Add(output);
        return output;
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

    private static long GetExpandedLength(string path)
    {
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.Length;
    }

    private static byte[] CreateTarBytes(string entryName, int contentLength)
    {
        using var output = new MemoryStream();
        using (var tar = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(new byte[contentLength], writable: false)
            });
        }
        return output.ToArray();
    }

    private sealed class CancellationTriggeredStream(byte[] contents, CancellationTokenSource cancellation, long cancelAfterBytes) : Stream
    {
        private readonly MemoryStream _inner = new(contents, writable: false);
        public bool CancellationWasTriggeredDuringRead { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            TriggerCancellationIfNeeded();
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            TriggerCancellationIfNeeded();
            return read;
        }
        private void TriggerCancellationIfNeeded()
        {
            if (Position < cancelAfterBytes || cancellation.IsCancellationRequested) return;
            CancellationWasTriggeredDuringRead = true;
            cancellation.Cancel();
        }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadStream : Stream
    {
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("test read failure");
        public override int Read(Span<byte> buffer) => throw new IOException("test read failure");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
