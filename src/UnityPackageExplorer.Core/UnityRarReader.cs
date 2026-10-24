using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace UnityPackageExplorer.Core;

/// <summary>Reads RAR metadata and individual members without extracting the archive.</summary>
public sealed class UnityRarReader : IUnityArchiveReader
{
    private string _path;
    private readonly Func<Stream>? _openStream;

    public UnityRarReader(string path) => _path = Path.GetFullPath(path);

    public UnityRarReader(Func<Stream> openStream)
    {
        _path = string.Empty;
        _openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
    }

    public string FormatName => "RAR";

    public void RebindPath(string path)
    {
        if (_openStream is not null) throw new InvalidOperationException("A stream-backed RAR reader cannot be rebound to a file path.");
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<UnityArchiveMember> ReadIndex(CancellationToken cancellationToken = default)
    {
        using var file = OpenFile();
        using var archive = OpenArchive(file);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UnityArchiveMember>();
        long declaredTotal = 0;
        var entryCount = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > UnityArchiveLimits.MaximumEntryCount)
                throw new InvalidDataException($"RAR contains more than {UnityArchiveLimits.MaximumEntryCount:N0} entries.");
            if (entry.IsDirectory) continue;
            var key = entry.Key ?? throw new InvalidDataException("RAR contains an entry without a name.");
            if (!names.Add(key)) throw new InvalidDataException($"RAR contains a duplicate member name: {key}");
            declaredTotal = checked(declaredTotal + entry.Size);
            if (declaredTotal > UnityArchiveLimits.MaximumDeclaredTotalBytes)
                throw new InvalidDataException("RAR declares more than 8 GiB of uncompressed content.");
            result.Add(new UnityArchiveMember(key, entry.Size, entry.CompressedSize));
        }
        return result;
    }

    public Stream OpenMember(string memberPath, long maximumBytes = UnityArchiveLimits.MaximumStreamedMemberBytes)
    {
        if (maximumBytes is < 0 or > UnityArchiveLimits.MaximumStreamedMemberBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var file = OpenFile();
        IRarArchive? archive = null;
        IReader? sequentialReader = null;
        try
        {
            archive = OpenArchive(file);
            var entry = archive.Entries.FirstOrDefault(candidate => !candidate.IsDirectory && string.Equals(candidate.Key, memberPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"RAR member was not found: {memberPath}");
            if (entry.Size > maximumBytes) throw new InvalidDataException($"RAR member exceeds the {maximumBytes / 1024 / 1024} MiB streaming safety limit: {memberPath}");
            if (!archive.IsSolid) return new OwnedRarMemberStream(entry.OpenEntryStream(), archive, file);

            // Solid members share the decompressor dictionary with earlier files.
            // Decode those files in order without writing them to disk.
            sequentialReader = archive.ExtractAllEntries();
            var buffer = new byte[64 * 1024];
            long skippedBytes = 0;
            while (sequentialReader.MoveToNextEntry())
            {
                if (sequentialReader.Entry.IsDirectory) continue;
                if (string.Equals(sequentialReader.Entry.Key, memberPath, StringComparison.OrdinalIgnoreCase))
                    return new OwnedRarMemberStream(sequentialReader.OpenEntryStream(), archive, file, sequentialReader);
                using var preceding = sequentialReader.OpenEntryStream();
                int read;
                while ((read = preceding.Read(buffer)) > 0)
                {
                    skippedBytes = checked(skippedBytes + read);
                    if (skippedBytes > UnityArchiveLimits.MaximumDeclaredTotalBytes)
                        throw new InvalidDataException("RAR exceeds the 8 GiB decompressed size limit while locating a solid member.");
                }
            }
            throw new FileNotFoundException($"RAR member was not found: {memberPath}");
        }
        catch
        {
            sequentialReader?.Dispose();
            archive?.Dispose();
            file.Dispose();
            throw;
        }
    }

    public byte[] ReadMember(string memberPath, CancellationToken cancellationToken = default)
        => ReadMember(memberPath, UnityArchiveLimits.MaximumBufferedMemberBytes, cancellationToken);

    public byte[] ReadMember(string memberPath, long maximumBytes, CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 0 or > UnityArchiveLimits.MaximumBufferedMemberBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var input = OpenMember(memberPath, maximumBytes);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("RAR member exceeded its declared safety limit while reading.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private IRarArchive OpenArchive(Stream file)
        => RarArchive.OpenArchive(file, ReaderOptions.ForExternalStream);

    private Stream OpenFile()
    {
        if (_openStream is null)
            return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

        var stream = _openStream();
        if (!stream.CanRead || !stream.CanSeek)
        {
            stream.Dispose();
            throw new InvalidDataException("RAR source stream must be readable and seekable.");
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class OwnedRarMemberStream(Stream member, IRarArchive archive, Stream file, IReader? reader = null) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                member.Dispose();
                reader?.Dispose();
                archive.Dispose();
                file.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => member.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => member.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => member.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => member.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
