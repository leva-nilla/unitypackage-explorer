using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Rendering;

/// <summary>Request-scoped, cancellable texture preprocessing for the simplified native material preview.</summary>
public static class AlphaTexturePreparation
{
    private const long MaximumPixels = 16_777_216;

    /// <summary>Applies the render-mode specific alpha handling shared by the app and Explorer previews.</summary>
    public static string PrepareForMaterial(string path, RenderingMaterialBinding binding, IDictionary<string, string> requestCache, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.RenderMode == UnityMaterialRenderMode.Opaque)
        {
            try { return PrepareOpaquePng(path, requestCache, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
            {
                warnings.Add($"Opaque texture alpha for material '{binding.MaterialName}' could not be removed ({ex.Message}); transparent pixels may still appear as holes.");
                return path;
            }
        }
        if (UnityMaterialPreviewPolicy.CutoutThreshold(binding.RenderMode, binding.Cutoff) is not { } cutoff) return path;
        try { return PrepareCutoutPng(path, cutoff, binding.Color?.A ?? 1f, requestCache, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            warnings.Add($"Cutout texture for material '{binding.MaterialName}' could not be converted ({ex.Message}); the original texture is shown without cutoff masking.");
            return path;
        }
    }

    /// <summary>Converts a TGA texture to PNG so the renderer can load it; returns false when the file is unsupported.</summary>
    public static bool TryConvertTgaToPng(string sourcePath, string materialName, IDictionary<string, string> requestCache, ICollection<string> warnings, CancellationToken cancellationToken, out string outputPath)
    {
        outputPath = sourcePath;
        var key = $"{Path.GetFullPath(sourcePath)}|tga";
        if (requestCache.TryGetValue(key, out var existing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputPath = existing;
            return true;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = TgaDecoder.Decode(File.ReadAllBytes(sourcePath));
            var result = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra32Pixels, decoded.Width * 4);
            result.Freeze();
            var output = Path.Combine(Path.GetDirectoryName(sourcePath)!, $"tga-{Guid.NewGuid():N}.png");
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(result));
                using (var stream = File.Create(output)) encoder.Save(stream);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                try { if (File.Exists(output)) File.Delete(output); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
            requestCache.Add(key, output);
            outputPath = output;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
            warnings.Add($"TGA texture for material '{materialName}' could not be converted ({ex.Message}); it is shown without a texture.");
            return false;
        }
    }

    public static string PrepareCutoutPng(string sourcePath, float cutoff, float colorAlpha, IDictionary<string, string> requestCache, CancellationToken cancellationToken)
    {
        var threshold = Math.Clamp(cutoff, 0f, 1f);
        var alpha = Math.Clamp(colorAlpha, 0f, 1f);
        return Prepare(sourcePath, $"cutout|{threshold.ToString("R", CultureInfo.InvariantCulture)}|{alpha.ToString("R", CultureInfo.InvariantCulture)}", requestCache, cancellationToken,
            value => value / 255f * alpha >= threshold ? (byte)255 : (byte)0, alwaysWrite: true, "cutout");
    }

    public static string PrepareOpaquePng(string sourcePath, IDictionary<string, string> requestCache, CancellationToken cancellationToken) =>
        Prepare(sourcePath, "opaque", requestCache, cancellationToken, _ => 255, alwaysWrite: false, "opaque");

    private static string Prepare(string sourcePath, string modeKey, IDictionary<string, string> requestCache, CancellationToken cancellationToken, Func<byte, byte> alphaTransform, bool alwaysWrite, string filePrefix)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"{Path.GetFullPath(sourcePath)}|{modeKey}";
        if (requestCache.TryGetValue(key, out var existing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return existing;
        }

        int width;
        int height;
        using (var metadataStream = File.OpenRead(sourcePath))
        {
            var metadataDecoder = BitmapDecoder.Create(metadataStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            width = metadataDecoder.Frames[0].PixelWidth;
            height = metadataDecoder.Frames[0].PixelHeight;
        }
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
            throw new NotSupportedException($"{filePrefix} alpha conversion is limited to images up to 16 megapixels.");
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var needsOutput = alwaysWrite;
        using (var decodeStream = File.OpenRead(sourcePath))
        {
            var decoder = BitmapDecoder.Create(decodeStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, stride, 0);
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var offset = row + x * 4 + 3;
                    var transformed = alphaTransform(pixels[offset]);
                    needsOutput |= transformed != pixels[offset];
                    pixels[offset] = transformed;
                }
            }
        }
        if (!needsOutput)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestCache.Add(key, sourcePath);
            return sourcePath;
        }

        var output = Path.Combine(Path.GetDirectoryName(sourcePath)!, $"{filePrefix}-{Guid.NewGuid():N}.png");
        try
        {
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(output)) encoder.Save(stream);
            cancellationToken.ThrowIfCancellationRequested();
            requestCache.Add(key, output);
            return output;
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
