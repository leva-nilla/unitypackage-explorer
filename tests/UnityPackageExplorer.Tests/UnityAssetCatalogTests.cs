using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityAssetCatalogTests
{
    [Fact]
    public void BuildsFoldersAndFiltersAssetsWithoutDroppingMatchingParents()
    {
        var entries = new[]
        {
            new UnityPackageEntry("a".PadLeft(32, '0'), "Assets/Models/Avatar.fbx", true, true),
            new UnityPackageEntry("b".PadLeft(32, '0'), "Assets/Models/Body.png", true, true),
            new UnityPackageEntry("c".PadLeft(32, '0'), "Assets/Prefabs/Avatar.prefab", true, true)
        };

        var tree = UnityAssetCatalog.BuildTree(entries);
        var filtered = UnityAssetCatalog.FilterTree(tree, "avatar", "Prefab").ToArray();

        var assets = Assert.Single(filtered);
        Assert.Equal("Assets", assets.PathName);
        var prefabs = Assert.Single(assets.Children);
        Assert.Equal("Prefabs", prefabs.Name);
        var prefab = Assert.Single(prefabs.Children);
        Assert.Equal("Assets/Prefabs/Avatar.prefab", prefab.PathName);
        Assert.Equal("Prefab", prefab.Entry?.Kind);
    }

    [Fact]
    public void ClassifiesAndFiltersFileKindsNotListedInTheInitialUi()
    {
        var audio = new UnityPackageEntry("a".PadLeft(32, '0'), "Assets/Audio/Theme.wav", true, false);
        var tree = UnityAssetCatalog.BuildTree([audio]);

        var filtered = UnityAssetCatalog.FilterTree(tree, string.Empty, "WAV").ToArray();

        Assert.Equal("WAV", audio.Kind);
        Assert.Single(filtered[0].Children[0].Children);
    }

    [Fact]
    public void ResolvesPrefabDependenciesInsidePackagesFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-project-test-{Guid.NewGuid():N}");
        var prefabPath = Path.Combine(root, "Packages", "com.example", "Prefabs", "Item.prefab");
        var modelPath = Path.Combine(root, "Packages", "com.example", "Models", "Item.fbx");
        var cachedModelPath = Path.Combine(root, "Library", "PackageCache", "com.example@1.0.0", "Models", "Cached.fbx");
        var guid = "d".PadLeft(32, '0');
        var cachedGuid = "e".PadLeft(32, '0');
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedModelPath)!);
            File.WriteAllText(prefabPath, "prefab");
            File.WriteAllText(modelPath, "fbx");
            File.WriteAllText(modelPath + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
            File.WriteAllText(cachedModelPath, "cached fbx");
            File.WriteAllText(cachedModelPath + ".meta", $"fileFormatVersion: 2\nguid: {cachedGuid}\n");

            var catalog = UnityAssetCatalog.OpenProjectPrefab(prefabPath);

            Assert.Equal(modelPath, catalog.ResolvePath(guid));
            Assert.Equal(cachedModelPath, catalog.ResolvePath(cachedGuid));
            Assert.Contains(catalog.Entries, x => x.PathName == "Packages/com.example/Models/Item.fbx");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsMaterialNameAndTextureGuidFromProjectAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-material-test-{Guid.NewGuid():N}");
        var prefabPath = Path.Combine(root, "Assets", "Prefabs", "Item.prefab");
        var materialPath = Path.Combine(root, "Assets", "Materials", "Leather.mat");
        var texturePath = Path.Combine(root, "Assets", "Textures", "Leather.png");
        const string materialGuid = "11111111111111111111111111111111";
        const string textureGuid = "22222222222222222222222222222222";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
            File.WriteAllText(prefabPath, "prefab");
            File.WriteAllText(materialPath, $"m_Name: Leather\nm_SavedProperties:\n  m_TexEnvs:\n  - _MainTex:\n      m_Texture: {{fileID: 2800000, guid: {textureGuid}, type: 3}}\n  - _BumpMap:\n      m_Texture: {{fileID: 2800000, guid: 33333333333333333333333333333333, type: 3}}\n");
            File.WriteAllText(materialPath + ".meta", $"fileFormatVersion: 2\nguid: {materialGuid}\n");
            File.WriteAllBytes(texturePath, [0]);
            File.WriteAllText(texturePath + ".meta", $"fileFormatVersion: 2\nguid: {textureGuid}\n");

            var catalog = UnityAssetCatalog.OpenProjectPrefab(prefabPath);

            Assert.Equal("Leather", catalog.ReadMaterialName(materialGuid));
            Assert.Equal(textureGuid, Assert.Single(catalog.ReadMaterialTextureGuids(materialGuid)));
            Assert.Equal("33333333333333333333333333333333", Assert.Single(catalog.ReadUnsupportedMaterialTextureGuids(materialGuid)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
