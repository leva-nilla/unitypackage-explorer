using System.Numerics;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityPrefabParserTests
{
    [Fact]
    public void BuildsHierarchyTransformsAndAssetReferences()
    {
        const string yaml = """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1 &100
            GameObject:
              m_Component:
              - component: {fileID: 101}
              - component: {fileID: 102}
              - component: {fileID: 103}
              m_Name: Root
              m_PrefabInstance: {fileID: 0}
            --- !u!4 &101
            Transform:
              m_GameObject: {fileID: 100}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 1, y: 2, z: 3}
              m_LocalScale: {x: 1, y: 1, z: 1}
              m_Children:
              - {fileID: 201}
              m_Father: {fileID: 0}
            --- !u!33 &102
            MeshFilter:
              m_GameObject: {fileID: 100}
              m_Mesh: {fileID: 4300000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
            --- !u!23 &103
            MeshRenderer:
              m_GameObject: {fileID: 100}
              m_Materials:
              - {fileID: 2100000, guid: 1234567890abcdef1234567890abcdef, type: 2}
            --- !u!1 &200
            GameObject:
              m_Component:
              - component: {fileID: 201}
              m_Name: Child
              m_PrefabInstance: {fileID: 0}
            --- !u!4 &201
            Transform:
              m_GameObject: {fileID: 200}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 0, y: 1.5, z: 0}
              m_LocalScale: {x: 2, y: 2, z: 2}
              m_Father: {fileID: 101}
            """;

        var result = UnityPrefabParser.Parse(yaml);

        var root = Assert.Single(result.Roots);
        Assert.Equal("Root", root.Name);
        Assert.Equal(new Vector3Value(1, 2, 3), root.Position);
        Assert.Equal("abcdefabcdefabcdefabcdefabcdefab", root.Mesh?.Guid);
        Assert.Equal("1234567890abcdef1234567890abcdef", Assert.Single(root.Materials).Guid);
        var child = Assert.Single(root.Children);
        Assert.Equal("Child", child.Name);
        Assert.Equal(new Vector3Value(0, 1.5f, 0), child.Position);
        Assert.Equal(new Vector3Value(2, 2, 2), child.Scale);
    }

    [Fact]
    public void ReportsEmptyOrUnrecognizedSerializedPrefab()
    {
        var result = UnityPrefabParser.Parse("not a Unity prefab");
        Assert.Empty(result.Roots);
        Assert.Contains(result.Warnings, x => x.Contains("No Unity text-serialized", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsMeshReferenceFromSkinnedMeshRenderer()
    {
        const string yaml = """
            --- !u!1 &100
            GameObject:
              m_Component:
              - component: {fileID: 101}
              - component: {fileID: 102}
              m_Name: Character
              m_PrefabInstance: {fileID: 0}
            --- !u!4 &101
            Transform:
              m_GameObject: {fileID: 100}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 0, y: 0, z: 0}
              m_LocalScale: {x: 1, y: 1, z: 1}
              m_Father: {fileID: 0}
            --- !u!137 &102
            SkinnedMeshRenderer:
              m_GameObject: {fileID: 100}
              m_Mesh: {fileID: 4300000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
            """;

        var result = UnityPrefabParser.Parse(yaml);

        var root = Assert.Single(result.Roots);
        Assert.Equal("abcdefabcdefabcdefabcdefabcdefab", root.Mesh?.Guid);
    }

    [Fact]
    public void AppliesTransformOverridesToPrefabInstancesWithoutStrippedObjects()
    {
        const string yaml = """
            --- !u!1001 &500
            PrefabInstance:
              m_Modification:
                serializedVersion: 3
                m_TransformParent: {fileID: 0}
                m_Modifications:
                - target: {fileID: 400000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
                  propertyPath: m_LocalPosition.x
                  value: 2.5
                  objectReference: {fileID: 0}
                - target: {fileID: 400000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
                  propertyPath: m_LocalPosition.y
                  value: -1
                  objectReference: {fileID: 0}
                - target: {fileID: 400000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
                  propertyPath: m_LocalRotation.z
                  value: 0.7071068
                  objectReference: {fileID: 0}
                - target: {fileID: 400000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
                  propertyPath: m_LocalRotation.w
                  value: 0.7071068
                  objectReference: {fileID: 0}
                - target: {fileID: 400000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
                  propertyPath: m_LocalScale.x
                  value: 2
                  objectReference: {fileID: 0}
                - target: {fileID: 400000, guid: 11111111111111111111111111111111, type: 3}
                  propertyPath: m_LocalPosition.x
                  value: 88
                  objectReference: {fileID: 0}
                - target: {fileID: 400001, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
                  propertyPath: m_LocalPosition.x
                  value: 99
                  objectReference: {fileID: 0}
              m_SourcePrefab: {fileID: 100100000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
            """;

        var node = Assert.Single(UnityPrefabParser.Parse(yaml, _ => 400000).Roots);

        Assert.Equal(new Vector3Value(2.5f, -1, 0), node.Position);
        Assert.Equal(new QuaternionValue(0, 0, 0.7071068f, 0.7071068f), node.Rotation);
        Assert.Equal(new Vector3Value(2, 1, 1), node.Scale);
    }

    [Fact]
    public void DoesNotApplyChildTransformOverrideToKnownPrefabRoot()
    {
        const string yaml = """
            --- !u!1001 &500
            PrefabInstance:
              m_Modification:
                m_Modifications:
                - target: {fileID: 400001, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
                  propertyPath: m_LocalPosition.x
                  value: 99
                  objectReference: {fileID: 0}
              m_SourcePrefab: {fileID: 100100000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
            """;

        var node = Assert.Single(UnityPrefabParser.Parse(yaml, _ => 400000).Roots);

        Assert.Equal(Vector3Value.Zero, node.Position);
    }

    [Fact]
    public void AppliesTransformOverridesToStrippedPrefabInstanceObjects()
    {
        const string yaml = """
            --- !u!1001 &500
            PrefabInstance:
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications:
                - target: {fileID: 400000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
                  propertyPath: m_LocalPosition.x
                  value: 2.5
                  objectReference: {fileID: 0}
              m_SourcePrefab: {fileID: 100100000, guid: abcdefabcdefabcdefabcdefabcdefab, type: 3}
            --- !u!1 &100
            GameObject:
              m_Component:
              - component: {fileID: 101}
              m_Name: InstanceRoot
              m_PrefabInstance: {fileID: 500}
            --- !u!4 &101
            Transform:
              m_GameObject: {fileID: 100}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 0, y: 0, z: 0}
              m_LocalScale: {x: 1, y: 1, z: 1}
              m_Father: {fileID: 0}
            """;

        var node = Assert.Single(UnityPrefabParser.Parse(yaml, _ => 400000).Roots);

        Assert.Equal("InstanceRoot", node.Name);
        Assert.Equal(new Vector3Value(2.5f, 0, 0), node.Position);
    }

    [Fact]
    public void GroupsRepeatedModelAssetsAtTheSameTransformAndCombinesTheirMaterials()
    {
        const string modelGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var first = new PrefabNode { FileId = 1, Name = "Foot", Mesh = new UnityAssetReference(101, modelGuid) };
        var second = new PrefabNode { FileId = 2, Name = "Jacket", Mesh = new UnityAssetReference(102, modelGuid) };
        var translated = new PrefabNode { FileId = 3, Name = "Translated", Mesh = new UnityAssetReference(103, modelGuid), Position = new Vector3Value(2, 0, 0) };
        var anotherModel = new PrefabNode { FileId = 4, Name = "Another", Mesh = new UnityAssetReference(104, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb") };
        first.Materials.Add(new UnityAssetReference(201, "cccccccccccccccccccccccccccccccc"));
        second.Materials.Add(new UnityAssetReference(202, "dddddddddddddddddddddddddddddddd"));
        var placements = UnityPrefabParser.GetModelPlacements([first, second, translated, anotherModel]);

        Assert.Equal(3, placements.Count);
        var repeated = Assert.Single(placements, x => x.Node.Name == "Foot");
        Assert.Equal(2, repeated.Materials.Count);
        Assert.Equal([101L, 102L], repeated.MeshFileIds);
        Assert.Equal(["Foot", "Jacket"], repeated.RendererAssignments.Select(x => x.RendererName));
        Assert.Equal([101L, 102L], repeated.RendererAssignments.Select(x => x.MeshFileId));
        Assert.Equal(["cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddd"],
            repeated.RendererAssignments.Select(x => Assert.Single(x.Materials).Guid));
        Assert.Equal(Matrix4x4.Identity, repeated.Transform);
        Assert.Contains(placements, x => x.Node.Name == "Translated" && x.Transform != Matrix4x4.Identity);
        Assert.Contains(placements, x => x.Node.Name == "Another");
    }

    [Fact]
    public void WholeFbxPreviewUsesRootTransformForSkinnedRendererButKeepsStaticMeshTransform()
    {
        const string modelGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var root = new PrefabNode { FileId = 1, Name = "OutfitRoot", Position = new Vector3Value(2, 0, 0) };
        var renderer = new PrefabNode
        {
            FileId = 2,
            Name = "SleeveBoneRenderer",
            Position = new Vector3Value(0, 4, 0),
            Rotation = new QuaternionValue(0.7071068f, 0, 0, 0.7071068f),
            Mesh = new UnityAssetReference(101, modelGuid)
        };
        renderer.Components.Add("SkinnedMeshRenderer");
        var staticModelReference = new PrefabNode
        {
            FileId = 4,
            Name = "StaticModelReference",
            Mesh = new UnityAssetReference(103, modelGuid)
        };
        staticModelReference.Components.Add("MeshFilter");
        root.Children.Add(staticModelReference);
        root.Children.Add(renderer);
        var staticMesh = new PrefabNode
        {
            FileId = 3,
            Name = "PlacedProp",
            Position = new Vector3Value(0, 4, 0),
            Mesh = new UnityAssetReference(102, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
        };
        staticMesh.Components.Add("MeshFilter");
        root.Children.Add(staticMesh);

        var placements = UnityPrefabParser.GetModelPlacements([root], useRootTransformForSkinnedMeshRenderers: true);
        var placement = Assert.Single(placements, x => x.Guid == modelGuid);

        Assert.Same(staticModelReference, placement.Node);
        Assert.Equal(Matrix4x4.CreateTranslation(2, 0, 0), placement.Transform);
        Assert.True(placement.UsesRootTransformApproximation);
        Assert.Equal([103L, 101L], placement.MeshFileIds);
        Assert.Equal(Matrix4x4.CreateTranslation(2, 4, 0), Assert.Single(placements, x => x.Guid == staticMesh.Mesh!.Guid).Transform);
    }

    [Fact]
    public void IncludesPrefabSourceReferencesAsModelPlacements()
    {
        const string sourceGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var sourcePrefab = new PrefabNode
        {
            FileId = 5,
            Name = "ImportedModelPrefab",
            SourcePrefab = new UnityAssetReference(100100000, sourceGuid)
        };

        var placement = Assert.Single(UnityPrefabParser.GetModelPlacements([sourcePrefab]));

        Assert.Equal(sourceGuid, placement.Guid);
        Assert.Same(sourcePrefab, placement.Node);
        Assert.Empty(placement.MeshFileIds);
    }

    [Fact]
    public void ParsesNestedPrefabMaterialOverridesAndWarnsAboutApproximateRendererMapping()
    {
        const string sourceGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string materialGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string yaml = """
            --- !u!1 &100
            GameObject:
              m_Component:
              - component: {fileID: 101}
              m_Name: Outfit
              m_PrefabInstance: {fileID: 500}
            --- !u!4 &101
            Transform:
              m_GameObject: {fileID: 100}
              m_Father: {fileID: 0}
            --- !u!1001 &500
            PrefabInstance:
              m_SourcePrefab: {fileID: 100100000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications:
                - target: {fileID: 44, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}
                  propertyPath: m_Materials.Array.data[0]
                  value:
                    objectReference: {fileID: 2100000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 2}
            """;

        var result = UnityPrefabParser.Parse(yaml);
        var root = Assert.Single(result.Roots);

        Assert.Equal(sourceGuid, root.SourcePrefab?.Guid);
        Assert.Empty(root.Materials);
        var materialOverride = Assert.Single(root.MaterialOverrides);
        Assert.Equal(44, materialOverride.TargetFileId);
        Assert.Equal(sourceGuid, materialOverride.TargetGuid);
        Assert.Equal(0, materialOverride.MaterialSlot);
        Assert.Equal(materialGuid, materialOverride.Material.Guid);
        var placement = Assert.Single(UnityPrefabParser.GetModelPlacements(result.Roots));
        Assert.Empty(placement.Materials);
        Assert.Empty(placement.RendererAssignments);
        Assert.Equal(materialOverride, Assert.Single(placement.MaterialOverrides));
        Assert.Contains(result.Warnings, warning => warning.Contains("target renderer and slot are retained for diagnostics", StringComparison.Ordinal));
        Assert.DoesNotContain("Unknown component", root.Components);
    }

    [Fact]
    public void ResolvesVariantChainAndAppliesExactRendererOverridesAndRootTransforms()
    {
        const string baseGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string variantGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string outerGuid = "cccccccccccccccccccccccccccccccc";
        const string meshGuid = "dddddddddddddddddddddddddddddddd";
        const string baseMaterialGuid = "11111111111111111111111111111111";
        const string firstMaterialGuid = "22222222222222222222222222222222";
        const string secondMaterialGuid = "33333333333333333333333333333333";
        const long baseRendererId = 30;
        const long firstInstanceId = 100;
        const long outerInstanceId = 200;
        var variantRendererId = (baseRendererId ^ firstInstanceId) & long.MaxValue;

        var basePrefab = $$"""
            --- !u!1 &10
            GameObject:
              m_Component:
              - component: {fileID: 20}
              - component: {fileID: 30}
              m_Name: Body
            --- !u!4 &20
            Transform:
              m_GameObject: {fileID: 10}
              m_Father: {fileID: 0}
              m_LocalPosition: {x: 1, y: 2, z: 3}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalScale: {x: 2, y: 2, z: 2}
            --- !u!137 &30
            SkinnedMeshRenderer:
              m_GameObject: {fileID: 10}
              m_Mesh: {fileID: 4300000, guid: {{meshGuid}}, type: 3}
              m_Materials:
              - {fileID: 2100000, guid: {{baseMaterialGuid}}, type: 2}
              - {fileID: 2100000, guid: {{baseMaterialGuid}}, type: 2}
            """;
        var variant = $$"""
            --- !u!1001 &{{firstInstanceId}}
            PrefabInstance:
              m_SourcePrefab: {fileID: 100100000, guid: {{baseGuid}}, type: 3}
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications:
                - target: {fileID: {{baseRendererId}}, guid: {{baseGuid}},
                    type: 3}
                  propertyPath: m_Materials.Array.data[0]
                  value:
                    objectReference: {fileID: 2100000, guid: {{firstMaterialGuid}}, type: 2}
                - target: {fileID: 20, guid: {{baseGuid}}, type: 3}
                  propertyPath: m_LocalPosition.x
                  value: 4
                  objectReference: {fileID: 0}
            """;
        var outer = $$"""
            --- !u!1001 &{{outerInstanceId}}
            PrefabInstance:
              m_SourcePrefab: {fileID: 100100000, guid: {{variantGuid}}, type: 3}
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications:
                - target: {fileID: {{variantRendererId}}, guid: {{variantGuid}},
                    type: 3}
                  propertyPath: m_Materials.Array.data[1]
                  value:
                    objectReference: {fileID: 2100000, guid: {{secondMaterialGuid}}, type: 2}
                - target: {fileID: {{20 ^ firstInstanceId}}, guid: {{variantGuid}}, type: 3}
                  propertyPath: m_LocalPosition.y
                  value: 8
                  objectReference: {fileID: 0}
            """;

        var result = UnityPrefabParser.ParseResolved(outer, outerGuid, (guid, _) => guid switch
        {
            var value when value == variantGuid => variant,
            var value when value == baseGuid => basePrefab,
            _ => null
        });

        var root = Assert.Single(result.Roots);
        Assert.Null(root.SourcePrefab);
        Assert.Equal(new Vector3Value(4, 8, 3), root.Position);
        Assert.Equal(new Vector3Value(2, 2, 2), root.Scale);
        Assert.Equal(meshGuid, root.Mesh?.Guid);
        Assert.Equal(firstMaterialGuid, root.Materials[0].Guid);
        Assert.Equal(secondMaterialGuid, root.Materials[1].Guid);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("could not be mapped safely", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("retained for diagnostics", StringComparison.Ordinal));
    }

    [Fact]
    public void StopsNestedPrefabCyclesAndHonorsDepthAndCancellationLimits()
    {
        const string firstGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string secondGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        static string Instance(string guid) => $$"""
            --- !u!1001 &100
            PrefabInstance:
              m_SourcePrefab: {fileID: 100100000, guid: {{guid}}, type: 3}
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications: []
            """;

        var cyclic = UnityPrefabParser.ParseResolved(Instance(secondGuid), firstGuid, (guid, _) => guid switch
        {
            secondGuid => Instance(firstGuid),
            firstGuid => Instance(secondGuid),
            _ => null
        });
        Assert.Contains(cyclic.Warnings, warning => warning.Contains("cycle detected", StringComparison.Ordinal));
        Assert.Equal(firstGuid, Assert.Single(cyclic.Roots).SourcePrefab?.Guid);

        var limited = UnityPrefabParser.ParseResolved(Instance(secondGuid), firstGuid, (_, _) => Instance(firstGuid), maximumNestedDepth: 0);
        Assert.Contains(limited.Warnings, warning => warning.Contains("depth limit", StringComparison.Ordinal));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => UnityPrefabParser.ParseResolved(Instance(secondGuid), firstGuid, (_, _) => null, cancellation.Token));
    }

    [Fact]
    public void KeepsCombinedHierarchyWithinThePerPrefabDepthLimit()
    {
        const string sourceGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const long instanceId = 9000;
        static string Hierarchy(int count, long firstObjectId, long firstTransformId, long? instance = null, string? sourceGuid = null)
        {
            var yaml = new StringBuilder();
            for (var index = 0; index < count; index++)
            {
                var objectId = firstObjectId + index;
                var transformId = firstTransformId + index;
                yaml.AppendLine($"--- !u!1 &{objectId}");
                yaml.AppendLine("GameObject:");
                yaml.AppendLine("  m_Component:");
                yaml.AppendLine($"  - component: {{fileID: {transformId}}}");
                yaml.AppendLine($"  m_Name: Node{index}");
                if (index == count - 1 && instance.HasValue) yaml.AppendLine($"  m_PrefabInstance: {{fileID: {instance.Value}}}");
                yaml.AppendLine($"--- !u!4 &{transformId}");
                yaml.AppendLine("Transform:");
                yaml.AppendLine($"  m_GameObject: {{fileID: {objectId}}}");
                yaml.AppendLine($"  m_Father: {{fileID: {(index == 0 ? 0 : firstTransformId + index - 1)}}}");
                yaml.AppendLine("  m_LocalPosition: {x: 0, y: 0, z: 0}");
                yaml.AppendLine("  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}");
                yaml.AppendLine("  m_LocalScale: {x: 1, y: 1, z: 1}");
            }
            if (instance.HasValue)
            {
                yaml.AppendLine($"--- !u!1001 &{instance.Value}");
                yaml.AppendLine("PrefabInstance:");
                yaml.AppendLine($"  m_SourcePrefab: {{fileID: 100100000, guid: {sourceGuid}, type: 3}}");
                yaml.AppendLine("  m_Modification:");
                yaml.AppendLine($"    m_TransformParent: {{fileID: {firstTransformId + count - 1}}}");
                yaml.AppendLine("    m_Modifications: []");
            }
            return yaml.ToString();
        }
        static int MaximumDepth(IEnumerable<PrefabNode> roots)
        {
            var maximum = 0;
            var pending = new Stack<(PrefabNode Node, int Depth)>(roots.Select(x => (x, 1)));
            while (pending.Count > 0)
            {
                var (node, depth) = pending.Pop();
                maximum = Math.Max(maximum, depth);
                foreach (var child in node.Children) pending.Push((child, depth + 1));
            }
            return maximum;
        }

        var outer = Hierarchy(250, 1000, 2000, instanceId, sourceGuid);
        var nested = Hierarchy(20, 5000, 6000);
        var result = UnityPrefabParser.ParseResolved(outer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", (guid, _) => guid == sourceGuid ? nested : null);

        Assert.True(MaximumDepth(result.Roots) <= 256);
        Assert.Contains(result.Warnings, warning => warning.Contains("trimmed to keep the combined hierarchy", StringComparison.Ordinal));
    }

    [Fact]
    public void AppliesIndexedRendererOverridesAndRejectsAmbiguousOrInvalidTargets()
    {
        const string sourceGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string oldMaterialGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string newMaterialGuid = "cccccccccccccccccccccccccccccccc";
        static PrefabNode Renderer(long gameObjectId, long componentId, string guid, string materialGuid)
        {
            var node = new PrefabNode { FileId = gameObjectId, Name = $"Renderer {gameObjectId}" };
            node.RendererComponentFileIds.Add(componentId);
            node.RendererSourceIdentities.Add(new PrefabSourceObjectIdentity(guid, componentId));
            node.Materials.Add(new UnityAssetReference(2100000, materialGuid));
            return node;
        }

        var first = Renderer(1, 10, sourceGuid, oldMaterialGuid);
        var duplicate = Renderer(2, 10, sourceGuid, oldMaterialGuid);
        var single = Renderer(3, 11, sourceGuid, oldMaterialGuid);
        var warnings = new List<string>();
        UnityPrefabParser.ApplyMaterialOverrides([first, duplicate, single],
        [
            new PrefabMaterialOverride(10, sourceGuid, 0, new UnityAssetReference(2100000, newMaterialGuid)),
            new PrefabMaterialOverride(11, sourceGuid, 1, new UnityAssetReference(2100000, newMaterialGuid))
        ], warnings, CancellationToken.None);

        Assert.Equal(oldMaterialGuid, Assert.Single(first.Materials).Guid);
        Assert.Equal(oldMaterialGuid, Assert.Single(duplicate.Materials).Guid);
        Assert.Equal(oldMaterialGuid, Assert.Single(single.Materials).Guid);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("matched multiple renderers", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("outside the source renderer's material list", StringComparison.Ordinal));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => UnityPrefabParser.ApplyMaterialOverrides([first], [], [], cancellation.Token));
    }

    [Fact]
    public void BreaksCyclicParentLinksAndKeepsThePrefabHierarchyReadable()
    {
        const string yaml = """
            --- !u!1 &100
            GameObject:
              m_Component:
              - component: {fileID: 101}
              m_Name: First
            --- !u!4 &101
            Transform:
              m_GameObject: {fileID: 100}
              m_Father: {fileID: 201}
            --- !u!1 &200
            GameObject:
              m_Component:
              - component: {fileID: 201}
              m_Name: Second
            --- !u!4 &201
            Transform:
              m_GameObject: {fileID: 200}
              m_Father: {fileID: 101}
            """;

        var result = UnityPrefabParser.Parse(yaml);

        Assert.Single(result.Roots);
        Assert.Contains(result.Warnings, warning => warning.Contains("cycle detected", StringComparison.OrdinalIgnoreCase));
        var first = Assert.Single(result.Roots);
        Assert.Single(first.Children);
    }

    [Fact]
    public void CutsPrefabHierarchiesDeeperThanTheSupportedLimit()
    {
        const int count = 300;
        var documents = Enumerable.Range(1, count).Select(index => $$"""
            --- !u!1 &{{index * 2}}
            GameObject:
              m_Component:
              - component: {fileID: {{index * 2 + 1}}}
              m_Name: Node{{index}}
            --- !u!4 &{{index * 2 + 1}}
            Transform:
              m_GameObject: {fileID: {{index * 2}}}
              m_Father: {fileID: {{(index == 1 ? 0 : (index - 1) * 2 + 1)}}}
            """).ToArray();

        var result = UnityPrefabParser.Parse(string.Join(Environment.NewLine, documents));

        Assert.Equal(2, result.Roots.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("exceeded 256", StringComparison.Ordinal));
        foreach (var root in result.Roots)
        {
            var nodeCount = 0;
            var pending = new Stack<PrefabNode>();
            pending.Push(root);
            while (pending.TryPop(out var current))
            {
                nodeCount++;
                foreach (var child in current.Children) pending.Push(child);
            }
            Assert.InRange(nodeCount, 1, 256);
        }
    }

    [Fact]
    public void ReportsReferencesThatCannotBeResolvedWithoutGuids()
    {
        const string yaml = """
            --- !u!1 &100
            GameObject:
              m_Component:
              - component: {fileID: 101}
              - component: {fileID: 102}
              - component: {fileID: 103}
              m_Name: Unresolved
            --- !u!4 &101
            Transform:
              m_GameObject: {fileID: 100}
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 0, y: 0, z: 0}
              m_LocalScale: {x: 1, y: 1, z: 1}
              m_Father: {fileID: 0}
            --- !u!33 &102
            MeshFilter:
              m_GameObject: {fileID: 100}
              m_Mesh: {fileID: 4300000}
            --- !u!23 &103
            MeshRenderer:
              m_GameObject: {fileID: 100}
              m_Materials:
              - {fileID: 2100000}
            """;

        var result = UnityPrefabParser.Parse(yaml);

        Assert.Contains(result.Warnings, x => x.Contains("mesh reference without a GUID", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, x => x.Contains("material reference without a GUID", StringComparison.Ordinal));
    }
}
