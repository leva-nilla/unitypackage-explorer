using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
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
    public bool IsMultiVolume { get; private set; }

    /// <summary>Identifies the older numbered extensions (.r00–.r99) accepted by the app.</summary>
    public static bool IsLegacyVolumePath(string path) => RarVolumeResolver.IsLegacyPart(path);

    public void RebindPath(string path)
    {
        if (_openStream is not null || IsMultiVolume)
            throw new InvalidOperationException("Only a single path-backed RAR can be rebound.");
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<UnityArchiveMember> ReadIndex(CancellationToken cancellationToken = default)
    {
        try
        {
            using var source = OpenArchive(cancellationToken);
            var result = ReadIndex(source, cancellationToken);
            IsMultiVolume = source.IsMultiVolume;
            return result;
        }
        catch (Exception ex) when (IsArchiveError(ex)) { throw DescribeArchiveError(ex); }
    }

    /// <summary>Visits package streams once; a solid archive must be decoded in archive order.</summary>
    internal void VisitBundlePackages(Stream headerSource, Action<UnityArchiveMember, Stream> visit, CancellationToken token)
    {
        try
        {
            using var source = OpenArchive(token, allowSplit: false);
            var archive = source.Archive;
            var packages = ReadIndex(source, token, headerSource)
                .Where(entry => IsPackage(entry.FullName)).ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
            if (packages.Count == 0) return;
            if (!archive.IsSolid)
            {
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.IsDirectory || !packages.TryGetValue(entry.Key!, out var member)) continue;
                    if (member.Length > UnityArchiveLimits.MaximumStreamedMemberBytes)
                        throw MemberTooLarge(member.FullName, UnityArchiveLimits.MaximumStreamedMemberBytes);
                    using var input = entry.OpenEntryStream();
                    visit(member, input);
                }
                return;
            }

            using var reader = archive.ExtractAllEntries();
            var buffer = new byte[64 * 1024];
            long decoded = 0;
            while (packages.Count > 0 && reader.MoveToNextEntry())
            {
                token.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                if (packages.Remove(reader.Entry.Key!, out var member))
                {
                    if (member.Length > UnityArchiveLimits.MaximumStreamedMemberBytes
                        || member.Length > UnityArchiveLimits.MaximumDeclaredTotalBytes - decoded)
                        throw MemberTooLarge(member.FullName, UnityArchiveLimits.MaximumStreamedMemberBytes);
                    using var input = reader.OpenEntryStream();
                    visit(member, input);
                    decoded += member.Length;
                }
                else
                {
                    using var input = reader.OpenEntryStream();
                    DrainSolidMember(input, buffer, ref decoded, token);
                }
            }
            if (packages.Count > 0) throw new InvalidDataException("ソリッドRAR内の .unitypackage を読み取れませんでした。");
        }
        catch (Exception ex) when (IsArchiveError(ex)) { throw DescribeArchiveError(ex); }
    }

    private static bool IsPackage(string name) => name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);

    private static List<UnityArchiveMember> ReadIndex(ArchiveSource source, CancellationToken token, Stream? bundleHeaderSource = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UnityArchiveMember>();
        long declaredTotal = 0;
        var entryCount = 0;
        var hasEncryptedEntry = false;
        HashSet<(int Number, string Name, int Attributes)>? windowsFiles = null;
        foreach (var entry in source.Archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (++entryCount > UnityArchiveLimits.MaximumEntryCount)
                throw new InvalidDataException($"RARの項目数が上限（{UnityArchiveLimits.MaximumEntryCount:N0}）を超えています。");
            if (!entry.IsComplete) throw RarVolumeResolver.Incomplete();
            if (entry.IsDirectory) continue;
            hasEncryptedEntry |= entry.IsEncrypted;
            var key = entry.Key ?? throw new InvalidDataException("RAR内に名前のない項目があります。");
            if (bundleHeaderSource is not null && IsPackage(key))
            {
                var attributes = entry.Attrib ?? 0;
                var unixType = attributes & 0xF000;
                if (entry is RarArchiveEntry { IsRedir: true } || entry.LinkTarget is not null
                    || (attributes & (int)FileAttributes.ReparsePoint) != 0
                    || (unixType is not (0 or 0x8000) && !IsWindowsNotContentIndexed()))
                    throw new InvalidDataException($"RAR内のリンクや特殊ファイルは統合できません: {key}");

                // Windows NotContentIndexed overlaps the Unix character-device type.
                // Only accept this exception when the original RAR5 header confirms Windows.
                bool IsWindowsNotContentIndexed() => unixType == (int)FileAttributes.NotContentIndexed
                    && (attributes & (int)(FileAttributes.Device | FileAttributes.Directory)) == 0
                    && (windowsFiles ??= Rar5FileHosts.ReadWindowsNotContentIndexed(bundleHeaderSource, token))
                        .Contains((entryCount, key, attributes));
            }
            if (!names.Add(key)) throw new InvalidDataException($"RAR内に同名の項目があります: {key}");
            var size = entry.Size;
            if (size < 0 || size > UnityArchiveLimits.MaximumDeclaredTotalBytes - declaredTotal)
                throw new InvalidDataException($"RARの宣言展開サイズが上限（{FormatSize(UnityArchiveLimits.MaximumDeclaredTotalBytes)}）を超えるか、不明です。必要なファイルを取り出して開いてください。");
            declaredTotal += size;
            result.Add(new UnityArchiveMember(key, size, entry.CompressedSize));
        }
        if (source.IsMultiVolume && source.Archive.Volumes.Select((volume, index) => (volume, index))
            .Any(part => part.volume is not RarVolume { IsMultiVolume: true } rar || (part.index > 0 && rar.IsFirstVolume)))
            throw RarVolumeResolver.Incomplete();
        if (hasEncryptedEntry) throw PasswordRequired();
        return result;
    }

    public Stream OpenMember(string memberPath, long maximumBytes = UnityArchiveLimits.MaximumStreamedMemberBytes)
        => OpenMember(memberPath, maximumBytes, default);

    internal Stream OpenMember(string memberPath, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 0 or > UnityArchiveLimits.MaximumStreamedMemberBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ArchiveSource? source = null;
        IReader? sequentialReader = null;
        try
        {
            source = OpenArchive(cancellationToken);
            var archive = source.Archive;
            var entry = archive.Entries.FirstOrDefault(candidate =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !candidate.IsDirectory && string.Equals(candidate.Key, memberPath, StringComparison.OrdinalIgnoreCase);
            }) ?? throw new FileNotFoundException($"RAR内の項目が見つかりません: {memberPath}");
            if (entry.IsEncrypted) throw PasswordRequired();
            if (entry.Size < 0 || entry.Size > maximumBytes) throw MemberTooLarge(memberPath, maximumBytes);
            if (!archive.IsSolid)
                return new OwnedRarMemberStream(entry.OpenEntryStream(), source, null, memberPath, maximumBytes, cancellationToken);

            // Solid members share the decompressor dictionary with earlier files.
            sequentialReader = archive.ExtractAllEntries();
            var buffer = new byte[64 * 1024];
            long skippedBytes = 0;
            while (sequentialReader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sequentialReader.Entry.IsDirectory) continue;
                if (sequentialReader.Entry.IsEncrypted) throw PasswordRequired();
                if (string.Equals(sequentialReader.Entry.Key, memberPath, StringComparison.OrdinalIgnoreCase))
                    return new OwnedRarMemberStream(sequentialReader.OpenEntryStream(), source, sequentialReader, memberPath, maximumBytes, cancellationToken);
                using var preceding = sequentialReader.OpenEntryStream();
                DrainSolidMember(preceding, buffer, ref skippedBytes, cancellationToken);
            }
            throw new FileNotFoundException($"RAR内の項目が見つかりません: {memberPath}");
        }
        catch (Exception ex)
        {
            try { sequentialReader?.Dispose(); }
            finally { source?.Dispose(); }
            if (IsArchiveError(ex)) throw DescribeArchiveError(ex);
            throw;
        }
    }

    public byte[] ReadMember(string memberPath, CancellationToken cancellationToken = default)
        => ReadMember(memberPath, UnityArchiveLimits.MaximumBufferedMemberBytes, cancellationToken);

    public byte[] ReadMember(string memberPath, long maximumBytes, CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 0 or > UnityArchiveLimits.MaximumBufferedMemberBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var input = OpenMember(memberPath, maximumBytes, cancellationToken);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void DrainSolidMember(Stream member, byte[] buffer, ref long decoded, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = member.Read(buffer, 0, buffer.Length);
            if (read == 0) return;
            decoded += read;
            if (decoded > UnityArchiveLimits.MaximumDeclaredTotalBytes)
                throw new InvalidDataException($"ソリッドRARの先行項目が展開上限（{FormatSize(UnityArchiveLimits.MaximumDeclaredTotalBytes)}）を超えています。");
        }
    }

    private ArchiveSource OpenArchive(CancellationToken token, bool allowSplit = true)
    {
        token.ThrowIfCancellationRequested();
        var source = new ArchiveSource();
        try
        {
            source.Add(_openStream is null ? OpenFile(_path) : _openStream());
            source.Open();
            // Pull one entry first: SharpCompress also populates the volume header in this pass.
            // In particular, do not ask for an incomplete entry's Size during this probe.
            using (var entries = source.Archive.Entries.GetEnumerator()) entries.MoveNext();
            if (source.Archive.Volumes.First() is not RarVolume { IsMultiVolume: true })
                return source;
            if (!allowSplit)
                throw new InvalidDataException("分割RARは統合ZIPの対象外です。単体RARまたはZIPを選んでください。");
            if (_openStream is not null)
                throw new InvalidDataException("分割RARはこのプレビューでは表示できません。すべての分割ファイルを同じフォルダーに置き、UnityPackage Explorerで開いてください。");

            source.Dispose();
            source = new ArchiveSource();
            var paths = RarVolumeResolver.Resolve(_path, token);
            for (var index = 0; index < paths.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var stream = source.Add(OpenFile(paths[index]));
                if (!RarArchive.IsRarFile(stream))
                    throw new InvalidDataException($"分割ファイルをRARとして認識できません: {Path.GetFileName(paths[index])}");
                stream.Position = 0;
                RarVolumeFooter.Check(stream, index, paths.Count);
            }
            source.Open();
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static Stream OpenFile(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);

    private static string FormatSize(long bytes) =>
        bytes % (1024L * 1024 * 1024) == 0 && bytes > 0 ? $"{bytes / (1024L * 1024 * 1024):N0} GiB"
        : bytes % (1024 * 1024) == 0 && bytes > 0 ? $"{bytes / (1024 * 1024):N0} MiB" : $"{bytes:N0} バイト";

    private static InvalidDataException MemberTooLarge(string memberPath, long maximumBytes) =>
        new($"RAR内の項目が読込み上限（{FormatSize(maximumBytes)}）を超えています: {memberPath}");

    private static InvalidDataException PasswordRequired(Exception? cause = null) =>
        new("暗号化されたRARには対応していません。信頼できる展開ソフトで必要なファイルを取り出して開いてください。", cause);

    private static bool IsArchiveError(Exception exception) =>
        exception is SharpCompressException or EndOfStreamException or OverflowException;

    private static InvalidDataException DescribeArchiveError(Exception cause) => cause switch
    {
        CryptographicException => PasswordRequired(cause),
        IncompleteArchiveException or MultipartStreamRequiredException or MultiVolumeExtractionException
            => new InvalidDataException(RarVolumeResolver.Incomplete().Message, cause),
        _ => new InvalidDataException("RARを読み込めません。ファイルが破損しているか、対応していない形式です。", cause)
    };

    private sealed class ArchiveSource : IDisposable
    {
        private readonly List<Stream> _streams = [];
        private IRarArchive? _archive;

        public IRarArchive Archive => _archive!;
        public bool IsMultiVolume => _streams.Count > 1;

        public Stream Add(Stream stream)
        {
            _streams.Add(stream);
            if (!stream.CanRead || !stream.CanSeek)
                throw new InvalidDataException("RARの入力には読み込みとシークが必要です。");
            stream.Position = 0;
            return stream;
        }

        public void Open() => _archive = RarArchive.OpenArchive(_streams, ReaderOptions.ForExternalStream);

        public void Dispose()
        {
            try { _archive?.Dispose(); }
            finally
            {
                foreach (var stream in _streams) stream.Dispose();
                _streams.Clear();
                _archive = null;
            }
        }
    }

    private sealed class OwnedRarMemberStream(Stream member, ArchiveSource source, IReader? reader,
        string memberPath, long maximumBytes, CancellationToken token) : Stream
    {
        private long _read;
        private bool _disposed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try { member.Dispose(); }
                finally
                {
                    try { reader?.Dispose(); }
                    finally { source.Dispose(); }
                }
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => !_disposed && member.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            try { return Account(member.Read(buffer[..ReadSize(buffer.Length)])); }
            catch (Exception ex) when (IsArchiveError(ex)) { throw DescribeArchiveError(ex); }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            try { return Account(await member.ReadAsync(buffer[..ReadSize(buffer.Length)], linked.Token).ConfigureAwait(false)); }
            catch (Exception ex) when (IsArchiveError(ex)) { throw DescribeArchiveError(ex); }
        }

        private int ReadSize(int count)
        {
            if (_read > maximumBytes) throw MemberTooLarge(memberPath, maximumBytes);
            return (int)Math.Min(count, maximumBytes - _read + 1);
        }

        private int Account(int count)
        {
            _read += count;
            if (_read > maximumBytes) throw MemberTooLarge(memberPath, maximumBytes);
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
