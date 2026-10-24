using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model.Scene;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.App;

internal sealed record LilToonExportMaterial(
    string Id,
    string Name,
    string RenderMode,
    string? ShaderVariant,
    IReadOnlyDictionary<string, object> Properties,
    IReadOnlyDictionary<string, string> Textures,
    bool IsLilToon);

/// <summary>Writes the renderer-neutral geometry/material payload consumed by WebViewer.</summary>
internal static class LilToonSceneExporter
{
    private sealed record MeshPayload(string Name, float[] Positions, float[] Normals, float[] Uvs, int[] Indices, float[] Matrix, string MaterialId);
    private sealed record ScenePayload(long RequestId, IReadOnlyList<MeshPayload> Meshes, IReadOnlyList<LilToonExportMaterial> Materials, string Background);

    public static string Export(
        SceneNode root,
        long requestId,
        string sceneKey,
        string temporaryRoot,
        Func<MeshNode, string?, LilToonExportMaterial?> resolveMaterial,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var materialIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var materials = new List<LilToonExportMaterial>();
        var meshes = new List<MeshPayload>();
        var unmatchedIndex = 0;
        foreach (var mesh in Enumerate(root).OfType<MeshNode>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mesh.Visible || mesh.Geometry is not MeshGeometry3D geometry) continue;
            var resolved = resolveMaterial(mesh, mesh.Material?.Name);
            if (resolved is null)
            {
                var id = $"unmatched-{unmatchedIndex++}";
                resolved = new LilToonExportMaterial(id, mesh.Material?.Name ?? mesh.Name ?? "Imported material", "Opaque", null,
                    new Dictionary<string, object> { ["_Color"] = new[] { 0.72f, 0.72f, 0.72f, 1f } },
                    new Dictionary<string, string>(), false);
                warnings.Add($"FBX material '{mesh.Material?.Name ?? mesh.Name}' could not be safely matched to a Unity material; it is shown with a neutral fallback.");
            }
            if (!materialIds.ContainsKey(resolved.Id))
            {
                materialIds.Add(resolved.Id, resolved.Id);
                materials.Add(resolved);
            }

            var geometryPositions = geometry.Positions?.ToArray() ?? [];
            var geometryIndices = geometry.TriangleIndices?.ToArray() ?? [];
            var geometryNormals = geometry.Normals?.ToArray() ?? [];
            var geometryUvs = geometry.TextureCoordinates?.ToArray() ?? [];
            if (geometryPositions.Length < 3 || geometryIndices.Length < 3) continue;
            var positions = Flatten(geometryPositions);
            var normals = Flatten(geometryNormals);
            var uvs = Flatten(geometryUvs);
            var matrix = Matrix(mesh.TotalModelMatrix);
            if (positions.Any(value => !float.IsFinite(value)) || matrix.Any(value => !float.IsFinite(value)))
            {
                warnings.Add($"Mesh '{mesh.Name}' contains invalid positions or transforms and was omitted from the lilToon view.");
                continue;
            }
            if (normals.Length != positions.Length || normals.Any(value => !float.IsFinite(value)))
            {
                normals = [];
                warnings.Add($"Mesh '{mesh.Name}' contains invalid normals; the lilToon view recomputes them from its triangles.");
            }
            if (uvs.Any(value => !float.IsFinite(value)))
            {
                uvs = [];
                warnings.Add($"Mesh '{mesh.Name}' contains invalid texture coordinates; its texture mapping may differ.");
            }
            meshes.Add(new MeshPayload(
                mesh.Name ?? "Mesh",
                positions,
                normals,
                uvs,
                geometryIndices,
                matrix,
                resolved.Id));
        }

        var payload = new ScenePayload(requestId, meshes, materials, "#14191F");
        var safeKey = string.Concat(sceneKey.Where(char.IsAsciiLetterOrDigit));
        var path = Path.Combine(temporaryRoot, $"liltoon-scene-{safeKey}.json");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return path;
    }

    public static string ToAssetUrl(long requestId, string temporaryRoot, string path)
    {
        var relativePath = Path.GetRelativePath(temporaryRoot, path).Replace('\\', '/');
        var escaped = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        return $"https://assets-{requestId}.upe.local/{escaped}";
    }

    public static string Combine(long requestId, IReadOnlyList<string> sceneFiles, string temporaryRoot)
    {
        var meshNodes = new JsonArray();
        var materialNodes = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in sceneFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (root.TryGetProperty("meshes", out var meshArray))
                foreach (var mesh in meshArray.EnumerateArray()) meshNodes.Add(JsonNode.Parse(mesh.GetRawText()));
            if (root.TryGetProperty("materials", out var materialArray))
                foreach (var material in materialArray.EnumerateArray())
                {
                    var id = material.GetProperty("id").GetString();
                    if (id is not null) materialNodes.TryAdd(id, JsonNode.Parse(material.GetRawText())!);
                }
        }
        var payload = new JsonObject
        {
            ["requestId"] = requestId,
            ["meshes"] = meshNodes,
            ["materials"] = new JsonArray(materialNodes.Values.Select(x => x.DeepClone()).ToArray()),
            ["background"] = "#14191F"
        };
        var path = Path.Combine(temporaryRoot, $"liltoon-scene-{requestId}.json");
        File.WriteAllText(path, payload.ToJsonString());
        return path;
    }

    private static IEnumerable<SceneNode> Enumerate(SceneNode root)
    {
        yield return root;
        foreach (var child in root.Items)
            foreach (var nested in Enumerate(child)) yield return nested;
    }

    private static float[] Flatten(IReadOnlyList<Vector3> values)
    {
        var result = new float[values.Count * 3];
        for (var i = 0; i < values.Count; i++)
        {
            result[i * 3] = values[i].X;
            result[i * 3 + 1] = values[i].Y;
            result[i * 3 + 2] = values[i].Z;
        }
        return result;
    }

    private static float[] Flatten(IReadOnlyList<Vector2> values)
    {
        var result = new float[values.Count * 2];
        for (var i = 0; i < values.Count; i++)
        {
            result[i * 2] = values[i].X;
            result[i * 2 + 1] = values[i].Y;
        }
        return result;
    }

    private static float[] Matrix(Matrix4x4 value) =>
    [value.M11, value.M12, value.M13, value.M14,
     value.M21, value.M22, value.M23, value.M24,
     value.M31, value.M32, value.M33, value.M34,
     value.M41, value.M42, value.M43, value.M44];
}
