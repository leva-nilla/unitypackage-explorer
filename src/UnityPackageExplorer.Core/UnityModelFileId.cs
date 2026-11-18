using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace UnityPackageExplorer.Core;

/// <summary>
/// Modern model-importer identifiers. An identifier is only used after an exact,
/// unique match to a reference in the Prefab; unsupported importer naming schemes
/// therefore remain unresolved rather than becoming a positional guess.
/// These are importer conventions, not a stable Unity API contract:
/// https://discussions.unity.com/t/fbx-submesh-fileids/803882
/// </summary>
public static class UnityModelFileId
{
    public static long Mesh(string name) => Hash($"Type:Mesh->{name}0");
    public static long Renderer(string path, string type) => Hash($"Type:{type}->{path}/{type}0");

    public static long Hash(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        const ulong p1 = 11400714785074694791UL, p2 = 14029467366897019727UL;
        const ulong p3 = 1609587929392839161UL, p4 = 9650029242287828579UL, p5 = 2870177450012600261UL;
        static ulong Round(ulong value, ulong input) => unchecked(BitOperations.RotateLeft(value + input * p2, 31) * p1);
        static ulong Merge(ulong value, ulong input) => unchecked((value ^ Round(0, input)) * p1 + p4);
        var offset = 0;
        ulong hash;
        unchecked
        {
            if (data.Length >= 32)
            {
                ulong v1 = p1 + p2, v2 = p2, v3 = 0, v4 = 0UL - p1;
                do
                {
                    v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)));
                    v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 8)));
                    v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 16)));
                    v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 24)));
                    offset += 32;
                } while (offset <= data.Length - 32);
                hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
                    + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
                hash = Merge(Merge(Merge(Merge(hash, v1), v2), v3), v4);
            }
            else hash = p5;
            hash += (ulong)data.Length;
            while (offset <= data.Length - 8)
            {
                hash ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)));
                hash = BitOperations.RotateLeft(hash, 27) * p1 + p4;
                offset += 8;
            }
            if (offset <= data.Length - 4)
            {
                hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) * p1;
                hash = BitOperations.RotateLeft(hash, 23) * p2 + p3;
                offset += 4;
            }
            while (offset < data.Length)
                hash = BitOperations.RotateLeft(hash ^ data[offset++] * p5, 11) * p1;
            hash ^= hash >> 33;
            hash *= p2;
            hash ^= hash >> 29;
            hash *= p3;
            return (long)(hash ^ (hash >> 32));
        }
    }
}
