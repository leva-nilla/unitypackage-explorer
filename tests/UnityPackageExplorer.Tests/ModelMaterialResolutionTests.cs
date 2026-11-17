using System.IO;
using System.Numerics;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Model.Scene;
using UnityPackageExplorer.Core;
using UnityPackageExplorer.Rendering;

namespace UnityPackageExplorer.Tests;

public sealed class ModelMaterialResolutionTests
{
    private const string FirstGuid = "11111111111111111111111111111111";
    private const string SecondGuid = "22222222222222222222222222222222";

    [Fact]
    public void SingleMaterialRendererUsesItsGuidDespiteDifferentImportedMaterialName()
    {
        using var root = new GroupNode();
        var hair = Mesh("hair_frontbangs.001", "PetaNuri_Hair_UV001_Baked");
        var accessory = Mesh("hair_accessory", "Acc_M_PBR_Source");
        root.AddChildNode(hair);
        root.AddChildNode(accessory);
        var result = Apply(root, [Binding(FirstGuid), Binding(SecondGuid)],
            [new(1, "hair_frontbangs", [new(2100000, FirstGuid)]), new(2, "hair_accessory", [new(2100000, SecondGuid)])]);

        Assert.Equal(FirstGuid, result[hair].MaterialGuid);
        Assert.Equal(SecondGuid, result[accessory].MaterialGuid);
    }

    [Fact]
    public void AmbiguousRendererNamesDoNotFallBackToGlobalMaterialGuess()
    {
        using var mesh = Mesh("hair", "Unrelated");
        var result = Apply(mesh, [Binding(FirstGuid)],
            [new(1, "hair", [new(2100000, FirstGuid)]), new(2, "hair", [new(2100000, FirstGuid)])]);

        Assert.Empty(result);
    }

    [Fact]
    public void MissingExplicitMaterialDoesNotFallBackToAnotherAvailableMaterial()
    {
        using var mesh = Mesh("hair", "Renamed");
        Assert.Empty(Apply(mesh, [Binding(FirstGuid)], [new(1, "hair", [new(2100000, SecondGuid)])]));
        Assert.Empty(Apply(mesh, [Binding(FirstGuid)], [], new Dictionary<string, UnityAssetReference>
        {
            ["Renamed"] = new(2100000, SecondGuid)
        }));
    }

    [Fact]
    public void RendererOverrideTakesPrecedenceOverModelImporterDefault()
    {
        using var mesh = Mesh("hair", "Source");
        var result = Apply(mesh, [Binding(FirstGuid), Binding(SecondGuid)],
            [new(1, "hair", [new(2100000, FirstGuid)])], new Dictionary<string, UnityAssetReference>
            {
                ["Source"] = new(2100000, SecondGuid)
            });

        Assert.Equal(FirstGuid, Assert.Single(result).Value.MaterialGuid);
    }

    [Fact]
    public void MultipleRendererMaterialsAreNotAssignedBySlotOrder()
    {
        using var mesh = Mesh("hair", "Source");
        Assert.Empty(Apply(mesh, [Binding(FirstGuid), Binding(SecondGuid)],
            [new(1, "hair", [new(2100000, FirstGuid), new(2100000, SecondGuid)])]));
    }

