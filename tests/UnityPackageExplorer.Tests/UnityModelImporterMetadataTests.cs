using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityModelImporterMetadataTests
{
    private const string Guid = "11111111111111111111111111111111";

    [Theory]
    [InlineData("M_Body", "M_Body")]
    [InlineData("'Body''s material'", "Body's material")]
    [InlineData("\"\\u9854\"", "顔")]
    public void ReadsOnlyMaterialEntriesInExternalObjects(string yamlName, string name)
    {
        var map = UnityModelImporterMetadata.ParseMaterialRemaps($$"""
            ModelImporter:
              externalObjects:
              - first:
                  type: UnityEngine:Texture2D
                  name: Ignore
                second: {fileID: 2800000, guid: {{Guid}}, type: 2}
              - first:
                  type: UnityEngine:Material
                  assembly: UnityEngine.CoreModule
                  name: {{yamlName}}
                second: {fileID: 2100000, guid: {{Guid}}, type: 2}
              materials:
                name: NotAnExternalObject
                second: {fileID: 2100000, guid: {{Guid}}, type: 2}
            """);

        Assert.Equal(new UnityAssetReference(2100000, Guid), Assert.Single(map).Value);
        Assert.True(map.ContainsKey(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ModelImporter:\n  externalObjects: {}")]
    [InlineData("TextureImporter:\n  externalObjects:\n  - first:\n      type: UnityEngine:Material\n      name: M_Body\n    second: {fileID: 2100000, guid: 11111111111111111111111111111111, type: 2}")]
    [InlineData("ModelImporter:\n  externalObjects:\n  - first:\n      type: UnityEngine:Material\n      name: M_Body\n    second: {fileID: 0}")]
    public void MissingOrUnsupportedMetadataProducesNoRemaps(string? text)
        => Assert.Empty(UnityModelImporterMetadata.ParseMaterialRemaps(text));

    [Fact]
    public void ConflictingNamesAreNotResolvedByLastEntryWins()
    {
        var entry = $$"""
              - first:
                  type: UnityEngine:Material
                  name: M_Body
                second: {fileID: 2100000, guid: {{Guid}}, type: 2}
            """;
        entry = string.Join("\n", entry.Split('\n').Select(line => "  " + line));
        var text = "ModelImporter:\n  externalObjects:\n" + entry + "\n"
            + entry.Replace(Guid, "22222222222222222222222222222222", StringComparison.Ordinal) + "\n" + entry;

        Assert.Empty(UnityModelImporterMetadata.ParseMaterialRemaps(text));
    }
}
