using System.Text;
using System.IO.Compression;
using System.Buffers.Binary;

namespace UnityPackageExplorer.Core;

/// <summary>Source FBX model identity and its declared local material-slot connections.</summary>
public sealed record FbxModelBinding(string Name, string Path, string GeometryName, IReadOnlyList<string> MaterialSlots)
{
    public string RendererPath { get; init; } = "";
}

/// <summary>
/// Reads only the object identifiers/names and OO connections of a binary FBX.
/// Vertex/animation arrays are skipped. Only bounded per-polygon material indices are decoded.
/// Unsupported/malformed layouts yield no bindings, rather than a partial slot guess.
/// </summary>
public static class FbxMaterialLayout
{
    private const int MaximumRecords = 200_000;
    private const int MaximumStringBytes = 16_384;

    public static IReadOnlyList<FbxModelBinding> Read(string path, CancellationToken token = default)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, token);
    }

    public static IReadOnlyList<FbxModelBinding> Read(Stream stream, CancellationToken token = default)
    {
        if (!stream.CanSeek) return [];
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 27) return [];
            stream.Position = 0;
            if (!reader.ReadBytes(23).AsSpan().SequenceEqual("Kaydara FBX Binary  \0\x1a\0"u8)) return [];
            var wide = reader.ReadUInt32() >= 7500;
            var objects = new Dictionary<long, SourceObject>();
            var connections = new List<(long Child, long Parent)>();
            var geometrySlots = new Dictionary<long, IReadOnlyList<int>>();
            var remainingArrayBytes = 16 * 1024 * 1024;
            var remainingStringBytes = 16 * 1024 * 1024;
            var records = 0;
            while (stream.Position < stream.Length)
            {
                token.ThrowIfCancellationRequested();
                var root = ReadRecord(reader, wide, stream.Length, ref records);
                if (root is null) break;
                if (root.Value.Name is "Objects" or "Connections")
                {
                    stream.Position = root.Value.Children;
                    while (stream.Position < root.Value.End)
                    {
                        token.ThrowIfCancellationRequested();
                        var child = ReadRecord(reader, wide, root.Value.End, ref records);
                        if (child is null) break;
                        if (root.Value.Name == "Objects" && child.Value.Name is "Model" or "Geometry" or "Material")
                        {
                            stream.Position = child.Value.Properties;
                            var id = ReadProperty(reader, child.Value.Children, ref remainingStringBytes);
                            var name = ReadProperty(reader, child.Value.Children, ref remainingStringBytes) as string;
                            var type = ReadProperty(reader, child.Value.Children, ref remainingStringBytes) as string;
                            if (id is not long key || name is null || type is null || objects.ContainsKey(key))
                                throw new InvalidDataException("Invalid or duplicate FBX object identity.");
                            objects.Add(key, new SourceObject(child.Value.Name, CleanName(name), type));
                            if (child.Value.Name == "Geometry" && type == "Mesh")
                                geometrySlots[key] = ReadGeometrySlots(reader, child.Value, wide, ref records, ref remainingArrayBytes, ref remainingStringBytes, token);
                        }
                        else if (root.Value.Name == "Connections" && child.Value.Name == "C")
                        {
                            stream.Position = child.Value.Properties;
                            var kind = ReadProperty(reader, child.Value.Children, ref remainingStringBytes);
                            var from = ReadProperty(reader, child.Value.Children, ref remainingStringBytes);
                            var to = ReadProperty(reader, child.Value.Children, ref remainingStringBytes);
                            if (kind is "OO" && from is long fromId && to is long toId) connections.Add((fromId, toId));
                        }
                        stream.Position = child.Value.End;
                    }
                }
                stream.Position = root.Value.End;
            }

            var parents = new Dictionary<long, long>();
            var geometry = new Dictionary<long, List<long>>();
            var materials = new Dictionary<long, List<long>>();
            foreach (var (child, parent) in connections)
            {
                if (!objects.TryGetValue(child, out var source) || !objects.TryGetValue(parent, out var target)) continue;
                if (target.Kind != "Model") continue;
                if (source.Kind == "Material")
                {
                    if (!materials.TryGetValue(parent, out var slots)) materials[parent] = slots = [];
                    slots.Add(child); // This is the FBX's declared slot order, not importer traversal order.
                }
                else if (source.Kind == "Geometry")
                {
                    if (!geometry.TryGetValue(parent, out var meshes)) geometry[parent] = meshes = [];
                    meshes.Add(child);
                }
                else if (source.Kind == "Model" && !parents.TryAdd(child, parent))
                    throw new InvalidDataException("FBX model has multiple parents.");
            }
            string ModelPath(long id)
            {
                var segments = new List<string>();
                var visited = new HashSet<long>();
                while (objects.TryGetValue(id, out var item))
                {
                    if (!visited.Add(id) || segments.Count >= 128) throw new InvalidDataException("Invalid FBX model hierarchy.");
                    segments.Add(item.Name);
                    if (!parents.TryGetValue(id, out id)) break;
                }
                segments.Reverse();
                return string.Join("/", segments);
            }
            var result = new List<FbxModelBinding>();
            var singleRoot = objects.Count(pair => pair.Value.Kind == "Model" && !parents.ContainsKey(pair.Key)) == 1;
            foreach (var pair in geometry.Where(pair => pair.Value.Count == 1 && objects[pair.Key].Type == "Mesh"))
            {
                var declared = materials.TryGetValue(pair.Key, out var connectionsForModel) ? connectionsForModel : [];
                var used = geometrySlots.GetValueOrDefault(pair.Value[0]) ?? [];
                // Unity's generated submeshes follow first use in the polygon material-index
                // layer. Connection order alone is NOT that submesh order (unused slots remain).
                var slots = used.Count > 0 && used.All(index => index >= 0 && index < declared.Count)
                    ? used.Select(index => objects[declared[index]].Name).ToArray() : [];
                var path = ModelPath(pair.Key);
                var slash = path.IndexOf('/');
                // ModelImporter renames a single FBX root, but inserts a root
                // above multiple source roots. Never try both hash candidates.
                var rendererPath = singleRoot
                    ? "//RootNode/root" + (slash < 0 ? "" : path[slash..])
                    : "//RootNode/root/" + path;
                result.Add(new FbxModelBinding(objects[pair.Key].Name, path, objects[pair.Value[0]].Name, slots)
                { RendererPath = rendererPath });
            }
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        {
            return [];
        }
    }

    private static IReadOnlyList<int> ReadGeometrySlots(BinaryReader reader, Record geometry, bool wide,
        ref int records, ref int remainingBytes, ref int remainingStringBytes, CancellationToken token)
    {
        var stream = reader.BaseStream;
        stream.Position = geometry.Children;
        IReadOnlyList<int> result = [];
        var layers = 0;
        while (stream.Position < geometry.End)
        {
            token.ThrowIfCancellationRequested();
            var child = ReadRecord(reader, wide, geometry.End, ref records);
            if (child is null) break;
            if (child.Value.Name == "LayerElementMaterial")
            {
                if (++layers > 1) return []; // Multiple material layers need a separate interpretation.
                string? mapping = null, reference = null;
                int[]? indices = null;
                stream.Position = child.Value.Children;
                while (stream.Position < child.Value.End)
                {
                    var field = ReadRecord(reader, wide, child.Value.End, ref records);
                    if (field is null) break;
                    stream.Position = field.Value.Properties;
                    if (field.Value.Name == "MappingInformationType") mapping = ReadProperty(reader, field.Value.Children, ref remainingStringBytes) as string;
                    else if (field.Value.Name == "ReferenceInformationType") reference = ReadProperty(reader, field.Value.Children, ref remainingStringBytes) as string;
                    else if (field.Value.Name == "Materials") indices = ReadIndices(reader, field.Value.Children, ref remainingBytes, token);
                    stream.Position = field.Value.End;
                }
                if (mapping is "ByPolygon" or "AllSame" && reference == "IndexToDirect" && indices is not null)
                    result = indices.Distinct().ToArray();
            }
            stream.Position = child.Value.End;
        }
        return result;
    }

    private static int[] ReadIndices(BinaryReader reader, long limit, ref int remainingBytes, CancellationToken token)
    {
        var stream = reader.BaseStream;
        if (limit - stream.Position < 13 || reader.ReadByte() != 'i') throw new InvalidDataException("Invalid FBX material indices.");
        var length = reader.ReadInt32();
        var encoding = reader.ReadInt32();
        var stored = reader.ReadInt32();
        var bytes = checked(length * sizeof(int));
        if (length < 0 || bytes > remainingBytes || stored < 0 || stored > 8 * 1024 * 1024
            || stored > limit - stream.Position || encoding is not (0 or 1))
            throw new InvalidDataException("FBX material-index budget exceeded.");
        remainingBytes -= bytes;
        var data = new byte[bytes];
        if (encoding == 0)
        {
            if (stored != bytes) throw new InvalidDataException("Invalid raw FBX array size.");
            stream.ReadExactly(data);
        }
        else
        {
            using var compressed = new MemoryStream(reader.ReadBytes(stored));
            using var decoded = new ZLibStream(compressed, CompressionMode.Decompress);
            for (var offset = 0; offset < bytes;)
            {
                token.ThrowIfCancellationRequested();
                var read = decoded.Read(data, offset, Math.Min(65536, bytes - offset));
                if (read == 0) throw new InvalidDataException("Truncated FBX array.");
                offset += read;
            }
            if (decoded.ReadByte() != -1) throw new InvalidDataException("FBX array exceeds declared size.");
        }
        var values = new int[length];
        for (var i = 0; i < length; i++) values[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4));
        return values;
    }

    private static Record? ReadRecord(BinaryReader reader, bool wide, long limit, ref int count)
    {
        var stream = reader.BaseStream;
        var header = wide ? 25 : 13;
        if (limit - stream.Position < header) throw new InvalidDataException("Truncated FBX record.");
        var end = wide ? checked((long)reader.ReadUInt64()) : reader.ReadUInt32();
        var properties = wide ? reader.ReadUInt64() : reader.ReadUInt32();
        var bytes = wide ? checked((long)reader.ReadUInt64()) : reader.ReadUInt32();
        var nameLength = reader.ReadByte();
        if (end == 0)
        {
            if (properties != 0 || bytes != 0 || nameLength != 0) throw new InvalidDataException("Invalid FBX terminator.");
            return null;
        }
        if (++count > MaximumRecords || end > limit || end <= stream.Position || nameLength > end - stream.Position
            || bytes > end - stream.Position - nameLength || properties > (ulong)bytes)
            throw new InvalidDataException("Invalid FBX record bounds.");
        var name = Encoding.ASCII.GetString(reader.ReadBytes(nameLength));
        return new Record(name, stream.Position, checked(stream.Position + bytes), end);
    }

    private static object ReadProperty(BinaryReader reader, long limit, ref int remainingStringBytes)
    {
        var stream = reader.BaseStream;
        if (limit <= stream.Position) throw new InvalidDataException("Missing FBX property.");
        var kind = reader.ReadByte();
        if (kind == 'L' && limit - stream.Position >= 8) return reader.ReadInt64();
        if (kind == 'S' && limit - stream.Position >= 4)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > MaximumStringBytes || length > remainingStringBytes || length > limit - stream.Position)
                throw new InvalidDataException("Invalid FBX name.");
            remainingStringBytes -= length;
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
        throw new InvalidDataException("Unsupported FBX identity property.");
    }

    private static string CleanName(string value)
    {
        var end = value.IndexOf('\0');
        if (end >= 0) value = value[..end];
        var prefix = value.IndexOf("::", StringComparison.Ordinal);
        return prefix < 0 ? value : value[(prefix + 2)..];
    }

    private sealed record SourceObject(string Kind, string Name, string Type);
    private readonly record struct Record(string Name, long Properties, long Children, long End);
}
