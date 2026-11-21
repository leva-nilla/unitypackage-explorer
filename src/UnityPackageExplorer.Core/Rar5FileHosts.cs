using System.Text;

namespace UnityPackageExplorer.Core;

/// <summary>Reads the RAR5 host OS field, which SharpCompress does not expose.</summary>
internal static class Rar5FileHosts
{
    internal static HashSet<(int Number, string Name, int Attributes)> ReadWindowsNotContentIndexed(
        Stream stream, CancellationToken token)
    {
        // Read only the standard headers, not quick-open copies or packed data.
        // https://www.rarlab.com/technote.htm
        var matches = new HashSet<(int Number, string Name, int Attributes)>();
        ReadOnlySpan<byte> rar5Signature = "Rar!\x1a\x07\x01\0"u8;
        stream.Position = 0;
        var length = stream.Length;
        if (length < rar5Signature.Length) return matches;
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(rar5Signature)) return matches;

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var fileNumber = 0;
        var blocks = 0;
        while (stream.Position < length)
        {
            token.ThrowIfCancellationRequested();
            if (++blocks > UnityArchiveLimits.MaximumEntryCount || length - stream.Position < 4)
                throw InvalidHeader();

            // SharpCompress validates the CRC when reading the same file header.
            stream.Position += 4;
            var size = (long)ReadVInt(reader, length, maxBytes: 3);
            if (size > length - stream.Position) throw InvalidHeader();
            var headerEnd = stream.Position + size;
            var type = ReadVInt(reader, headerEnd);
            if (type == 5) break; // end of archive
            var flags = ReadVInt(reader, headerEnd);
            var extraSize = (flags & 1) != 0 ? ReadVInt(reader, headerEnd) : 0;
            var dataSize = (flags & 2) != 0 ? ReadVInt(reader, headerEnd) : 0;
            if (extraSize > (ulong)(headerEnd - stream.Position) || dataSize > (ulong)(length - headerEnd))
                throw InvalidHeader();

            if (type == 2) ReadFileHeader(headerEnd - (long)extraSize, ++fileNumber);
            stream.Position = headerEnd + (long)dataSize;
        }
        return matches;

        void ReadFileHeader(long end, int number)
        {
            var flags = ReadVInt(reader, end);
            _ = ReadVInt(reader, end); // unpacked size
            var attributes = (int)checked((uint)ReadVInt(reader, end));
            if ((attributes & (int)FileAttributes.NotContentIndexed) == 0) return;
            var optionalBytes = ((flags & 2) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 4 : 0);
            if (end - stream.Position < optionalBytes) throw InvalidHeader();
            stream.Position += optionalBytes; // mtime and data CRC32
            _ = ReadVInt(reader, end); // compression information
            if (ReadVInt(reader, end) != 0) return; // Windows host OS
            var nameLength = ReadVInt(reader, end);
            if (nameLength > ushort.MaxValue || nameLength > (ulong)(end - stream.Position))
                throw InvalidHeader();
            var name = Encoding.UTF8.GetString(reader.ReadBytes((int)nameLength));
            // Match SharpCompress's normalization so the host applies to the same entry.
            if (Path.DirectorySeparatorChar == '\\') name = name.Replace('\\', '-').Replace('/', '\\');
            matches.Add((number, name, attributes));
        }
    }

    private static ulong ReadVInt(BinaryReader reader, long end, int maxBytes = 10)
    {
        ulong value = 0;
        for (var index = 0; index < maxBytes && reader.BaseStream.Position < end; index++)
        {
            var next = reader.ReadByte();
            if (index == 9 && next > 1) break;
            value |= (ulong)(next & 0x7f) << (index * 7);
            if (next < 0x80) return value;
        }
        throw InvalidHeader();
    }

    private static InvalidDataException InvalidHeader() => new("RAR5内のファイル属性を安全に確認できませんでした。");
}
