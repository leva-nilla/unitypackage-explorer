using System.Globalization;
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
    public void BuildTreeStopsWhenCancellationIsRequested()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            UnityAssetCatalog.BuildTree(
                [new UnityPackageEntry("a".PadLeft(32, '0'), "Assets/Models/Avatar.fbx", true, true)],
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
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
        var cutoutMaterialPath = Path.Combine(root, "Assets", "Materials", "Leaf.mat");
        var texturePath = Path.Combine(root, "Assets", "Textures", "Leather.png");
        var shaderPath = Path.Combine(root, "Assets", "lilToon", "Shader", "lts.shader");
        var originalCulture = CultureInfo.CurrentCulture;
        const string materialGuid = "11111111111111111111111111111111";
        const string textureGuid = "22222222222222222222222222222222";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
            File.WriteAllText(prefabPath, "prefab");
            File.WriteAllText(materialPath, $"Material:\n  m_Name: Leather\n  m_Shader: {{fileID: 4800000, guid: 165365ab7100a044ca85fc8c33548a62, type: 3}}\n  m_SavedProperties:\n    m_TexEnvs:\n    - _MainTex:\n        m_Texture: {{fileID: 2800000, guid: {textureGuid}, type: 3}}\n    - _BumpMap:\n        m_Texture: {{fileID: 2800000, guid: 33333333333333333333333333333333, type: 3}}\n    m_Floats:\n    - _Cutoff: 0.37\n    - _TransparentMode: 1\n    - _AlphaMaskMode: 1\n    - _UseOutline: 0\n    m_Colors:\n    - _Color: {{r: 0.25, g: 0.5, b: 0.75, a: 0.4}}\n");
            File.WriteAllText(materialPath + ".meta", $"fileFormatVersion: 2\nguid: {materialGuid}\n");
            File.WriteAllText(cutoutMaterialPath, "Material:\n  m_Name: Leaf\n  m_Shader: {fileID: 4800000, guid: 85d6126cae43b6847aff4b13f4adb8ec, type: 3}\n  m_SavedProperties:\n    m_Floats:\n    - _Cutoff: 0.37\n");
            File.WriteAllText(cutoutMaterialPath + ".meta", "fileFormatVersion: 2\nguid: 88888888888888888888888888888888\n");
            File.WriteAllText(shaderPath, "Shader \"lilToon\" {}\n");
            File.WriteAllText(shaderPath + ".meta", "fileFormatVersion: 2\nguid: 165365ab7100a044ca85fc8c33548a62\n");
            File.WriteAllBytes(texturePath, [0]);
            File.WriteAllText(texturePath + ".meta", $"fileFormatVersion: 2\nguid: {textureGuid}\n");

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var catalog = UnityAssetCatalog.OpenProjectPrefab(prefabPath);

            var preview = catalog.ReadMaterialInfo(materialGuid);

            Assert.NotNull(preview);
            Assert.Equal("Leather", preview.Name);
            Assert.Equal(textureGuid, Assert.Single(preview.TextureGuids));
            Assert.Equal(new UnityMaterialColor(0.25f, 0.5f, 0.75f, 0.4f), preview.Color);
            Assert.Equal(UnityMaterialRenderMode.Transparent, preview.RenderMode);
            Assert.Equal("lts", preview.ShaderName);
            Assert.True(preview.IsLilToonShaderAsset);
            Assert.Equal(0.37f, preview.Cutoff);
            Assert.Equal(["_AlphaMaskMode"], preview.EnabledUnsupportedFeatures);
            var cutout = catalog.ReadMaterialInfo("88888888888888888888888888888888");
            Assert.NotNull(cutout);
            Assert.Equal(UnityMaterialRenderMode.Cutout, cutout.RenderMode);
            Assert.Equal("33333333333333333333333333333333", Assert.Single(preview.UnsupportedTextureGuids));
            Assert.Equal("Leather", catalog.ReadMaterialName(materialGuid));
            Assert.Equal(textureGuid, Assert.Single(catalog.ReadMaterialTextureGuids(materialGuid)));
            Assert.Equal("33333333333333333333333333333333", Assert.Single(catalog.ReadUnsupportedMaterialTextureGuids(materialGuid)));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CutoffAloneDoesNotClassifyMaterialAndMalformedColorIsIgnored()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-material-malformed-{Guid.NewGuid():N}");
        var prefabPath = Path.Combine(root, "Assets", "Item.prefab");
        var materialPath = Path.Combine(root, "Assets", "Broken.mat");
        const string materialGuid = "55555555555555555555555555555555";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(prefabPath)!);
            File.WriteAllText(prefabPath, "prefab");
            File.WriteAllText(materialPath, "m_Name: Broken\nm_SavedProperties:\n  m_Floats:\n  - _Cutoff: not-a-number\n  m_Colors:\n  - _Color: {r: NaN, g: 0.2, b: 0.3, a: 1}\n");
            File.WriteAllText(materialPath + ".meta", $"fileFormatVersion: 2\nguid: {materialGuid}\n");
            var info = UnityAssetCatalog.OpenProjectPrefab(prefabPath).ReadMaterialInfo(materialGuid);
            Assert.NotNull(info);
            Assert.Equal(UnityMaterialRenderMode.Unknown, info.RenderMode);
            Assert.Null(info.Cutoff);
            Assert.Null(info.Color);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnknownLilToonVariantIsNotGuessedFromCutoffOrTransparentModeValue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-unknown-shader-{Guid.NewGuid():N}");
        var prefabPath = Path.Combine(root, "Assets", "Item.prefab");
        var shaderPath = Path.Combine(root, "Assets", "lilToon", "Shader", "ltsfuronly.shader");
        var materialPath = Path.Combine(root, "Assets", "Item.mat");
        const string shaderGuid = "66666666666666666666666666666666";
        const string materialGuid = "77777777777777777777777777777777";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
            File.WriteAllText(prefabPath, "prefab");
            File.WriteAllText(shaderPath, "Shader \"lilToonFurOnly\" {}\n");
            File.WriteAllText(shaderPath + ".meta", $"fileFormatVersion: 2\nguid: {shaderGuid}\n");
            File.WriteAllText(materialPath, $"Material:\n  m_Name: Unknown\n  m_Shader: {{fileID: 4800000, guid: {shaderGuid}, type: 3}}\n  stringTagMap: {{RenderType: Opaque}}\n  m_SavedProperties:\n    m_Floats:\n    - _TransparentMode: 1\n    - _Cutoff: 0.4\n");
            File.WriteAllText(materialPath + ".meta", $"fileFormatVersion: 2\nguid: {materialGuid}\n");

            var info = UnityAssetCatalog.OpenProjectPrefab(prefabPath).ReadMaterialInfo(materialGuid);
            Assert.NotNull(info);
            Assert.Equal("ltsfuronly", info.ShaderName);
            Assert.True(info.IsLilToonShaderAsset);
            Assert.Equal(UnityMaterialRenderMode.Unknown, info.RenderMode);
            Assert.Null(info.LilToon);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParsesLilToonPropertiesTexturesAndKnownRenderVariantsWithoutClampingColors()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-liltoon-data-{Guid.NewGuid():N}");
        var prefabPath = Path.Combine(root, "Assets", "Item.prefab");
        var shaderPath = Path.Combine(root, "Assets", "lilToon", "Shader", "lts.shader");
        var materialPath = Path.Combine(root, "Assets", "Item.mat");
        const string materialGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string mainGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string normalGuid = "cccccccccccccccccccccccccccccccc";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
            File.WriteAllText(prefabPath, "prefab");
            File.WriteAllText(shaderPath, "Shader \"lilToon\" {}\n");
            File.WriteAllText(shaderPath + ".meta", "fileFormatVersion: 2\nguid: df12117ecd77c31469c224178886498e\n");
            File.WriteAllText(materialPath, $"Material:\n  m_Name: Surface\n  m_Shader: {{fileID: 4800000, guid: df12117ecd77c31469c224178886498e, type: 3}}\n  m_SavedProperties:\n    m_TexEnvs:\n    - _MainTex:\n        m_Texture: {{fileID: 2800000, guid: {mainGuid}, type: 3}}\n        m_Scale: {{x: 2, y: 3}}\n        m_Offset: {{x: 0.25, y: -0.5}}\n    - _BumpMap:\n        m_Texture: {{fileID: 2800000, guid: {normalGuid}, type: 3}}\n        m_Scale: {{x: 1, y: 1}}\n        m_Offset: {{x: 0, y: 0}}\n    m_Ints:\n    - _FeatureFlag: 2\n    m_Floats:\n    - _UseMatCap: 1\n    - _UseEmission: 1\n    - _Cutoff: 0.5\n    - _BadValue: Infinity\n    m_Colors:\n    - _Color: {{r: 1.5, g: 0.5, b: 0.25, a: 0.8}}\n    - _EmissionColor: {{x: 2, y: 0.3, z: 0.4, w: 1}}\n");
            File.WriteAllText(materialPath + ".meta", $"fileFormatVersion: 2\nguid: {materialGuid}\n");

            var info = UnityAssetCatalog.OpenProjectPrefab(prefabPath).ReadMaterialInfo(materialGuid)!;
            var data = Assert.IsType<LilToonMaterialData>(info.LilToon);
            Assert.Equal("opaque", data.RenderMode);
            Assert.Equal(1.5f, Assert.IsType<float[]>(data.Properties["_Color"])[0]);
            Assert.Equal(0.8f, Assert.IsType<float[]>(data.Properties["_Color"])[3]);
            Assert.Equal(2f, Assert.IsType<float[]>(data.Properties["_EmissionColor"])[0]);
            Assert.Equal(1f, Assert.IsType<float>(data.Properties["_UseMatCap"]));
            Assert.Equal(2, Assert.IsType<int>(data.Properties["_FeatureFlag"]));
            Assert.False(data.Properties.ContainsKey("_BadValue"));
            Assert.Equal(new[] { 2f, 3f, 0.25f, -0.5f }, Assert.IsType<float[]>(data.Properties["_MainTex_ST"]));
            Assert.Equal(new[] { "_MainTex", "_BumpMap" }, data.Textures.Select(x => x.Property));
            Assert.Equal(mainGuid, data.Textures[0].Guid);

            var cutoutPath = Path.Combine(root, "Assets", "Cutout.mat");
            File.WriteAllText(cutoutPath, "Material:\n  m_Name: Cutout\n  m_Shader: {fileID: 4800000, guid: 85d6126cae43b6847aff4b13f4adb8ec, type: 3}\n  m_SavedProperties:\n    m_Floats:\n    - _Cutoff: 0.5\n");
            File.WriteAllText(cutoutPath + ".meta", "fileFormatVersion: 2\nguid: dddddddddddddddddddddddddddddddd\n");
            var cutout = UnityAssetCatalog.OpenProjectPrefab(prefabPath).ReadMaterialInfo("dddddddddddddddddddddddddddddddd")!;
            Assert.Equal(UnityMaterialRenderMode.Cutout, cutout.RenderMode);
            Assert.Equal("cutout", cutout.LilToon?.RenderMode);
            Assert.Null(cutout.LilToon?.ShaderVariant);

            var nonLilPath = Path.Combine(root, "Assets", "Other.mat");
            File.WriteAllText(nonLilPath, "Material:\n  m_Name: Other\n  m_Shader: {fileID: 4800000, guid: eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee, type: 3}\n  m_SavedProperties:\n    m_Floats:\n    - _UseEmission: 1\n");
            File.WriteAllText(nonLilPath + ".meta", "fileFormatVersion: 2\nguid: ffffffffffffffffffffffffffffffff\n");
            Assert.Null(UnityAssetCatalog.OpenProjectPrefab(prefabPath).ReadMaterialInfo("ffffffffffffffffffffffffffffffff")!.LilToon);

            var unrelatedShader = Path.Combine(root, "Assets", "Other", "lts.shader");
            Directory.CreateDirectory(Path.GetDirectoryName(unrelatedShader)!);
            File.WriteAllText(unrelatedShader, "Shader \"Unrelated/Surface\" {}\n");
            File.WriteAllText(unrelatedShader + ".meta", "guid: eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\n");
            var unrelatedInfo = UnityAssetCatalog.OpenProjectPrefab(prefabPath).ReadMaterialInfo("ffffffffffffffffffffffffffffffff")!;
            Assert.Null(unrelatedInfo.LilToon);
            Assert.Equal(UnityMaterialRenderMode.Unknown, unrelatedInfo.RenderMode);

            File.WriteAllText(nonLilPath, "Material:\n  m_Name: Multi\n  m_Shader: {fileID: 4800000, guid: 51b2dee0ab07bd84d8147601ff89e511, type: 3}\n  m_SavedProperties:\n    m_Floats:\n    - _TransparentMode: 1\n    - _UseMatCap: 1\n");
            var multi = UnityAssetCatalog.OpenProjectPrefab(prefabPath).ReadMaterialInfo("ffffffffffffffffffffffffffffffff")!;
            Assert.Equal("cutout", multi.LilToon?.RenderMode);
            Assert.Equal("multi-outline", multi.LilToon?.ShaderVariant);
            File.WriteAllText(nonLilPath, File.ReadAllText(nonLilPath).Replace("_TransparentMode: 1", "_TransparentMode: 3", StringComparison.Ordinal));
            Assert.Null(UnityAssetCatalog.OpenProjectPrefab(prefabPath).ReadMaterialInfo("ffffffffffffffffffffffffffffffff")!.LilToon);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

}
