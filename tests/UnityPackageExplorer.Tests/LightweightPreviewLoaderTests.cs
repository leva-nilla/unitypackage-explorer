using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityPackageExplorer.Core;
using UnityPackageExplorer.Rendering;

namespace UnityPackageExplorer.Tests;

public sealed class LightweightPreviewLoaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadsShowcaseZipOrPackageWithSceneAndOnlyBoundTextures(bool useSinglePackage)
    {
        var root = CreateTemporaryDirectory();
        var source = Path.Combine(root, "sample");
        Directory.CreateDirectory(source);
        var sourceArchive = Path.Combine(AppContext.BaseDirectory, "TestData", "UnityPackageExplorer-Showcase.zip");
        var archivePath = useSinglePackage ? CopyInnerPackage(sourceArchive, source) : sourceArchive;
        var catalog = useSinglePackage ? UnityAssetCatalog.OpenPackage(archivePath) : UnityAssetCatalog.OpenZip(archivePath);
        var selected = Assert.Single(catalog.Entries, entry => entry.Extension == ".prefab");
        var tempRoot = Path.Combine(root, "prepared");
        try
        {
            using var preview = await LightweightPreviewLoader.LoadAsync(catalog, selected, tempRoot, CancellationToken.None);

            Assert.Single(preview.Scenes);
            Assert.NotEmpty(preview.MaterialBindings);
            Assert.DoesNotContain(preview.Warnings, warning => warning.Contains("not found", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(tempRoot));
            var stagedImages = Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories)
                .Where(path => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var boundImages = preview.MaterialBindings.Where(binding => binding.AssetPath is not null)
                .Select(binding => Path.GetFullPath(binding.AssetPath!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(boundImages, stagedImages);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotStageAnUnselectedSiblingImage()
    {
        var root = CreateTemporaryDirectory();
        var projectRoot = Path.Combine(root, "project");
        var (prefabPath, modelDirectory) = CopyShowcaseProject(projectRoot);
        var unusedGuid = "33333333333333333333333333333333";
        File.WriteAllBytes(Path.Combine(modelDirectory, "Unselected.png"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(modelDirectory, "Unselected.png.meta"), $"fileFormatVersion: 2\nguid: {unusedGuid}\n");
        var catalog = UnityAssetCatalog.OpenProjectPrefab(prefabPath);
        var selected = Assert.Single(catalog.Entries, entry => entry.Extension == ".prefab");
        var tempRoot = Path.Combine(root, "prepared");
        try
        {
            using var preview = await LightweightPreviewLoader.LoadAsync(catalog, selected, tempRoot, CancellationToken.None);

            Assert.Single(preview.Scenes);
            Assert.DoesNotContain(Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories), path =>
                Path.GetFileName(path).Contains(unusedGuid, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreCanceledLoadProducesNoPreparedOutput()
    {
        var root = CreateTemporaryDirectory();
        var sample = Path.Combine(AppContext.BaseDirectory, "TestData", "UnityPackageExplorer-Showcase.zip");
        var catalog = UnityAssetCatalog.OpenZip(sample);
        var selected = Assert.Single(catalog.Entries, entry => entry.Extension == ".prefab");
        var tempRoot = Path.Combine(root, "must-not-exist");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                LightweightPreviewLoader.LoadAsync(catalog, selected, tempRoot, cancellation.Token));
            Assert.False(Directory.Exists(tempRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFbxReturnsWarningWithoutChangingSourcePrefab()
    {
        var root = CreateTemporaryDirectory();
        var prefabPath = Path.Combine(root, "Assets", "Broken.prefab");
        var prefabGuid = "11111111111111111111111111111111";
        var missingModelGuid = "22222222222222222222222222222222";
        Directory.CreateDirectory(Path.GetDirectoryName(prefabPath)!);
        File.WriteAllText(prefabPath, CreateMissingModelPrefab(missingModelGuid));
        File.WriteAllText(prefabPath + ".meta", $"fileFormatVersion: 2\nguid: {prefabGuid}\n");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(prefabPath));
        var catalog = UnityAssetCatalog.OpenProjectPrefab(prefabPath);
        var selected = Assert.Single(catalog.Entries, entry => entry.Extension == ".prefab");
        try
        {
            using var preview = await LightweightPreviewLoader.LoadAsync(catalog, selected, Path.Combine(root, "prepared"), CancellationToken.None);

            Assert.Empty(preview.Scenes);
            Assert.Contains(preview.Warnings, warning => warning.Contains(missingModelGuid, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(prefabPath)));
            Assert.Equal(new[] { "Broken.prefab", "Broken.prefab.meta" }, Directory.EnumerateFiles(Path.GetDirectoryName(prefabPath)!).Select(Path.GetFileName).Order().ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoPhaseLoadMatchesSinglePhaseLoad()
    {
        var root = CreateTemporaryDirectory();
        var archive = Path.Combine(AppContext.BaseDirectory, "TestData", "UnityPackageExplorer-Showcase.zip");
        var catalog = UnityAssetCatalog.OpenZip(archive);
        var selected = Assert.Single(catalog.Entries, entry => entry.Extension == ".prefab");
        var tempRoot = Path.Combine(root, "prepared");
        try
        {
            using var reference = await LightweightPreviewLoader.LoadAsync(catalog, selected, tempRoot, CancellationToken.None);
            using var geometry = await LightweightPreviewLoader.LoadGeometryAsync(catalog, selected, tempRoot, CancellationToken.None);

            Assert.Equal(reference.Scenes.Count, geometry.Scenes.Count);
            Assert.NotEmpty(geometry.Pending);
            // Phase 2 must resolve materials without mutating the live scene.
            var materialBefore = geometry.Pending[0].Meshes[0].Material?.Name;
            var plan = await LightweightPreviewLoader.CompleteMaterialsAsync(geometry, catalog, selected, tempRoot, CancellationToken.None);
            Assert.Equal(materialBefore, geometry.Pending[0].Meshes[0].Material?.Name);
            Assert.NotEmpty(plan.Plans[0].Specs);

            var bindings = new List<RenderingMaterialBinding>();
            foreach (var scenePlan in plan.Plans)
            {
                LightweightPreviewLoader.ApplyMaterialSpecs(scenePlan.Specs);
                bindings.AddRange(scenePlan.Bindings);
            }
            Assert.Equal(
                reference.MaterialBindings.Select(binding => (binding.MaterialGuid, binding.MaterialName, binding.TextureName, binding.Slot, binding.AssetPath)),
                bindings.Select(binding => (binding.MaterialGuid, binding.MaterialName, binding.TextureName, binding.Slot, binding.AssetPath)));
            Assert.Equal(
                reference.Warnings,
                geometry.Warnings.Concat(plan.Warnings).Distinct(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string CopyInnerPackage(string sampleZip, string outputDirectory)
    {
        using var archive = ZipFile.OpenRead(sampleZip);
        var entry = Assert.Single(archive.Entries, item => item.FullName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase));
        var output = Path.Combine(outputDirectory, "showcase.unitypackage");
        using var input = entry.Open();
        using var target = File.Create(output);
        input.CopyTo(target);
        return output;
    }

    private static (string PrefabPath, string ModelDirectory) CopyShowcaseProject(string projectRoot)
    {
        var sourceArchive = Path.Combine(AppContext.BaseDirectory, "TestData", "UnityPackageExplorer-Showcase.zip");
        var sourceCatalog = UnityAssetCatalog.OpenZip(sourceArchive);
        var prefab = Assert.Single(sourceCatalog.Entries, entry => entry.Extension == ".prefab");
        UnityPackageEntry? model = null;
        foreach (var entry in sourceCatalog.Entries.Where(entry => entry.PackageMemberPath == prefab.PackageMemberPath))
        {
            var assetPath = entry.AssetPathName ?? throw new InvalidDataException($"Missing package asset path for {entry.PathName}");
            var targetPath = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllBytes(targetPath, sourceCatalog.ReadAsset(entry) ?? []);
            if (sourceCatalog.ReadAsset(entry, "asset.meta") is { } meta)
                File.WriteAllBytes(targetPath + ".meta", meta);
            if (entry.Extension == ".fbx") model = entry;
        }
        var prefabPath = Path.Combine(projectRoot, (prefab.AssetPathName ?? throw new InvalidDataException("Missing showcase prefab path.")).Replace('/', Path.DirectorySeparatorChar));
        var modelPath = Path.Combine(projectRoot, (model?.AssetPathName ?? throw new InvalidDataException("Missing showcase model path.")).Replace('/', Path.DirectorySeparatorChar));
        return (prefabPath, Path.GetDirectoryName(modelPath)!);
    }

    private static string CreateMissingModelPrefab(string modelGuid) => $$"""
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!1 &100
        GameObject:
          m_Component:
          - component: {fileID: 101}
          - component: {fileID: 102}
          - component: {fileID: 103}
          m_Name: Broken
          m_PrefabInstance: {fileID: 0}
        --- !u!4 &101
        Transform:
          m_GameObject: {fileID: 100}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 0, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 0}
        --- !u!33 &102
        MeshFilter:
          m_GameObject: {fileID: 100}
          m_Mesh: {fileID: 4300000, guid: {{modelGuid}}, type: 3}
        --- !u!23 &103
        MeshRenderer:
          m_GameObject: {fileID: 100}
          m_Materials: []
        """;

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Environment.CurrentDirectory, ".artifacts", $"preview-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
