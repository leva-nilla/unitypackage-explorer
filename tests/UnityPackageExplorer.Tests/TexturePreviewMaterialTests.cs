using System.Reflection;
using HelixToolkit.SharpDX.Model;
using UnityPackageExplorer.App;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class TexturePreviewMaterialTests
{
    [Fact]
    public void TexturedPreviewMaterialUsesLowAmbientAndSpecularLight()
    {
        var factory = typeof(MainWindow).GetMethod("CreateTexturePreviewMaterial", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(factory);

        var material = Assert.IsType<PhongMaterialCore>(factory!.Invoke(null, ["preview-texture.png"]));

        Assert.True(material.RenderDiffuseMap);
        Assert.NotNull(material.DiffuseMap);
        Assert.Equal(0.08f, material.AmbientColor.Red, 3);
        Assert.Equal(0.08f, material.AmbientColor.Green, 3);
        Assert.Equal(0.08f, material.AmbientColor.Blue, 3);
        Assert.Equal(0.06f, material.SpecularColor.Red, 3);
        Assert.Equal(0.06f, material.SpecularColor.Green, 3);
        Assert.Equal(0.06f, material.SpecularColor.Blue, 3);
        Assert.Equal(0f, material.EmissiveColor.Red, 3);
        Assert.Equal(24f, material.SpecularShininess);
    }

    [Fact]
    public void ColorOnlyPreviewMaterialUsesTintWithoutCreatingTextureMap()
    {
        var factory = typeof(MainWindow).GetMethod("CreatePreviewMaterial", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(factory);

        var material = Assert.IsType<PhongMaterialCore>(factory!.Invoke(null, [null, new UnityMaterialColor(0.2f, 0.4f, 0.8f, 0.3f), 0.3f]));

        Assert.False(material.RenderDiffuseMap);
        Assert.Null(material.DiffuseMap);
        Assert.Equal(0.2f, material.DiffuseColor.Red, 3);
        Assert.Equal(0.4f, material.DiffuseColor.Green, 3);
        Assert.Equal(0.8f, material.DiffuseColor.Blue, 3);
        Assert.Equal(0.3f, material.DiffuseColor.Alpha, 3);
    }

    [Fact]
    public void TintedTextureKeepsTextureBindingAndAppliesMaterialColor()
    {
        var factory = typeof(MainWindow).GetMethod("CreatePreviewMaterial", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(factory);

        var material = Assert.IsType<PhongMaterialCore>(factory!.Invoke(null, ["main-texture.png", new UnityMaterialColor(0.7f, 0.4f, 0.2f, 0.15f), 1f]));

        Assert.True(material.RenderDiffuseMap);
        Assert.NotNull(material.DiffuseMap);
        Assert.Equal(0.7f, material.DiffuseColor.Red, 3);
        Assert.Equal(0.4f, material.DiffuseColor.Green, 3);
        Assert.Equal(0.2f, material.DiffuseColor.Blue, 3);
        Assert.Equal(1f, material.DiffuseColor.Alpha, 3);
    }
}
