using System.Buffers.Binary;

namespace UnityPackageExplorer.Core;

/// <summary>Checks standard RAR volume end markers that SharpCompress does not expose.</summary>
internal static class RarVolumeFooter
{
    internal static void Check(Stream stream, int index, int count)
    {
        // Some older archives (notably RAR 2.x) lack this marker. In those cases
        // the numbered names and SharpCompress's split-entry checks still apply.
        stream.Position = 6;
        var rar5 = stream.ReadByte() == 1;
        Span<byte> tail = stackalloc byte[64];
        tail = tail[..(int)Math.Min(stream.Length, tail.Length)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        stream.Position = 0;

        for (var offset = 0; offset <= tail.Length - 7; offset++)
        {
            var footer = tail[offset..];
            if (rar5)
            {
                if (footer.Length < 8 || footer[4] != 3 || footer[5] != 5
                    || footer[6] is not (0 or 4) || footer[7] > 1 || footer[8..].ContainsAnyExcept((byte)0)
                    || BinaryPrimitives.ReadUInt32LittleEndian(footer) != Crc32(footer[4..8]))
                    continue;
                if ((footer[7] != 0) != (index < count - 1)) throw RarVolumeResolver.Incomplete();
                return;
            }

            if (footer[2] != 0x7b) continue;
            var size = BinaryPrimitives.ReadUInt16LittleEndian(footer[5..]);
            if (size < 7 || size > footer.Length || footer[size..].ContainsAnyExcept((byte)0)
                || BinaryPrimitives.ReadUInt16LittleEndian(footer) != (ushort)Crc32(footer[2..size]))
                continue;
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(footer[3..]);
            var numberOffset = (flags & 2) != 0 ? 11 : 7;
            if (((flags & 1) != 0) != (index < count - 1)
                || ((flags & 8) != 0 && (size < numberOffset + 2
                    || BinaryPrimitives.ReadUInt16LittleEndian(footer[numberOffset..]) != index)))
                throw RarVolumeResolver.Incomplete();
            return;
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }
}
