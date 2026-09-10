using System.Text;
using PoEToolbox.Core.Assets;
using Xunit;

namespace PoEToolbox.Tests;

public sealed class DdsMapNumberRendererTests
{
    [Fact]
    public void GetsPreviewPixelsFromDxt5Dds()
    {
        var pixels = DdsMapNumberRenderer.GetPreviewPixels(CreateSolidRedDxt5Dds());

        Assert.Equal(4 * 4 * 4, pixels.Length);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            Assert.Equal(0, pixels[index]);
            Assert.Equal(0, pixels[index + 1]);
            Assert.Equal(255, pixels[index + 2]);
            Assert.Equal(255, pixels[index + 3]);
        }
    }

    [Fact]
    public void RendersDxt5DdsWithoutChangingItsFormat()
    {
        var rendered = DdsMapNumberRenderer.Render(CreateSolidRedDxt5Dds(), 1, fontSize: 3);

        Assert.Equal("DDS ", Encoding.ASCII.GetString(rendered, 0, 4));
        Assert.Equal("DXT5", Encoding.ASCII.GetString(rendered, 84, 4));
        Assert.Equal(144, rendered.Length);
        Assert.Equal(4 * 4 * 4, DdsMapNumberRenderer.GetPreviewPixels(rendered).Length);
    }

    private static byte[] CreateSolidRedDxt5Dds()
    {
        var dds = new byte[144];
        Encoding.ASCII.GetBytes("DDS ".AsSpan(), dds);
        BitConverter.TryWriteBytes(dds.AsSpan(4), 124);
        BitConverter.TryWriteBytes(dds.AsSpan(8), 0x00081007);
        BitConverter.TryWriteBytes(dds.AsSpan(12), 4);
        BitConverter.TryWriteBytes(dds.AsSpan(16), 4);
        BitConverter.TryWriteBytes(dds.AsSpan(20), 16);
        BitConverter.TryWriteBytes(dds.AsSpan(28), 1);
        BitConverter.TryWriteBytes(dds.AsSpan(76), 32);
        BitConverter.TryWriteBytes(dds.AsSpan(80), 4);
        Encoding.ASCII.GetBytes("DXT5".AsSpan(), dds.AsSpan(84));
        BitConverter.TryWriteBytes(dds.AsSpan(108), 0x1000);

        dds[128] = 255;
        dds[129] = 0;
        BitConverter.TryWriteBytes(dds.AsSpan(136), (ushort)0xF800);
        BitConverter.TryWriteBytes(dds.AsSpan(138), (ushort)0);
        return dds;
    }
}
