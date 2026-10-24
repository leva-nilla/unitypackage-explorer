using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace UnityPackageExplorer.Core;

/// <summary>Reads Unity's gzip/tar package without extracting it beside the source.</summary>
public sealed class UnityPackageReader
{
    private const int MaximumPathNameBytes = 1024 * 1024;
    public const long MaximumAssetBytes = 512L * 1024 * 1024;
    public const long MaximumExpandedArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumEntryCount = UnityArchiveLimits.MaximumEntryCount;
    private readonly Func<Stream> _openStream;
    private readonly long _maximumExpandedArchiveBytes;
    private readonly int _maximumEntryCount;

    public UnityPackageReader(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        _openStream = () => new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        _maximumExpandedArchiveBytes = MaximumExpandedArchiveBytes;
        _maximumEntryCount = MaximumEntryCount;
    }

    public UnityPackageReader(Func<Stream> openStream, long maximumExpandedArchiveBytes = MaximumExpandedArchiveBytes, int maximumEntryCount = MaximumEntryCount)
    {
        _openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
        if (maximumExpandedArchiveBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumExpandedArchiveBytes));
        if (maximumEntryCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntryCount));
        _maximumExpandedArchiveBytes = maximumExpandedArchiveBytes;
        _maximumEntryCount = maximumEntryCount;
    }

    public IReadOnlyList<UnityPackageEntry> ReadIndex(CancellationToken cancellationToken = default, ArchiveExpansionBudget? expansionBudget = null)
    {
        var records = new Dictionary<string, MutableEntry>(StringComparer.OrdinalIgnoreCase);
        using var archive = OpenArchive(expansionBudget);
        using var reader = new TarReader(archive, leaveOpen: false);

        var entryCount = 0;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++entryCount > _maximumEntryCount)
                throw new InvalidDataException($"UnityPackage contains more than {_maximumEntryCount:N0} entries.");
            var archiveName = entry.Name.Replace('\\', '/').TrimEnd('/');
            var pieces = archiveName.Split('/', 2);
            if (pieces.Length == 2 && IsGuid(pieces[0]))
            {
                if (!records.TryGetValue(pieces[0], out var record))
                {
                    record = new MutableEntry(pieces[0]);
                    records.Add(pieces[0], record);
                }

                switch (pieces[1])
                {
                    case "pathname":
                        if (entry.DataStream is not null)
                        {
                            var data = ReadLimited(entry.DataStream, MaximumPathNameBytes, cancellationToken);
                            record.PathName = Encoding.UTF8.GetString(data).Trim('\r', '\n', '\0');
                        }
                        break;
                    case "asset":
                        record.HasAsset = true;
                        break;
                    case "asset.meta":
                        record.HasMeta = true;
                        break;
                }
            }

            Drain(entry.DataStream, cancellationToken);
        }

        return records.Values
            .Where(x => !string.IsNullOrWhiteSpace(x.PathName))
            .Select(x => new UnityPackageEntry(x.Guid, x.PathName!, x.HasAsset, x.HasMeta))
            .OrderBy(x => x.PathName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public byte[]? ReadAsset(string guid, string payloadName = "asset", CancellationToken cancellationToken = default, long maximumBytes = MaximumAssetBytes)
    {
        if (!IsGuid(guid)) throw new ArgumentException("GUID must contain 32 hexadecimal characters.", nameof(guid));
        if (payloadName is not ("asset" or "asset.meta")) throw new ArgumentException("Unsupported Unity package payload.", nameof(payloadName));
        if (maximumBytes is <= 0 or > MaximumAssetBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var target = $"{guid}/{payloadName}";
        using var archive = OpenArchive();
        using var reader = new TarReader(archive, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(entry.Name.Replace('\\', '/').TrimEnd('/'), target, StringComparison.OrdinalIgnoreCase))
            {
                if (entry.DataStream is null) return [];
                if (entry.Length > maximumBytes) throw new InvalidDataException($"Asset exceeds the {maximumBytes:N0} byte safety limit.");
                return ReadLimited(entry.DataStream, maximumBytes, cancellationToken);
            }
            Drain(entry.DataStream, cancellationToken);
        }

        return null;
    }

    public byte[]? ReadAssetAtPath(string pathName, string payloadName = "asset", CancellationToken cancellationToken = default)
    {
        var entry = ReadIndex(cancellationToken).FirstOrDefault(x => string.Equals(x.PathName, pathName, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : ReadAsset(entry.Guid, payloadName, cancellationToken);
    }

    public string ExtractTo(string destination, CancellationToken cancellationToken = default)
    {
        var indexed = ReadIndex(cancellationToken);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in indexed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = GetSafeRelativePath(item.PathName);
            if (item.HasAsset && !destinations.Add(relative)) throw new InvalidDataException($"Duplicate asset destination: {relative}");
            if (item.HasMeta && !destinations.Add(relative + ".meta")) throw new InvalidDataException($"Duplicate asset destination: {relative}.meta");
            paths[item.Guid] = relative;
        }

        var basePath = Path.GetFullPath(destination);
        var parentPath = Path.GetDirectoryName(basePath) ?? throw new InvalidDataException("Extraction destination must have a parent directory.");
        Directory.CreateDirectory(parentPath);
        var destinationName = Path.GetFileName(basePath);
        if (string.IsNullOrWhiteSpace(destinationName)) throw new InvalidDataException("Extraction destination must name a directory.");

        // Build beside the final path so the final directory move stays on one volume. A fresh
        // random staging name belongs only to this extraction request; never clean up a path that
        // was already present before we created and marked it.
        var token = Guid.NewGuid().ToString("N");
        var stagingPath = Path.Combine(parentPath, $".{destinationName}.upx-{token}.tmp");
        var markerName = $".unitypackage-explorer-owner-{token}";
        var markerPath = Path.Combine(stagingPath, markerName);
        var stagingOwned = false;
        try
        {
            if (Directory.Exists(stagingPath) || File.Exists(stagingPath))
                throw new IOException("Could not reserve a unique extraction staging directory.");
            Directory.CreateDirectory(stagingPath);
            using (new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            stagingOwned = true;

            using var archive = OpenArchive();
            using var reader = new TarReader(archive, leaveOpen: false);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pieces = entry.Name.Replace('\\', '/').TrimEnd('/').Split('/', 2);
                if (pieces.Length == 2 && paths.TryGetValue(pieces[0], out var relative))
                {
                    var outputRelative = pieces[1] switch
                    {
                        "asset" => relative,
                        "asset.meta" => relative + ".meta",
                        _ => null
                    };
                    if (outputRelative is not null && entry.DataStream is not null)
                    {
                        if (entry.Length > MaximumAssetBytes) throw new InvalidDataException("Archive asset exceeds the extraction size limit.");
                        var target = Path.GetFullPath(Path.Combine(stagingPath, outputRelative));
                        var rootPrefix = Path.GetFullPath(stagingPath) + Path.DirectorySeparatorChar;
                        if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Asset path escaped the destination root.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        CopyLimited(entry.DataStream, output, MaximumAssetBytes, cancellationToken);
                    }
                }
                Drain(entry.DataStream, cancellationToken);
            }

            var suffix = 1;
            while (true)
            {
                var finalPath = suffix == 1 ? basePath : $"{basePath}-{suffix}";
                if (Directory.Exists(finalPath) || File.Exists(finalPath))
                {
                    suffix++;
                    continue;
                }

                try
                {
                    Directory.Move(stagingPath, finalPath);
                    var finalMarker = Path.Combine(finalPath, markerName);
                    try { File.Delete(finalMarker); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    stagingOwned = false;
                    return finalPath;
                }
                catch (IOException) when (Directory.Exists(finalPath) || File.Exists(finalPath))
                {
                    // Another extraction or user created this destination after the check. Keep
                    // our staging directory and try the next suffix without replacing anything.
                    suffix++;
                }
            }
        }
        catch
        {
            if (stagingOwned && Directory.Exists(stagingPath))
            {
                try { Directory.Delete(stagingPath, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    public static string GetSafeRelativePath(string unityPath)
    {
        if (string.IsNullOrWhiteSpace(unityPath)) throw new InvalidDataException("Asset path is empty.");
        var normalized = unityPath.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException($"Rooted asset paths are not allowed: {unityPath}");

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(x => x is "." or ".." || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"Unsafe asset path: {unityPath}");

        foreach (var part in parts)
        {
            var fileName = Path.GetFileName(part).TrimEnd(' ', '.');
            var firstDot = fileName.IndexOf('.');
            var stem = (firstDot >= 0 ? fileName[..firstDot] : fileName).TrimEnd(' ', '.');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
                throw new InvalidDataException($"Reserved Windows path segment: {part}");
        }

        return Path.Combine(parts);
    }

    public static string? ReadGuid(string? metaText)
    {
        if (metaText is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(metaText, "(?m)^guid:\\s*([0-9a-fA-F]{32})\\s*$");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private Stream OpenArchive(ArchiveExpansionBudget? expansionBudget = null)
    {
        var file = _openStream();
        try
        {
            if (!file.CanRead) throw new InvalidDataException("UnityPackage source must be readable.");

            var signature = new byte[2];
            var signatureLength = 0;
            while (signatureLength < signature.Length)
            {
                var read = file.Read(signature, signatureLength, signature.Length - signatureLength);
                if (read == 0) break;
                signatureLength += read;
            }

            if (signatureLength != signature.Length) throw new InvalidDataException("Package is empty or truncated.");

            Stream source;
            if (file.CanSeek)
            {
                file.Position = 0;
                source = file;
            }
            else
            {
                source = new PrefixReadStream(signature, file);
            }

            var archive = signature[0] == 0x1f && signature[1] == 0x8b
                ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: false)
                : source;
            return new ExpandedLimitStream(archive, expansionBudget ?? new ArchiveExpansionBudget(_maximumExpandedArchiveBytes));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private sealed class PrefixReadStream(byte[] prefix, Stream tail) : Stream
    {
        private int _prefixOffset;
        protected override void Dispose(bool disposing)
        {
            if (disposing) tail.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => tail.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var copied = Math.Min(count, prefix.Length - _prefixOffset);
            if (copied > 0)
            {
                Array.Copy(prefix, _prefixOffset, buffer, offset, copied);
                _prefixOffset += copied;
                return copied;
            }
            return tail.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            var copied = Math.Min(buffer.Length, prefix.Length - _prefixOffset);
            if (copied > 0)
            {
                prefix.AsSpan(_prefixOffset, copied).CopyTo(buffer);
                _prefixOffset += copied;
                return copied;
            }
            return tail.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ExpandedLimitStream(Stream inner, ArchiveExpansionBudget expansionBudget) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Count(read);
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ReadAsyncCore(buffer, cancellationToken);

        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Count(read);
            return read;
        }

        private void Count(int read)
        {
            expansionBudget.Consume(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] ReadLimited(Stream stream, long limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read = stream.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("Archive entry exceeds the safety limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void CopyLimited(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) break;
            total += read;
            if (total > limit) throw new InvalidDataException("Archive entry exceeds the safety limit.");
            destination.Write(buffer, 0, read);
        }
    }

    private static void Drain(Stream? stream, CancellationToken cancellationToken)
    {
        if (stream is null) return;
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) break;
        }
    }

    private static bool IsGuid(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);

    private sealed class MutableEntry(string guid)
    {
        public string Guid { get; } = guid;
        public string? PathName { get; set; }
        public bool HasAsset { get; set; }
        public bool HasMeta { get; set; }
    }
}
