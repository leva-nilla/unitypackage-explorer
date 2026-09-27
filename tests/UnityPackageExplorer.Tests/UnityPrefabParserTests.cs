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
