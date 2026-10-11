using System.IO.Compression;

namespace UnityPackageExplorer.Core;

public sealed record UnityZipMember(string FullName, long Length, long CompressedLength);

/// <summary>Reads ZIP metadata and individual members without extracting the archive.</summary>
public sealed class UnityZipReader
{
    /// <summary>Maximum size for a ZIP member returned as a byte array.</summary>
    public const long MaximumMemberBytes = 512L * 1024 * 1024;
    /// <summary>Maximum size for a ZIP member consumed incrementally as a stream.</summary>
    public const long MaximumStreamedMemberBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumEntryCount = 100_000;
    private const long MaximumDeclaredTotalBytes = MaximumStreamedMemberBytes;
    private string _path;
    private readonly Func<Stream>? _openStream;

    public UnityZipReader(string path) => _path = Path.GetFullPath(path);

    public UnityZipReader(Func<Stream> openStream)
    {
        _path = string.Empty;
        _openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
    }

    public void RebindPath(string path)
    {
        if (_openStream is not null) throw new InvalidOperationException("A stream-backed ZIP reader cannot be rebound to a file path.");
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<UnityZipMember> ReadIndex(CancellationToken cancellationToken = default)
    {
        using var file = OpenFile();
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntryCount)
            throw new InvalidDataException($"ZIP contains more than {MaximumEntryCount:N0} entries.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        var result = new List<UnityZipMember>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(entry.FullName)) throw new InvalidDataException($"ZIP contains a duplicate member name: {entry.FullName}");
            var maximumMemberBytes = entry.FullName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)
                ? MaximumStreamedMemberBytes
                : MaximumMemberBytes;
            var limitDescription = maximumMemberBytes == MaximumMemberBytes ? "read" : "streaming";
            if (entry.Length > maximumMemberBytes)
                throw new InvalidDataException($"ZIP member exceeds the {maximumMemberBytes / 1024 / 1024} MiB {limitDescription} limit: {entry.FullName}");
            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > MaximumDeclaredTotalBytes) throw new InvalidDataException("ZIP declares more than 8 GiB of uncompressed content.");
            result.Add(new UnityZipMember(entry.FullName, entry.Length, entry.CompressedLength));
        }
        return result;
    }

    public Stream OpenMember(string memberPath, long maximumBytes = MaximumMemberBytes)
        => OpenMember(memberPath, maximumBytes, "streaming");

    private Stream OpenMember(string memberPath, long maximumBytes, string limitDescription)
    {
        if (maximumBytes is < 0 or > MaximumStreamedMemberBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var file = OpenFile();
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.GetEntry(memberPath) ?? throw new FileNotFoundException($"ZIP member was not found: {memberPath}");
            if (entry.Length > maximumBytes) throw new InvalidDataException($"ZIP member exceeds the {maximumBytes / 1024 / 1024} MiB {limitDescription} limit: {memberPath}");
            return new OwnedZipMemberStream(entry.Open(), archive, file, maximumBytes, limitDescription);
        }
        catch
        {
            archive?.Dispose();
            file.Dispose();
            throw;
        }
    }

    public byte[] ReadMember(string memberPath, CancellationToken cancellationToken = default)
        => ReadMember(memberPath, MaximumMemberBytes, cancellationToken);

    public byte[] ReadMember(string memberPath, long maximumBytes, CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 0 or > MaximumMemberBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var input = OpenMember(memberPath, maximumBytes, "read");
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length + read > maximumBytes) throw new InvalidDataException("ZIP member exceeded its declared safety limit while reading.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private Stream OpenFile()
    {
        if (_openStream is null)
            return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

        var stream = _openStream();
        if (!stream.CanRead || !stream.CanSeek)
        {
            stream.Dispose();
            throw new InvalidDataException("ZIP source stream must be readable and seekable.");
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class OwnedZipMemberStream(Stream member, ZipArchive archive, Stream file, long maximumBytes, string limitDescription) : Stream
    {
        private long _bytesRead;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                member.Dispose();
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
        public override int Read(byte[] buffer, int offset, int count) => ValidateRead(member.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => ValidateRead(member.Read(buffer));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ValidateRead(await member.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValidateRead(await member.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ValidateRead(int read)
        {
            if (read > 0)
            {
                _bytesRead += read;
                if (_bytesRead > maximumBytes)
                    throw new InvalidDataException($"ZIP member exceeded its {maximumBytes / 1024 / 1024} MiB {limitDescription} limit while reading.");
            }
            return read;
        }
    }
}
