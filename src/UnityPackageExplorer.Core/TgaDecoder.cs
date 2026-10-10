namespace UnityPackageExplorer.Core;

public sealed record DecodedTgaImage(int Width, int Height, byte[] Bgra32Pixels);

/// <summary>Decodes common true-color and grayscale TGA files to top-left-origin BGRA pixels.</summary>
public static class TgaDecoder
{
    private const int HeaderSize = 18;
    private const int MaximumDimension = 16384;
    private const long MaximumPixelCount = 64L * 1024 * 1024;

    public static DecodedTgaImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("TGA header is truncated.");
        var idLength = data[0];
        var colorMapType = data[1];
        var imageType = data[2];
        var width = data[12] | (data[13] << 8);
        var height = data[14] | (data[15] << 8);
        var bitsPerPixel = data[16];
        var descriptor = data[17];
        var isGray = imageType is 3 or 11;
        var isRle = imageType is 10 or 11;
        if (colorMapType != 0 || imageType is not (2 or 3 or 10 or 11))
            throw new NotSupportedException("Only non-indexed true-color or grayscale TGA images are supported.");
        if (width is <= 0 or > MaximumDimension || height is <= 0 or > MaximumDimension || (long)width * height > MaximumPixelCount)
            throw new InvalidDataException("TGA dimensions exceed the preview limit.");
        if (isGray ? bitsPerPixel != 8 : bitsPerPixel is not (24 or 32))
            throw new NotSupportedException($"TGA pixel depth {bitsPerPixel} is not supported.");

        var bytesPerPixel = bitsPerPixel / 8;
        var pixelCount = checked(width * height);
        var pixels = new byte[checked(pixelCount * 4)];
        var offset = HeaderSize + idLength;
        var sourcePixel = 0;

        while (sourcePixel < pixelCount)
        {
            var packetHeader = isRle ? ReadByte(data, ref offset) : 0;
            var runLength = isRle ? (packetHeader & 0x7f) + 1 : 1;
            if (runLength > pixelCount - sourcePixel) throw new InvalidDataException("TGA run-length packet exceeds the image dimensions.");
            var repeated = isRle && (packetHeader & 0x80) != 0;
            var repeatedPixel = repeated ? ReadPixel(data, ref offset, bytesPerPixel, isGray) : 0U;

            for (var i = 0; i < runLength; i++, sourcePixel++)
            {
                var pixel = repeated ? repeatedPixel : ReadPixel(data, ref offset, bytesPerPixel, isGray);
                var sourceX = sourcePixel % width;
                var sourceY = sourcePixel / width;
                var x = (descriptor & 0x10) != 0 ? width - 1 - sourceX : sourceX;
                var y = (descriptor & 0x20) != 0 ? sourceY : height - 1 - sourceY;
                var destination = (y * width + x) * 4;
                pixels[destination] = (byte)pixel;
                pixels[destination + 1] = (byte)(pixel >> 8);
                pixels[destination + 2] = (byte)(pixel >> 16);
                pixels[destination + 3] = (byte)(pixel >> 24);
            }
        }

        return new DecodedTgaImage(width, height, pixels);
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int offset)
    {
        if ((uint)offset >= (uint)data.Length) throw new InvalidDataException("TGA pixel data is truncated.");
        return data[offset++];
    }

    private static uint ReadPixel(ReadOnlySpan<byte> data, ref int offset, int bytesPerPixel, bool isGray)
    {
        if (offset > data.Length - bytesPerPixel) throw new InvalidDataException("TGA pixel data is truncated.");
        if (isGray)
        {
            var gray = data[offset++];
            return gray | ((uint)gray << 8) | ((uint)gray << 16) | 0xff000000;
        }

        var blue = data[offset++];
        var green = data[offset++];
        var red = data[offset++];
        var alpha = bytesPerPixel == 4 ? data[offset++] : (byte)255;
        return blue | ((uint)green << 8) | ((uint)red << 16) | ((uint)alpha << 24);
    }
}
