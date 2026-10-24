using System.IO;
using System.Numerics;
using System.Text.Json;
using HelixToolkit;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model.Scene;
using UnityPackageExplorer.App;

namespace UnityPackageExplorer.Tests;

public sealed class LilToonSceneExporterTests
{
    [Fact]
    public void RepairsInvalidImportedNormalsWithoutDiscardingValidMeshPositions()
    {
        using var mesh = CreateTriangle();
        mesh.Geometry = new MeshGeometry3D
        {
            Positions = new Vector3Collection { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            Normals = new Vector3Collection { new(float.NaN, 0, 0), new(0, float.PositiveInfinity, 0), Vector3.UnitZ },
            Indices = new IntCollection { 0, 1, 2 }
        };
        WithExport(mesh, (json, warnings) =>
        {
            var payload = Assert.Single(json.GetProperty("meshes").EnumerateArray());
            Assert.Equal(9, payload.GetProperty("positions").GetArrayLength());
            Assert.Equal(0, payload.GetProperty("normals").GetArrayLength());
            Assert.Contains(warnings, warning => warning.Contains("recomputes", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void IncludesParentTransformAndRetainsUnmatchedGeometryWithFallbackMaterial()
    {
        using var parent = new GroupNode { ModelMatrix = Matrix4x4.CreateTranslation(10, 20, 30) };
        var mesh = CreateTriangle();
        mesh.ModelMatrix = Matrix4x4.CreateTranslation(2, 3, 4);
        parent.AddChildNode(mesh);
        parent.UpdateAllTransformMatrix();
        WithExport(parent, (json, warnings) =>
        {
            var payload = Assert.Single(json.GetProperty("meshes").EnumerateArray());
            var matrix = payload.GetProperty("matrix").EnumerateArray().Select(element => element.GetSingle()).ToArray();
            Assert.Equal(new[] { 12f, 23f, 34f }, matrix[12..15]);
            var material = Assert.Single(json.GetProperty("materials").EnumerateArray());
            Assert.False(material.GetProperty("isLilToon").GetBoolean());
            Assert.Contains(warnings, warning => warning.Contains("neutral fallback", StringComparison.Ordinal));
        });
    }

    private static MeshNode CreateTriangle() => new()
    {
        Name = "Triangle",
        Geometry = new MeshGeometry3D
        {
            Positions = new Vector3Collection { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            Normals = new Vector3Collection { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            Indices = new IntCollection { 0, 1, 2 }
        }
    };

    private static void WithExport(SceneNode root, Action<JsonElement, List<string>> verify)
    {
        var directory = Path.Combine(Path.GetTempPath(), "upe-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var warnings = new List<string>();
            var path = LilToonSceneExporter.Export(root, 7, "fixture", directory, (_, _) => null, warnings, CancellationToken.None);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            verify(document.RootElement, warnings);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
