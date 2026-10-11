using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityPackageExplorer.App;

namespace UnityPackageExplorer.Tests;

public sealed class AlphaTexturePreparationTests
{
    [Fact]
    public void CutoutConversionPreservesRgbCachesTheSameMapAndChecksCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unity-material-cutout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "alpha.png");
        try
        {
            var pixels = new byte[] { 3, 17, 29, 255, 41, 53, 67, 128 };
            var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(source)) encoder.Save(stream);

            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var output = AlphaTexturePreparation.PrepareCutoutPng(source, 0.6f, 1f, cache, CancellationToken.None);
            Assert.Equal(output, AlphaTexturePreparation.PrepareCutoutPng(source, 0.6f, 1f, cache, CancellationToken.None));
            var decoded = BitmapFrame.Create(new Uri(output), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var outputPixels = new byte[8];
            decoded.CopyPixels(outputPixels, 8, 0);
            Assert.Equal(pixels[0..3], outputPixels[0..3]);
            Assert.Equal((byte)255, outputPixels[3]);
            Assert.Equal(pixels[4..7], outputPixels[4..7]);
            Assert.Equal((byte)0, outputPixels[7]);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var existingFiles = Directory.GetFiles(root).Length;
            Assert.Throws<OperationCanceledException>(() => AlphaTexturePreparation.PrepareCutoutPng("missing.png", 0.5f, 1f, cache, canceled.Token));
            Assert.Equal(existingFiles, Directory.GetFiles(root).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpaqueConversionRemovesTextureAlphaButPreservesRgbAndSharesCachedOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unity-material-opaque-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "alpha.png");
        try
        {
            var pixels = new byte[] { 3, 17, 29, 0, 41, 53, 67, 128 };
            var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(source)) encoder.Save(stream);

            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var output = AlphaTexturePreparation.PrepareOpaquePng(source, cache, CancellationToken.None);
            Assert.NotEqual(source, output);
            Assert.Equal(output, AlphaTexturePreparation.PrepareOpaquePng(source, cache, CancellationToken.None));
            var decoded = BitmapFrame.Create(new Uri(output), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var outputPixels = new byte[8];
            decoded.CopyPixels(outputPixels, 8, 0);
            Assert.Equal(pixels[0..3], outputPixels[0..3]);
            Assert.Equal(pixels[4..7], outputPixels[4..7]);
            Assert.True(outputPixels[3] == 255 && outputPixels[7] == 255);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
