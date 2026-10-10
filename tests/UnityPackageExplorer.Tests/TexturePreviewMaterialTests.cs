using System.Reflection;
using HelixToolkit.SharpDX.Model;
using UnityPackageExplorer.App;

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
}
