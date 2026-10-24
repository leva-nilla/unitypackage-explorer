using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityZipReaderTests
{
    // SharpCompress TestArchives/Archives/Rar5.comment.plain.rar, MIT licensed,
    // SHA-256 9CD2CA8EA137E220987DA76B33863F4BE0E74FE166D1459D79241D0E721DA2ED.
    private const string Rar5PlainFixture = "UmFyIRoHAQDB319WAwEEACUj1XYTAwKRAASRAADbmNhBgAABA0NNVDEyMzQ1Njc4OTAxMjM0NcOpdkIYxSkCAwuQAASQAKSDAkoDyL2AAAELcGF5bG9hZC50eHQKAxO+fr9q3rWXH2hlbGxvIGZyb20gUkFSIQodd1ZRAwUEAA==";

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
    public void IndexesMembersLargerThanTheBufferedReadLimit()
    {
        const long declaredLength = UnityArchiveLimits.MaximumBufferedMemberBytes + 1;
        var reader = new UnityZipReader(() => CreateZipWithDeclaredMemberLength("large.unitypackage", declaredLength));

        var member = Assert.Single(reader.ReadIndex());

        Assert.Equal("large.unitypackage", member.FullName);
        Assert.Equal(declaredLength, member.Length);
        Assert.Throws<InvalidDataException>(() => reader.ReadMember(member.FullName));
    }

    [Fact]
    public void OpensAnOversizedDeclaredNestedUnityPackageThroughTheZipCatalog()
    {
        const string guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-large-inner-zip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "bundle.zip");
        try
        {
            using var package = CreateUnityPackage((guid + "/pathname", "Assets/Props/Oversized.prefab"), (guid + "/asset", "prefab bytes"));
            File.WriteAllBytes(zipPath, CreateDeflatedZipWithDeclaredMemberLength("Packages/Oversized.unitypackage", package.ToArray(), UnityArchiveLimits.MaximumBufferedMemberBytes + 1));

            var catalog = UnityAssetCatalog.OpenZip(zipPath);
            var entry = Assert.Single(catalog.Entries);

            Assert.Equal("Assets/Props/Oversized.prefab", entry.AssetPathName);
            Assert.Equal("prefab bytes", Encoding.UTF8.GetString(catalog.ReadAsset(entry)!));
            Assert.Empty(catalog.Warnings);
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsARealRar5MemberWithoutExtractingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-rar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var rarPath = Path.Combine(root, "fixture.rar");
        try
        {
            File.WriteAllBytes(rarPath, Convert.FromBase64String(Rar5PlainFixture));

            var reader = new UnityRarReader(rarPath);
            var member = Assert.Single(reader.ReadIndex());
            var catalog = UnityAssetCatalog.OpenRar(rarPath);
            var entry = Assert.Single(catalog.Entries);

            Assert.Equal("payload.txt", member.FullName);
            Assert.Equal(16, member.Length);
            Assert.Equal("hello from RAR!\n", Encoding.UTF8.GetString(reader.ReadMember(member.FullName)));
            Assert.Equal("hello from RAR!\n", Encoding.UTF8.GetString(catalog.ReadAsset(entry)!));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsALaterSolidRarMemberWithItsPrecedingDictionary()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "Rar5.solid.rar");
        var original = File.ReadAllBytes(fixture);
        var catalog = UnityAssetCatalog.OpenRar(fixture);
        var image = Assert.Single(catalog.Entries, entry => entry.PathName == "jpg\\test.jpg");

        // This third file needs the dictionary built from both preceding files.
        var first = catalog.ReadAsset(image)!;
        var second = catalog.ReadAsset(image)!;

        Assert.Equal(40372, first.Length);
        Assert.Equal("B251C7501FB0F55DD4A92FEABE0A6F5733BC40A02679498155FAE9B30138FC53",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first)));
        Assert.Equal(first, second);
        Assert.Equal(original, File.ReadAllBytes(fixture));
    }

    [Fact]
    public void ReadsANestedUnityPackageFromAStoredRarWithoutExtractingIt()
    {
        const string guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-nested-rar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var rarPath = Path.Combine(root, "bundle.rar");
        try
        {
            using var package = CreateUnityPackage((guid + "/pathname", "Assets/Props/Nested.prefab"), (guid + "/asset", "nested prefab bytes"));
            File.WriteAllBytes(rarPath, CreateStoredRar4("Packages/Nested.unitypackage", package.ToArray()));

            var catalog = UnityAssetCatalog.OpenRar(rarPath);
            var entry = Assert.Single(catalog.Entries);

            Assert.Equal("Assets/Props/Nested.prefab", entry.AssetPathName);
            Assert.Equal("nested prefab bytes", Encoding.UTF8.GetString(catalog.ReadAsset(entry)!));
            Assert.Empty(catalog.Warnings);
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsCorruptRarWithoutChangingTheSourceFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-corrupt-rar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var rarPath = Path.Combine(root, "broken.rar");
        try
        {
            var bytes = Convert.FromBase64String(Rar5PlainFixture);
            bytes[8] ^= 1; // Break the archive header CRC while retaining the RAR signature.
            File.WriteAllBytes(rarPath, bytes);
            var before = System.Security.Cryptography.SHA256.HashData(bytes);

            Assert.Throws<SharpCompress.Common.InvalidFormatException>(() => new UnityRarReader(rarPath).ReadIndex());

            Assert.Equal(before, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(rarPath)));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsRarMembersAboveTheStreamingLimitWithoutReadingThem()
    {
        var declaredLength = UnityArchiveLimits.MaximumStreamedMemberBytes + 1;
        const string name = "large.unitypackage";
        var archive = CreateStoredRar4(name, [0x00], declaredLength, packedLength: declaredLength);
        var dataStart = GetRarDataStart(name);
        var reader = new UnityRarReader(() => new SparseTailStream(
            archive[..dataStart],
            checked(dataStart + declaredLength),
            archive[(dataStart + 1)..]));

        var member = Assert.Single(reader.ReadIndex());

        Assert.Equal(declaredLength, member.Length);
        Assert.Throws<InvalidDataException>(() => reader.OpenMember(member.FullName));
    }

    [Fact]
    public void CountsRarDirectoriesTowardTheEntryLimit()
    {
        var directoryArchive = CreateStoredRar4("folder", [], isDirectory: true);
        var entryBytes = directoryArchive.AsSpan(20, directoryArchive.Length - 27).ToArray();
        using var contents = new MemoryStream();
        contents.Write(directoryArchive.AsSpan(0, 20));
        for (var index = 0; index <= UnityArchiveLimits.MaximumEntryCount; index++)
            contents.Write(entryBytes);
        contents.Write(directoryArchive.AsSpan(directoryArchive.Length - 7));
        var bytes = contents.ToArray();
        var reader = new UnityRarReader(() => new MemoryStream(bytes, writable: false));

        var exception = Assert.Throws<InvalidDataException>(() => reader.ReadIndex());

        Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private static MemoryStream CreateZipWithDeclaredMemberLength(string name, long declaredLength)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x04034b50u); // local file header
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write(nameBytes);

        var centralOffset = checked((uint)stream.Position);
        writer.Write(0x02014b50u); // central directory header
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(checked((uint)declaredLength));
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(nameBytes);

        writer.Write(0x06054b50u); // end of central directory
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(checked((uint)(46 + nameBytes.Length)));
        writer.Write(centralOffset);
        writer.Write((ushort)0);
        stream.Position = 0;
        return stream;
    }

    private static byte[] CreateDeflatedZipWithDeclaredMemberLength(string name, byte[] contents, long declaredLength)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(contents);
            compressed = output.ToArray();
        }

        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x04034b50u); // local file header
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)8); // deflate
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(Crc32(contents));
        writer.Write(checked((uint)compressed.Length));
        writer.Write(checked((uint)declaredLength));
        writer.Write(checked((ushort)nameBytes.Length));
        writer.Write((ushort)0);
        writer.Write(nameBytes);
        writer.Write(compressed);

        var centralOffset = checked((uint)stream.Position);
        writer.Write(0x02014b50u); // central directory header
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)8);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(Crc32(contents));
        writer.Write(checked((uint)compressed.Length));
        writer.Write(checked((uint)declaredLength));
        writer.Write(checked((ushort)nameBytes.Length));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(nameBytes);

        writer.Write(0x06054b50u); // end of central directory
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(checked((uint)(46 + nameBytes.Length)));
        writer.Write(centralOffset);
        writer.Write((ushort)0);
        return stream.ToArray();
    }

    private static byte[] CreateStoredRar4(string name, byte[] contents, long? declaredLength = null, long? packedLength = null, bool isDirectory = false)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var unpackedLength = declaredLength ?? contents.LongLength;
        var storedLength = packedLength ?? contents.LongLength;
        if (unpackedLength is < 0 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(declaredLength));
        if (storedLength is < 0 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(packedLength));
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }); // RAR4 signature
        WriteRarHeader(writer, [0x73, 0, 0, 13, 0, 0, 0, 0, 0, 0, 0]); // main header

        using var fileHeader = new MemoryStream();
        using (var fileWriter = new BinaryWriter(fileHeader, Encoding.UTF8, leaveOpen: true))
        {
            fileWriter.Write((byte)0x74); // file header
            fileWriter.Write((ushort)(isDirectory ? 0x80e0 : 0x8000)); // long block, with directory flags when requested
            fileWriter.Write(checked((ushort)(32 + nameBytes.Length)));
            fileWriter.Write(checked((uint)storedLength));
            fileWriter.Write(checked((uint)unpackedLength));
            fileWriter.Write((byte)3); // Unix host OS
            fileWriter.Write(Crc32(contents));
            fileWriter.Write(0u);
            fileWriter.Write((byte)20);
            fileWriter.Write((byte)0x30); // stored
            fileWriter.Write(checked((ushort)nameBytes.Length));
            fileWriter.Write(0u);
            fileWriter.Write(nameBytes);
        }
        WriteRarHeader(writer, fileHeader.ToArray());
        writer.Write(contents);
        WriteRarHeader(writer, [0x7b, 0, 0, 7, 0]); // end archive header
        return stream.ToArray();
    }

    private static void WriteRarHeader(BinaryWriter writer, byte[] header)
    {
        writer.Write(checked((ushort)(Crc32(header) & 0xffff)));
        writer.Write(header);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xffffffffu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    private static int GetRarDataStart(string name) => 7 + 13 + 32 + Encoding.ASCII.GetByteCount(name);

    /// <summary>Exposes a sparse RAR member without allocating a multi-gigabyte test file.</summary>
    private sealed class SparseTailStream(byte[] prefix, long suffixOffset, byte[] suffix) : Stream
    {
        private long _position;
        private readonly long _length = checked(suffixOffset + suffix.LongLength);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }
        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length || buffer.IsEmpty) return 0;
            var available = checked((int)Math.Min(buffer.Length, _length - _position));
            for (var index = 0; index < available; index++)
            {
                var sourcePosition = _position + index;
                buffer[index] = sourcePosition < prefix.LongLength
                    ? prefix[(int)sourcePosition]
                    : sourcePosition >= suffixOffset && sourcePosition - suffixOffset < suffix.LongLength
                        ? suffix[(int)(sourcePosition - suffixOffset)]
                        : (byte)0;
            }
            _position += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
