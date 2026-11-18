using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using HelixToolkit.SharpDX.Model.Scene;
using UnityPackageExplorer.Core;
using UnityPackageExplorer.Rendering;

namespace UnityPackageExplorer.Tests;

public sealed class FbxBindingTests
{
    [Theory]
    [InlineData("", "ef46db3751d8e999")]
    [InlineData("a", "d24ec4f1a98c6e5b")]
    [InlineData("abc", "44bc2cf5ad770999")]
    [InlineData("The quick brown fox jumps over the lazy dog", "0b242d361fda71bc")]
    public void HashMatchesXxHash64Vectors(string text, string expected)
        => Assert.Equal(expected, unchecked((ulong)UnityModelFileId.Hash(text)).ToString("x16"));

    [Fact]
    public void MatchesModelFileIdsEvenWhenGeometryAndRendererWereRenamed()
    {
        Assert.Equal(4909762108231848987, UnityModelFileId.Mesh("X_Tight"));
        Assert.Equal(5340118069397963977, UnityModelFileId.Mesh("Garter"));
        Assert.Equal(-9022799016997099106, UnityModelFileId.Renderer("//RootNode/root/Grim_Cover_R", "MeshRenderer"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReadsDeclaredPolygonSlotsInsteadOfConnectionOrObjectOrder(bool wide, bool compressed)
    {
        using var stream = new MemoryStream(Fixture(wide, compressed));
        var models = FbxMaterialLayout.Read(stream);
        var part = Assert.Single(models, model => model.Name == "Part");
        Assert.Equal("Geometry_Name", part.GeometryName);
        Assert.Equal(new[] { "Atlas", "Lace", "Metal" }, part.MaterialSlots);
        Assert.Equal("//RootNode/root/Part", part.RendererPath);
        Assert.Equal(new[] { "Metal" }, Assert.Single(models, model => model.Name == "Other").MaterialSlots);
    }

    [Fact]
    public void SingleRootIsReplacedRatherThanTriedAsAnAlternativeHash()
    {
        using var stream = new MemoryStream(Fixture(false, true, singleRoot: true));
        var part = Assert.Single(FbxMaterialLayout.Read(stream), model => model.Name == "Part");
        Assert.Equal("Armature/Part", part.Path);
        Assert.Equal("//RootNode/root/Part", part.RendererPath);
    }

    [Fact]
    public void RejectsMalformedLayoutAndHonorsCancellation()
    {
        var bytes = Fixture(false, true);
        Array.Fill(bytes, (byte)255, 27, 4);
        Assert.Empty(FbxMaterialLayout.Read(new MemoryStream(bytes)));
        Assert.Empty(FbxMaterialLayout.Read(new MemoryStream(new byte[30])));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            FbxMaterialLayout.Read(new MemoryStream(Fixture(false, false)), new CancellationToken(true)));
        bytes = Fixture(false, true);
        var array = Encoding.ASCII.GetString(bytes).IndexOf("Materials", StringComparison.Ordinal) + "Materials".Length;
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, array + 1);
        Assert.Empty(FbxMaterialLayout.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void ConflictingOverridesDoNotFallBackToInheritedRendererMaterial()
    {
        WithScene(root =>
        {
            const string guid = "11111111111111111111111111111111";
            var target = UnityModelFileId.Renderer("//RootNode/root/Part", "MeshRenderer");
            var selection = LightweightPreviewLoader.ResolveModelSelection(root, guid,
                [new(UnityModelFileId.Mesh("Part"), "Renamed", [new(2100000, "inherited")])],
                [new(target, guid, 0, new(2100000, "one")), new(target, guid, 0, new(2100000, "two"))],
                new List<string>());
            var conflicted = Assert.Single(Meshes(root), mesh => mesh.Material?.Name == "Atlas");
            Assert.Contains(conflicted, selection.ScopedMeshes);
            Assert.False(selection.Materials.ContainsKey(conflicted));
            Assert.Equal(2, selection.UnresolvedOverrides.Count);
        });
    }

    [Fact]
    public void DoesNotUseSingleRootRendererHashForAMultipleRootModel()
    {
        WithScene(root =>
        {
            const string guid = "11111111111111111111111111111111";
            var selection = LightweightPreviewLoader.ResolveModelSelection(root, guid, [],
                [new(UnityModelFileId.Renderer("//RootNode/root", "MeshRenderer"), guid, 0, new(2100000, "wrong"))],
                new List<string>());
            Assert.Empty(selection.Materials);
            Assert.Single(selection.UnresolvedOverrides);
        });
    }

    [Fact]
    public void IsolatesReferencedModelAndResolvesEveryDeclaredMaterialSlot()
    {
        WithScene(root =>
        {
            var guid = "11111111111111111111111111111111";
            var refs = new[] { new UnityAssetReference(2100000, "a"), new(2100000, "b"), new(2100000, "c") };
            var warnings = new List<string>();
            var selection = LightweightPreviewLoader.ResolveModelSelection(root, guid,
                [new(UnityModelFileId.Mesh("Part"), "RenamedRenderer", refs)], [], warnings);
            Assert.True(selection.MeshesIsolated, string.Join("; ", Meshes(root).Select(mesh =>
                $"{mesh.Name} parent={mesh.Parent?.Name} grandparent={mesh.Parent?.Parent?.Name} material={mesh.Material?.Name}")));
            Assert.Equal(3, Meshes(root).Count());
            Assert.DoesNotContain(Meshes(root), mesh => mesh.Parent?.Name == "Other");
            Assert.Equal("a", Assert.Single(selection.Materials, item => item.Key.Material?.Name == "Atlas").Value.Guid);
            Assert.Equal("b", Assert.Single(selection.Materials, item => item.Key.Material?.Name == "Lace").Value.Guid);
            Assert.Equal("c", Assert.Single(selection.Materials, item => item.Key.Material?.Name == "Metal").Value.Guid);
            Assert.Empty(warnings);
        });
    }

    [Fact]
    public void ResolvesNestedRendererFileIdAndDoesNotApplyConflictingOverrides()
    {
        WithScene(root =>
        {
            var target = UnityModelFileId.Renderer("//RootNode/root/Part", "MeshRenderer");
            const string guid = "11111111111111111111111111111111";
            var good = new PrefabMaterialOverride(target, guid, 1, new(2100000, "good"));
            var conflict1 = new PrefabMaterialOverride(target, guid, 0, new(2100000, "one"));
            var conflict2 = new PrefabMaterialOverride(target, guid, 0, new(2100000, "two"));
            var foreign = new PrefabMaterialOverride(target, "other-fbx", 2, new(2100000, "foreign"));
            var selection = LightweightPreviewLoader.ResolveModelSelection(root, guid, [],
                [good, conflict1, conflict2, foreign], new List<string>());
            Assert.Equal("good", Assert.Single(selection.Materials).Value.Guid);
            Assert.Equal("Lace", Assert.Single(selection.Materials).Key.Material?.Name);
            Assert.Equal(new[] { conflict1, conflict2, foreign }, selection.UnresolvedOverrides);
            Assert.Contains(selection.ScopedMeshes, mesh => mesh.Material?.Name == "Atlas");
        });
    }

    private static IEnumerable<MeshNode> Meshes(SceneNode root)
    {
        if (root is MeshNode mesh) yield return mesh;
        foreach (var child in root.Items)
            foreach (var nested in Meshes(child)) yield return nested;
    }

    private static void WithScene(Action<SceneNode> check)
    {
        var folder = Path.Combine(Environment.CurrentDirectory, ".artifacts", "fbx-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "fixture.fbx");
            File.WriteAllBytes(path, Fixture(false, true));
            var scene = LightweightPreviewLoader.ImportModel(path, Matrix4x4.Identity, CancellationToken.None);
            try { check(scene.Root); }
            finally { scene.Root.Dispose(); }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private sealed record Node(string Name, object[] Properties, params Node[] Children);
    private static byte[] Fixture(bool wide, bool compressed, bool singleRoot = false)
    {
        Node Geometry(long id, string name, int[] materials) => new("Geometry", [id, name + "\0\u0001Geometry", "Mesh"],
            new("GeometryVersion", [124]),
            new("Vertices", [new double[] { 0,0,0, 1,0,0, 0,1,0, 2,0,0, 3,0,0, 2,1,0, 4,0,0, 5,0,0, 4,1,0 }]),
            new("PolygonVertexIndex", [new int[] { 0, 1, -3, 3, 4, -6, 6, 7, -9 }]),
            new("LayerElementMaterial", [0],
                new("Version", [101]), new("Name", [""]),
                new("MappingInformationType", ["ByPolygon"]), new("ReferenceInformationType", ["IndexToDirect"]),
                new("Materials", [materials])),
            new("Layer", [0], new("Version", [100]),
                new("LayerElement", [], new("Type", ["LayerElementMaterial"]), new("TypedIndex", [0]))));
        Node Material(long id, string name) => new("Material", [id, name + "\0\u0001Material", ""],
            new("Version", [102]), new("ShadingModel", ["phong"]));
        Node Connection(long child, long parent) => new("C", ["OO", child, parent]);
        var objects = new Node("Objects", [], [
            ..(singleRoot ? new[] { new Node("Model", [3L, "Armature\0\u0001Model", "Null"], new Node("Version", [232])) } : []),
            new("Model", [1L, "Part\0\u0001Model", "Mesh"], new Node("Version", [232])),
            new("Model", [2L, "Other\0\u0001Model", "Mesh"], new Node("Version", [232])),
            Geometry(11, "Geometry_Name", [1, 2, 0]), Geometry(12, "Extra", [0, 0, 0]),
            Material(30, "Metal"), Material(31, "Atlas"), Material(32, "Lace"), Material(33, "Unused")]);
        var connections = new Node("Connections", [], [
            Connection(1, singleRoot ? 3 : 0), Connection(2, singleRoot ? 3 : 0), Connection(11, 1), Connection(12, 2),
            ..(singleRoot ? new[] { Connection(3, 0) } : []),
            Connection(30, 1), Connection(31, 1), Connection(32, 1), Connection(33, 1), Connection(30, 2)]);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write("Kaydara FBX Binary  \0\x1a\0"u8);
        writer.Write(wide ? 7500 : 7400);
        void Write(Node node)
        {
            var start = output.Position;
            writer.Write(new byte[wide ? 25 : 13]);
            var name = Encoding.ASCII.GetBytes(node.Name);
            writer.Write(name);
            var propertiesStart = output.Position;
            foreach (var property in node.Properties)
            {
                switch (property)
                {
                    case long value: writer.Write((byte)'L'); writer.Write(value); break;
                    case int value: writer.Write((byte)'I'); writer.Write(value); break;
                    case string value:
                        var stringBytes = Encoding.UTF8.GetBytes(value);
                        writer.Write((byte)'S'); writer.Write(stringBytes.Length); writer.Write(stringBytes); break;
                    case Array array:
                        using (var raw = new MemoryStream())
                        {
                            using var rawWriter = new BinaryWriter(raw, Encoding.UTF8, true);
                            foreach (var item in array)
                                if (item is int number) rawWriter.Write(number); else rawWriter.Write((double)item);
                            var bytes = raw.ToArray();
                            if (compressed)
                            {
                                using var buffer = new MemoryStream();
                                using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, true)) zlib.Write(bytes);
                                bytes = buffer.ToArray();
                            }
                            writer.Write((byte)(array is int[] ? 'i' : 'd'));
                            writer.Write(array.Length); writer.Write(compressed ? 1 : 0); writer.Write(bytes.Length); writer.Write(bytes);
                        }
                        break;
                    default: throw new InvalidOperationException();
                }
            }
            var bytesLength = output.Position - propertiesStart;
            foreach (var child in node.Children) Write(child);
            if (node.Children.Length > 0) writer.Write(new byte[wide ? 25 : 13]);
            var end = output.Position;
            output.Position = start;
            if (wide) { writer.Write((ulong)end); writer.Write((ulong)node.Properties.Length); writer.Write((ulong)bytesLength); }
            else { writer.Write((uint)end); writer.Write((uint)node.Properties.Length); writer.Write((uint)bytesLength); }
            writer.Write((byte)name.Length);
            output.Position = end;
        }
        Write(new("FBXHeaderExtension", [], new("FBXHeaderVersion", [1003]), new("FBXVersion", [wide ? 7500 : 7400])));
        Write(new("Documents", [], new("Count", [1]), new("Document", [1000L, "Scene", "Scene"], new Node("RootNode", [0L]))));
        Write(new("Definitions", [], new("Version", [100]), new("Count", [singleRoot ? 9 : 8]),
            new("ObjectType", ["Geometry"], new Node("Count", [2])),
            new("ObjectType", ["Model"], new Node("Count", [singleRoot ? 3 : 2])),
            new("ObjectType", ["Material"], new Node("Count", [4]))));
        Write(objects); Write(connections);
        writer.Write(new byte[wide ? 25 : 13]);
        return output.ToArray();
    }
}
