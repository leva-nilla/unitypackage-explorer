using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class TgaDecoderTests
{
    [Fact]
    public void DecodesBottomOriginTrueColorIntoTopLeftBgraPixels()
    {
        var data = Header(1, 2, imageType: 2, bitsPerPixel: 24, descriptor: 0);
        data.AddRange([1, 2, 3, 4, 5, 6]); // bottom pixel, then top pixel; each is BGR.

        var image = TgaDecoder.Decode(data.ToArray());

        Assert.Equal((1, 2), (image.Width, image.Height));
        Assert.Equal(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255 }, image.Bgra32Pixels);
    }

    [Fact]
    public void DecodesRunLengthGrayscalePackets()
    {
        var data = Header(2, 1, imageType: 11, bitsPerPixel: 8, descriptor: 0x20);
        data.AddRange([0x81, 42]); // two identical pixels.

        var image = TgaDecoder.Decode(data.ToArray());

        Assert.Equal(new byte[] { 42, 42, 42, 255, 42, 42, 42, 255 }, image.Bgra32Pixels);
    }

    [Fact]
    public void RejectsTruncatedPixelData()
    {
        var data = Header(1, 1, imageType: 2, bitsPerPixel: 24, descriptor: 0);
        Assert.Throws<InvalidDataException>(() => TgaDecoder.Decode(data.ToArray()));
    }

    private static List<byte> Header(int width, int height, byte imageType, byte bitsPerPixel, byte descriptor)
    {
        var header = new byte[18];
        header[2] = imageType;
        header[12] = (byte)width;
        header[13] = (byte)(width >> 8);
        header[14] = (byte)height;
        header[15] = (byte)(height >> 8);
        header[16] = bitsPerPixel;
        header[17] = descriptor;
        return [.. header];
    }
}