    [Fact]
    public async Task PreservesIdenticalLookingFbxMaterialsAndResolvesRenamedUnityMaterialsInBothPhases()
    {
        // Keep project fixtures beside the working tree, as the other loader tests do.
        // CI's checkout and OS temp directory can be on different drives.
        var root = Path.Combine(Environment.CurrentDirectory, ".artifacts", $"material-test-{Guid.NewGuid():N}");
        var assets = Path.Combine(root, "Assets");
        Directory.CreateDirectory(assets);
        var model = Path.Combine(assets, "TwoMaterials.fbx");
        var prefab = Path.Combine(assets, "Empty.prefab");
        File.WriteAllText(model, TwoMaterialFbx);
        File.WriteAllText(model + ".meta", $$"""
            fileFormatVersion: 2
            guid: 33333333333333333333333333333333
            ModelImporter:
              externalObjects:
              - first:
                  type: UnityEngine:Material
                  name: SourceFace
                second: {fileID: 2100000, guid: {{FirstGuid}}, type: 2}
              - first:
                  type: UnityEngine:Material
                  name: SourceBody
                second: {fileID: 2100000, guid: {{SecondGuid}}, type: 2}
            """);
        File.WriteAllText(prefab, "%YAML 1.1\n");
        foreach (var (guid, name) in new[] { (FirstGuid, "RenamedFace"), (SecondGuid, "RenamedBody") })
        {
            var path = Path.Combine(assets, name + ".mat");
            File.WriteAllText(path, $"%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  m_Name: {name}\n");
            File.WriteAllText(path + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
        }
        try
        {
            var imported = LightweightPreviewLoader.ImportModel(model, Matrix4x4.Identity, CancellationToken.None);
            try
            {
                Assert.Equal(new[] { "SourceBody", "SourceFace" },
                    Meshes(imported.Root).Select(mesh => mesh.Material?.Name).Order().ToArray());
            }
            finally { imported.Root.Dispose(); }

            using var catalog = UnityAssetCatalog.OpenProjectPrefab(prefab);
            var selected = Assert.Single(catalog.Entries, entry => entry.Extension == ".fbx");
            using var geometry = await LightweightPreviewLoader.LoadGeometryAsync(catalog, selected, Path.Combine(root, "staged"), CancellationToken.None);
            Assert.True(geometry.Scenes.Count == 1, string.Join(Environment.NewLine, geometry.Warnings));
            var plan = await LightweightPreviewLoader.CompleteMaterialsAsync(geometry, catalog, selected, Path.Combine(root, "staged"), CancellationToken.None);
            var bindings = Assert.Single(plan.Plans).ExportBindings;
            Assert.Equal(2, bindings.Count);
            Assert.Equal(FirstGuid, Assert.Single(bindings, pair => pair.Key.Material?.Name == "SourceFace").Value.MaterialGuid);
            Assert.Equal(SecondGuid, Assert.Single(bindings, pair => pair.Key.Material?.Name == "SourceBody").Value.MaterialGuid);
            Assert.DoesNotContain(plan.Warnings, warning => warning.Contains("left unused", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static MeshNode Mesh(string name, string material) => new() { Name = name, Material = new PhongMaterialCore { Name = material } };
    private static RenderingMaterialBinding Binding(string guid) => new(guid, "Renamed", "", "", null, null, UnityMaterialRenderMode.Opaque, null);
    private static IReadOnlyDictionary<MeshNode, RenderingMaterialBinding> Apply(SceneNode root,
        IReadOnlyList<RenderingMaterialBinding> bindings, IReadOnlyList<PrefabRendererMaterialAssignment> renderers,
        IReadOnlyDictionary<string, UnityAssetReference>? remaps = null)
        => LightweightPreviewLoader.ApplyUnityMaterials(root, bindings, [new(2100000, FirstGuid)], renderers,
            true, false, CancellationToken.None, null, materialRemaps: remaps);
    private static IEnumerable<MeshNode> Meshes(SceneNode node)
    {
        if (node is MeshNode mesh) yield return mesh;
        foreach (var child in node.Items)
            foreach (var nested in Meshes(child)) yield return nested;
    }

    // Original two-triangle FBX: same geometry name and identical material properties.
    // Only the source material identities distinguish the two imported submeshes.
    private const string TwoMaterialFbx = """
        ; FBX 7.4.0 project file
        FBXHeaderExtension: { FBXHeaderVersion: 1003 FBXVersion: 7400 }
        Documents: { Count: 1 Document: 1, "Scene", "Scene" { RootNode: 0 } }
        Definitions: { Version: 100 Count: 4
            ObjectType: "Geometry" { Count: 1 }
            ObjectType: "Model" { Count: 1 }
            ObjectType: "Material" { Count: 2 }
        }
        Objects: {
            Geometry: 100, "Geometry::SharedMesh", "Mesh" {
                GeometryVersion: 124
                Vertices: *12 { a: 0,0,0,1,0,0,1,1,0,0,1,0 }
                PolygonVertexIndex: *6 { a: 0,1,-3,0,2,-4 }
                LayerElementMaterial: 0 { Version: 101 Name: ""
                    MappingInformationType: "ByPolygon"
                    ReferenceInformationType: "IndexToDirect"
                    Materials: *2 { a: 0,1 }
                }
                Layer: 0 { Version: 100 LayerElement: { Type: "LayerElementMaterial" TypedIndex: 0 } }
            }
            Model: 200, "Model::Shared", "Mesh" { Version: 232 }
            Material: 300, "Material::SourceFace", "" {
                Version: 102 ShadingModel: "phong" MultiLayer: 0
                Properties70: { P: "DiffuseColor", "Color", "", "A",0.8,0.8,0.8 }
            }
            Material: 400, "Material::SourceBody", "" {
                Version: 102 ShadingModel: "phong" MultiLayer: 0
                Properties70: { P: "DiffuseColor", "Color", "", "A",0.8,0.8,0.8 }
            }
        }
        Connections: { C: "OO",100,200 C: "OO",300,200 C: "OO",400,200 C: "OO",200,0 }
        """;
}
