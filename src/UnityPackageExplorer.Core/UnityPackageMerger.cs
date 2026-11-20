using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

/// <summary>Combines Unity asset records without extracting or rewriting their contents.</summary>
internal sealed class UnityPackageMerger(FileStream spool, byte[] buffer)
{
    private const int MaximumTextBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ArchiveExpansionBudget _inputBudget = new(UnityPackageReader.MaximumExpandedArchiveBytes);
    private readonly Dictionary<string, AssetRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private int _inputEntries;

    private sealed record Slice(long Offset, long Length, byte[] Hash)
    {
        public bool Matches(Slice other) => Length == other.Length && Hash.AsSpan().SequenceEqual(other.Hash);
    }

    private sealed record AssetRecord(string Path, bool IsFolder, Dictionary<string, Slice> Parts);

    internal void AddPackage(FileStream package, string name, CancellationToken token)
    {
        try
        {
            using var source = new SliceStream(package, 0, package.Length, token);
            using var expanded = UnityPackageReader.OpenExpandedArchive(source, _inputBudget);
            using var tar = new TarReader(expanded, leaveOpen: true);
            var incoming = new Dictionary<string, Dictionary<string, Slice>>(StringComparer.OrdinalIgnoreCase);
            while (tar.GetNextEntry(copyData: false) is { } entry)
            {
                token.ThrowIfCancellationRequested();
                if (++_inputEntries > UnityArchiveLimits.MaximumEntryCount)
                    throw new InvalidDataException("統合するUnityPackageの項目数が上限を超えています。");
                var path = entry.Name.Replace('\\', '/');
                if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
                if (entry.EntryType == TarEntryType.Directory && (path is "" or "." || IsGuid(path.TrimEnd('/')))) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                    throw new InvalidDataException($"UnityPackage内のリンクや特殊項目は統合できません: {path}");
                if (entry.Length < 0 || entry.Length > UnityPackageReader.MaximumAssetBytes)
                    throw new InvalidDataException($"UnityPackage内の項目のサイズが上限を超えています: {path}");

                if (path == ".icon.png")
                {
                    // This icon identifies the original package, not an asset with a GUID.
                    ArchiveBundleService.CopyAndHash(entry.DataStream ?? Stream.Null, Stream.Null, entry.Length, token, buffer);
                    continue;
                }
                var segments = path.Split('/');
                if (segments.Length != 2 || !IsGuid(segments[0])
                    || segments[1] is not ("pathname" or "asset" or "asset.meta" or "preview.png"))
                    throw new InvalidDataException($"対応していないUnityPackage内の項目です: {path}");
                var guid = segments[0].ToLowerInvariant();
                var part = segments[1];
                if (part is ("pathname" or "asset.meta") && entry.Length > MaximumTextBytes)
                    throw new InvalidDataException($"UnityPackageのパスまたはメタデータが大きすぎます: {path}");
                if (!incoming.TryGetValue(guid, out var parts)) incoming.Add(guid, parts = new(StringComparer.Ordinal));
                if (parts.ContainsKey(part))
                    throw new InvalidDataException($"UnityPackage内で同じ項目が重複しています: {path}");
                var offset = spool.Position;
                var (length, hash) = ArchiveBundleService.CopyAndHash(entry.DataStream ?? Stream.Null, spool, entry.Length, token, buffer);
                parts.Add(part, new(offset, length, hash));
            }
            DrainPadding(expanded, token);
            if (incoming.Count == 0) throw new InvalidDataException("素材が入っていません。");
            foreach (var (guid, parts) in incoming)
            {
                token.ThrowIfCancellationRequested();
                AddRecord(guid, parts);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or EndOfStreamException)
        {
            throw new InvalidDataException($"{name}: {ex.Message}", ex);
        }
    }

    private void AddRecord(string guid, Dictionary<string, Slice> parts)
    {
        if (!parts.TryGetValue("pathname", out var pathname) || !parts.TryGetValue("asset.meta", out var metadata))
            throw new InvalidDataException($"GUID {guid} のパスまたは .meta がありません。");
        var path = ReadText(pathname).TrimStart('\uFEFF').TrimEnd('\r', '\n', '\0').Replace('\\', '/');
        // Refuse Windows path aliases before comparing destinations case-insensitively.
        var pathParts = path.Split('/');
        if (path.Length > 4096 || pathParts.Length > 128)
            throw new InvalidDataException($"素材パスが長すぎます: {path}");
        foreach (var part in pathParts) ArchiveBundleService.ValidateLeafName(part);
        UnityPackageReader.GetSafeRelativePath(path);

        var meta = ReadText(metadata).TrimStart('\uFEFF');
        if (!string.Equals(UnityPackageReader.ReadGuid(meta), guid, StringComparison.Ordinal))
            throw new InvalidDataException($".meta のGUIDが一致しません: {path} ({guid})");
        var folder = Regex.IsMatch(meta, @"(?m)^folderAsset:[ \t]*yes[ \t]*\r?$");
        parts.TryGetValue("asset", out var asset);
        if ((folder && asset is { Length: > 0 }) || (!folder && asset is null))
            throw new InvalidDataException($"素材とフォルダーの情報が一致しません: {path}");

        if (!_records.TryGetValue(guid, out var previous))
        {
            _records.Add(guid, new(path, folder, parts));
            return;
        }

        previous.Parts.TryGetValue("asset", out var previousAsset);
        if (!string.Equals(previous.Path, path, StringComparison.Ordinal) || previous.IsFolder != folder
            || !previous.Parts["asset.meta"].Matches(metadata)
            || (!folder && (asset is null || previousAsset is null || !previousAsset.Matches(asset))))
            throw new InvalidDataException($"同じGUIDに異なる素材またはパスがあります: {guid}\n{previous.Path}\n{path}");
        // An optional preview does not change the material/Prefab referenced by this GUID.
        if (parts.TryGetValue("preview.png", out var preview)) previous.Parts.TryAdd("preview.png", preview);
    }

    internal void WriteAndVerify(FileStream output, CancellationToken token)
    {
        ValidateDestinations(token);
        spool.Flush();
        var manifest = new Dictionary<string, Slice>(StringComparer.Ordinal);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var (guid, record) in _records)
            foreach (var (part, slice) in record.Parts)
            {
                token.ThrowIfCancellationRequested();
                var name = guid + "/" + part;
                manifest.Add(name, slice);
                using var payload = new SliceStream(spool, slice.Offset, slice.Length, token);
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = payload });
            }
        }
        output.Flush();
        using var written = new SliceStream(output, 0, output.Length, token);
        using var expanded = UnityPackageReader.OpenExpandedArchive(written, new(UnityPackageReader.MaximumExpandedArchiveBytes));
        using var tar = new TarReader(expanded, leaveOpen: true);
        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            token.ThrowIfCancellationRequested();
            if (entry.EntryType != TarEntryType.RegularFile || !manifest.Remove(entry.Name, out var slice) || entry.Length != slice.Length)
                throw new InvalidDataException("統合したUnityPackageの項目が一致しません。");
            var (_, hash) = ArchiveBundleService.CopyAndHash(entry.DataStream ?? Stream.Null, Stream.Null, entry.Length, token, buffer);
            if (!slice.Hash.AsSpan().SequenceEqual(hash))
                throw new InvalidDataException($"統合したUnityPackageの内容が一致しません: {entry.Name}");
        }
        if (manifest.Count > 0) throw new InvalidDataException("統合したUnityPackageに不足している項目があります。");
        DrainPadding(expanded, token);
    }

    private void ValidateDestinations(CancellationToken token)
    {
        var outputs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _records.Values)
        {
            token.ThrowIfCancellationRequested();
            if (!outputs.TryAdd(record.Path, !record.IsFolder))
                throw Collision(record.Path);
            if (!outputs.TryAdd(record.Path + ".meta", true))
                throw Collision(record.Path + ".meta");
        }
        foreach (var path in outputs.Keys)
        {
            token.ThrowIfCancellationRequested();
            for (var end = path.LastIndexOf('/'); end > 0; end = path.LastIndexOf('/', end - 1))
            {
                if (outputs.TryGetValue(path[..end], out var isFile) && isFile)
                    throw Collision(path[..end]);
            }
        }
    }

    private static InvalidDataException Collision(string path) => new($"異なる素材のパスが重なっています: {path}");

    private string ReadText(Slice slice)
    {
        var saved = spool.Position;
        try
        {
            var bytes = new byte[checked((int)slice.Length)];
            spool.Position = slice.Offset;
            spool.ReadExactly(bytes);
            return StrictUtf8.GetString(bytes);
        }
        finally { spool.Position = saved; }
    }

    private void DrainPadding(Stream expanded, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = expanded.Read(buffer, 0, buffer.Length);
            if (read == 0) return;
            if (buffer.AsSpan(0, read).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("UnityPackageの末尾に対応していないデータがあります。");
        }
    }

    private static bool IsGuid(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);

    /// <summary>Read-only bounds over a scratch file; the caller owns the underlying file.</summary>
    private sealed class SliceStream(FileStream file, long offset, long length, CancellationToken token) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> destination)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(destination.Length, length - _position);
            if (count == 0) return 0;
            file.Position = offset + _position;
            var read = file.Read(destination[..count]);
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = checked(offset + (origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            }));
            if (next < 0 || next > length) throw new IOException("Scratch stream seek is out of range.");
            return _position = next;
        }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
