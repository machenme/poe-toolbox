using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;

namespace PoEToolbox.Core.Assets;

public readonly record struct DdsRgbColor(byte R, byte G, byte B)
{
    public Color ToDrawingColor() => Color.FromArgb(R, G, B);
}

/// <summary>
/// Recreates the map-number DDS texture with a centered Arabic numeral.
/// The DDS header is preserved and the pixel payload is replaced in its original format.
/// </summary>
public static class DdsMapNumberRenderer
{
    public const int DdsHeaderSize = 128;
    private const int SidecarDdsOffset = 28;
    private const int SidecarDdsPayloadOffset = SidecarDdsOffset + DdsHeaderSize;

    public static (int Width, int Height) GetDimensions(byte[] dds)
    {
        var header = ParseHeader(dds);
        return (header.Width, header.Height);
    }

    /// <summary>Returns the first mip in WPF's BGRA32 byte order.</summary>
    public static byte[] GetPreviewPixels(byte[] dds)
    {
        var header = ParseHeader(dds);
        var pixelBytes = GetMipByteCount(header.Width, header.Height, header.Layout);
        EnsurePixelData(dds, header, pixelBytes);
        return header.Layout == PixelLayout.Dxt5
            ? DecodeDxt5(dds.AsSpan(header.DataOffset, pixelBytes), header.Width, header.Height)
            : ToBgra(dds.AsSpan(header.DataOffset, pixelBytes), header.Layout);
    }

    public static byte[] Render(
        byte[] dds,
        int number,
        float fontSize = 28f,
        float offsetX = 0f,
        float offsetY = 0f,
        string fontFamily = "Arial",
        DdsRgbColor? textColor = null,
        byte[]? backgroundImage = null)
    {
        if (number is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(number), "Map number must be between 1 and 16.");
        if (fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));

        var header = ParseHeader(dds);
        var rowBytes = checked(header.Width * 4);
        var pixelBytes = GetMipByteCount(header.Width, header.Height, header.Layout);
        EnsurePixelData(dds, header, pixelBytes);

        var pixels = new byte[checked(rowBytes * header.Height)];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                header.Width,
                header.Height,
                rowBytes,
                PixelFormat.Format32bppArgb,
                handle.AddrOfPinnedObject());
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(0, 0, 0, 0));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (backgroundImage is not null)
            {
                using var backgroundStream = new MemoryStream(backgroundImage, writable: false);
                using var background = Image.FromStream(backgroundStream);
                graphics.DrawImage(background, new Rectangle(0, 0, header.Width, header.Height));
            }

            var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var font = new Font(fontFamily, fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            var renderColor = textColor?.ToDrawingColor() ?? GetColor(number);
            using var brush = new SolidBrush(renderColor);
            var measured = graphics.MeasureString(text, font);
            var x = (header.Width - measured.Width) / 2f;
            var y = (header.Height - measured.Height) / 2f;
            graphics.DrawString(text, font, brush, x + offsetX, y + offsetY);

            var renderedPixels = new byte[pixels.Length];
            Marshal.Copy(handle.AddrOfPinnedObject(), renderedPixels, 0, renderedPixels.Length);
            var result = RebuildMips(
                dds,
                header,
                bitmap,
                renderedPixels,
                renderColor,
                normalizeVisiblePixels: backgroundImage is null);
            return result;
        }
        finally
        {
            handle.Free();
        }
    }

    public static byte[] RenderSidecar(byte[] sidecar, byte[] renderedDds)
    {
        var sidecarHeader = ParseSidecarHeader(sidecar);
        var mainHeader = ParseHeader(renderedDds);
        var mainRowBytes = checked(mainHeader.Width * 4);
        var mainPixelBytes = GetMipByteCount(mainHeader.Width, mainHeader.Height, mainHeader.Layout);
        EnsurePixelData(renderedDds, mainHeader, mainPixelBytes);

        var mainPixels = GetPreviewPixels(renderedDds);
        var mainHandle = GCHandle.Alloc(mainPixels, GCHandleType.Pinned);
        try
        {
            using var sourceBitmap = new Bitmap(
                mainHeader.Width,
                mainHeader.Height,
                mainRowBytes,
                PixelFormat.Format32bppArgb,
                mainHandle.AddrOfPinnedObject());
            var result = sidecar.ToArray();
            var offset = sidecarHeader.DataOffset;
            var width = sidecarHeader.Width;
            var height = sidecarHeader.Height;

            // Rebuild every low mip from the final main texture, then convert
            // BGRA bitmap memory to the RGBA layout used by the sidecar DDS.
            for (var mip = 0; mip < sidecarHeader.MipCount; mip++)
            {
                var rowBytes = checked(width * 4);
                var pixelBytes = checked(rowBytes * height);
                if (offset + pixelBytes > result.Length)
                    throw new InvalidDataException("DDS sidecar mip data is truncated.");

                var mipPixels = new byte[pixelBytes];
                var mipHandle = GCHandle.Alloc(mipPixels, GCHandleType.Pinned);
                try
                {
                    using var mipBitmap = new Bitmap(
                        width,
                        height,
                        rowBytes,
                        PixelFormat.Format32bppArgb,
                        mipHandle.AddrOfPinnedObject());
                    using var graphics = Graphics.FromImage(mipBitmap);
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.Clear(Color.FromArgb(0, 0, 0, 0));
                    graphics.DrawImage(
                        sourceBitmap,
                        new Rectangle(0, 0, width, height),
                        0,
                        0,
                        mainHeader.Width,
                        mainHeader.Height,
                        GraphicsUnit.Pixel);

                    for (var index = 0; index < pixelBytes; index += 4)
                    {
                        result[offset + index] = mipPixels[index + 2];
                        result[offset + index + 1] = mipPixels[index + 1];
                        result[offset + index + 2] = mipPixels[index];
                        result[offset + index + 3] = mipPixels[index + 3];
                    }
                }
                finally
                {
                    mipHandle.Free();
                }

                offset += pixelBytes;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }

            if (offset != result.Length)
                throw new InvalidDataException("Unexpected DDS sidecar mip data length.");
            return result;
        }
        finally
        {
            mainHandle.Free();
        }
    }

    private static Color GetColor(int number)
        => number <= 5 ? Color.White : number <= 10 ? Color.Yellow : Color.Red;

    private static DdsHeader ParseHeader(byte[] dds)
    {
        if (dds.Length < DdsHeaderSize || Encoding.ASCII.GetString(dds, 0, 4) != "DDS ")
            throw new InvalidDataException("Not a DDS file.");

        var width = BitConverter.ToInt32(dds, 16);
        var height = BitConverter.ToInt32(dds, 12);
        var mipCount = Math.Max(1, BitConverter.ToInt32(dds, 28));
        var pixelFlags = BitConverter.ToInt32(dds, 80);
        var bitsPerPixel = BitConverter.ToInt32(dds, 88);
        var fourCc = Encoding.ASCII.GetString(dds, 84, 4).Trim('\0', ' ');
        var redMask = BitConverter.ToUInt32(dds, 92);
        var greenMask = BitConverter.ToUInt32(dds, 96);
        var blueMask = BitConverter.ToUInt32(dds, 100);
        var alphaMask = BitConverter.ToUInt32(dds, 104);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("DDS dimensions are invalid.");

        if (fourCc.Length == 0 && bitsPerPixel == 32 && pixelFlags == 0x41
            && redMask == 0x00FF0000 && greenMask == 0x0000FF00
            && blueMask == 0x000000FF && alphaMask == 0xFF000000)
            return new DdsHeader(width, height, DdsHeaderSize, mipCount, PixelLayout.Bgra);

        if (fourCc == "DX10" && dds.Length >= DdsHeaderSize + 20
            && BitConverter.ToInt32(dds, DdsHeaderSize) == 28)
            return new DdsHeader(width, height, DdsHeaderSize + 20, mipCount, PixelLayout.Rgba);

        if (fourCc == "DXT5")
            return new DdsHeader(width, height, DdsHeaderSize, mipCount, PixelLayout.Dxt5);

        if (fourCc.Length != 0 || bitsPerPixel != 32 || pixelFlags != 0x41
            || redMask != 0x00FF0000 || greenMask != 0x0000FF00
            || blueMask != 0x000000FF || alphaMask != 0xFF000000)
        {
            throw new InvalidDataException("Expected legacy ARGB DDS or DX10 RGBA8 DDS.");
        }

        throw new InvalidDataException("Unsupported DDS pixel format.");
    }

    private static SidecarHeader ParseSidecarHeader(byte[] sidecar)
    {
        if (sidecar.Length < SidecarDdsPayloadOffset
            || Encoding.ASCII.GetString(sidecar, SidecarDdsOffset, 4) != "DDS ")
            throw new InvalidDataException("Not a supported DDS sidecar.");

        var width = BitConverter.ToInt32(sidecar, SidecarDdsOffset + 16);
        var height = BitConverter.ToInt32(sidecar, SidecarDdsOffset + 12);
        var mipCount = BitConverter.ToInt32(sidecar, SidecarDdsOffset + 28);
        var pixelFlags = BitConverter.ToInt32(sidecar, SidecarDdsOffset + 80);
        var bitsPerPixel = BitConverter.ToInt32(sidecar, SidecarDdsOffset + 88);
        var fourCc = Encoding.ASCII.GetString(sidecar, SidecarDdsOffset + 84, 4).Trim('\0', ' ');
        var redMask = BitConverter.ToUInt32(sidecar, SidecarDdsOffset + 92);
        var greenMask = BitConverter.ToUInt32(sidecar, SidecarDdsOffset + 96);
        var blueMask = BitConverter.ToUInt32(sidecar, SidecarDdsOffset + 100);
        var alphaMask = BitConverter.ToUInt32(sidecar, SidecarDdsOffset + 104);
        if (width <= 0 || height <= 0 || mipCount <= 0
            || fourCc.Length != 0 || bitsPerPixel != 32 || pixelFlags != 0x41
            || redMask != 0x000000FF || greenMask != 0x0000FF00
            || blueMask != 0x00FF0000 || alphaMask != 0xFF000000)
        {
            throw new InvalidDataException("Expected an uncompressed RGBA DDS sidecar.");
        }

        var payloadLength = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var mip = 0; mip < mipCount; mip++)
        {
            payloadLength = checked(payloadLength + mipWidth * mipHeight * 4);
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        if (SidecarDdsPayloadOffset + payloadLength != sidecar.Length)
            throw new InvalidDataException("Unexpected DDS sidecar length.");
        return new SidecarHeader(width, height, mipCount, SidecarDdsPayloadOffset);
    }

    private static void EnsurePixelData(byte[] dds, DdsHeader header, int pixelBytes)
    {
        if (dds.Length < header.DataOffset + pixelBytes)
            throw new InvalidDataException("DDS pixel data is truncated.");
    }

    private static byte[] RebuildMips(
        byte[] dds,
        DdsHeader header,
        Bitmap sourceBitmap,
        byte[] firstMipPixels,
        Color color,
        bool normalizeVisiblePixels)
    {
        var result = dds.ToArray();
        var offset = header.DataOffset;
        var width = header.Width;
        var height = header.Height;
        for (var mip = 0; mip < header.MipCount; mip++)
        {
            var pixelBytes = GetMipByteCount(width, height, header.Layout);
            if (offset + pixelBytes > result.Length)
                throw new InvalidDataException("DDS mip data is truncated.");

            var pixels = mip == 0
                ? firstMipPixels
                : ResizePixels(sourceBitmap, width, height, color, normalizeVisiblePixels);
            WritePixels(result, offset, pixels, width, height, header.Layout);

            offset += pixelBytes;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return result;
    }

    private static byte[] ResizePixels(
        Bitmap sourceBitmap,
        int width,
        int height,
        Color color,
        bool normalizeVisiblePixels)
    {
        var rowBytes = checked(width * 4);
        var pixels = new byte[checked(rowBytes * height)];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                width,
                height,
                rowBytes,
                PixelFormat.Format32bppArgb,
                handle.AddrOfPinnedObject());
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.FromArgb(0, 0, 0, 0));
            graphics.DrawImage(
                sourceBitmap,
                new Rectangle(0, 0, width, height),
                0,
                0,
                sourceBitmap.Width,
                sourceBitmap.Height,
                GraphicsUnit.Pixel);

            // GDI+ may retain RGB values from transparent source pixels while
            // downscaling. Normalize visible pixels only for a number-only texture.
            if (normalizeVisiblePixels)
            {
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    if (pixels[index + 3] == 0) continue;

                    pixels[index] = color.B;
                    pixels[index + 1] = color.G;
                    pixels[index + 2] = color.R;
                }
            }

            return pixels;
        }
        finally
        {
            handle.Free();
        }
    }

    private static int GetMipByteCount(int width, int height, PixelLayout layout)
        => layout == PixelLayout.Dxt5
            ? checked(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16)
            : checked(width * height * 4);

    private static void WritePixels(byte[] output, int offset, ReadOnlySpan<byte> bgra, int width, int height, PixelLayout layout)
    {
        var destination = output.AsSpan(offset, GetMipByteCount(width, height, layout));
        switch (layout)
        {
            case PixelLayout.Bgra:
                bgra.CopyTo(destination);
                break;
            case PixelLayout.Rgba:
                ToRgba(bgra, destination);
                break;
            case PixelLayout.Dxt5:
                EncodeDxt5(bgra, width, height, destination);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }

    private static byte[] DecodeDxt5(ReadOnlySpan<byte> source, int width, int height)
    {
        var result = new byte[checked(width * height * 4)];
        var blockOffset = 0;
        for (var blockY = 0; blockY < height; blockY += 4)
        for (var blockX = 0; blockX < width; blockX += 4)
        {
            var alphaPalette = GetAlphaPalette(source[blockOffset], source[blockOffset + 1]);
            var alphaIndices = Read48Bit(source.Slice(blockOffset + 2, 6));
            var color0 = DecodeRgb565(BitConverter.ToUInt16(source.Slice(blockOffset + 8, 2)));
            var color1 = DecodeRgb565(BitConverter.ToUInt16(source.Slice(blockOffset + 10, 2)));
            var colorPalette = GetColorPalette(color0, color1);
            var colorIndices = BitConverter.ToUInt32(source.Slice(blockOffset + 12, 4));

            for (var pixel = 0; pixel < 16; pixel++)
            {
                var x = blockX + pixel % 4;
                var y = blockY + pixel / 4;
                if (x >= width || y >= height)
                    continue;

                var destination = (y * width + x) * 4;
                var (r, g, b) = colorPalette[(int)((colorIndices >> (pixel * 2)) & 0x3)];
                result[destination] = b;
                result[destination + 1] = g;
                result[destination + 2] = r;
                result[destination + 3] = alphaPalette[(int)((alphaIndices >> (pixel * 3)) & 0x7)];
            }

            blockOffset += 16;
        }

        return result;
    }

    private static void EncodeDxt5(ReadOnlySpan<byte> bgra, int width, int height, Span<byte> destination)
    {
        var blockOffset = 0;
        for (var blockY = 0; blockY < height; blockY += 4)
        for (var blockX = 0; blockX < width; blockX += 4)
        {
            var alpha = new byte[16];
            var colors = new (byte R, byte G, byte B)[16];
            for (var pixel = 0; pixel < 16; pixel++)
            {
                var x = Math.Min(width - 1, blockX + pixel % 4);
                var y = Math.Min(height - 1, blockY + pixel / 4);
                var source = (y * width + x) * 4;
                colors[pixel] = (bgra[source + 2], bgra[source + 1], bgra[source]);
                alpha[pixel] = bgra[source + 3];
            }

            EncodeAlphaBlock(alpha, destination.Slice(blockOffset, 8));
            EncodeColorBlock(colors, destination.Slice(blockOffset + 8, 8));
            blockOffset += 16;
        }
    }

    private static void EncodeAlphaBlock(ReadOnlySpan<byte> alpha, Span<byte> destination)
    {
        var maximum = alpha.ToArray().Max();
        var minimum = alpha.ToArray().Min();
        destination[0] = maximum;
        destination[1] = minimum;
        var palette = GetAlphaPalette(maximum, minimum);
        ulong indices = 0;
        for (var pixel = 0; pixel < alpha.Length; pixel++)
        {
            var index = FindClosestAlpha(alpha[pixel], palette);
            indices |= (ulong)index << (pixel * 3);
        }
        Write48Bit(indices, destination.Slice(2, 6));
    }

    private static void EncodeColorBlock(ReadOnlySpan<(byte R, byte G, byte B)> colors, Span<byte> destination)
    {
        var brightest = colors[0];
        var darkest = colors[0];
        var brightestLuminance = GetLuminance(brightest);
        var darkestLuminance = brightestLuminance;
        for (var index = 1; index < colors.Length; index++)
        {
            var luminance = GetLuminance(colors[index]);
            if (luminance > brightestLuminance)
            {
                brightest = colors[index];
                brightestLuminance = luminance;
            }
            if (luminance < darkestLuminance)
            {
                darkest = colors[index];
                darkestLuminance = luminance;
            }
        }

        var color0 = EncodeRgb565(brightest);
        var color1 = EncodeRgb565(darkest);
        if (color0 == color1 && color0 > 0)
            color1--;
        if (color0 < color1)
            (color0, color1) = (color1, color0);

        BitConverter.TryWriteBytes(destination, color0);
        BitConverter.TryWriteBytes(destination.Slice(2), color1);
        var palette = GetColorPalette(DecodeRgb565(color0), DecodeRgb565(color1));
        uint indices = 0;
        for (var pixel = 0; pixel < colors.Length; pixel++)
            indices |= (uint)FindClosestColor(colors[pixel], palette) << (pixel * 2);
        BitConverter.TryWriteBytes(destination.Slice(4), indices);
    }

    private static int GetLuminance((byte R, byte G, byte B) color)
        => color.R * 299 + color.G * 587 + color.B * 114;

    private static byte[] GetAlphaPalette(byte alpha0, byte alpha1)
    {
        var palette = new byte[8];
        palette[0] = alpha0;
        palette[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var index = 1; index <= 6; index++)
                palette[index + 1] = (byte)(((7 - index) * alpha0 + index * alpha1) / 7);
        }
        else
        {
            for (var index = 1; index <= 4; index++)
                palette[index + 1] = (byte)(((5 - index) * alpha0 + index * alpha1) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }
        return palette;
    }

    private static (byte R, byte G, byte B)[] GetColorPalette((byte R, byte G, byte B) color0, (byte R, byte G, byte B) color1)
        => [
            color0,
            color1,
            ((byte)((2 * color0.R + color1.R) / 3), (byte)((2 * color0.G + color1.G) / 3), (byte)((2 * color0.B + color1.B) / 3)),
            ((byte)((color0.R + 2 * color1.R) / 3), (byte)((color0.G + 2 * color1.G) / 3), (byte)((color0.B + 2 * color1.B) / 3)),
        ];

    private static (byte R, byte G, byte B) DecodeRgb565(ushort color)
        => ((byte)(((color >> 11) & 0x1F) * 255 / 31), (byte)(((color >> 5) & 0x3F) * 255 / 63), (byte)((color & 0x1F) * 255 / 31));

    private static ushort EncodeRgb565((byte R, byte G, byte B) color)
        => (ushort)(((color.R * 31 / 255) << 11) | ((color.G * 63 / 255) << 5) | (color.B * 31 / 255));

    private static int FindClosestAlpha(byte value, ReadOnlySpan<byte> palette)
    {
        var bestIndex = 0;
        var bestDistance = int.MaxValue;
        for (var index = 0; index < palette.Length; index++)
        {
            var distance = Math.Abs(value - palette[index]);
            if (distance < bestDistance)
            {
                bestIndex = index;
                bestDistance = distance;
            }
        }
        return bestIndex;
    }

    private static int FindClosestColor((byte R, byte G, byte B) value, ReadOnlySpan<(byte R, byte G, byte B)> palette)
    {
        var bestIndex = 0;
        var bestDistance = int.MaxValue;
        for (var index = 0; index < palette.Length; index++)
        {
            var red = value.R - palette[index].R;
            var green = value.G - palette[index].G;
            var blue = value.B - palette[index].B;
            var distance = red * red + green * green + blue * blue;
            if (distance < bestDistance)
            {
                bestIndex = index;
                bestDistance = distance;
            }
        }
        return bestIndex;
    }

    private static ulong Read48Bit(ReadOnlySpan<byte> source)
    {
        ulong value = 0;
        for (var index = 0; index < 6; index++)
            value |= (ulong)source[index] << (index * 8);
        return value;
    }

    private static void Write48Bit(ulong value, Span<byte> destination)
    {
        for (var index = 0; index < 6; index++)
            destination[index] = (byte)(value >> (index * 8));
    }

    private static byte[] ToBgra(ReadOnlySpan<byte> pixels, PixelLayout layout)
    {
        var result = pixels.ToArray();
        if (layout == PixelLayout.Rgba)
        {
            for (var index = 0; index < result.Length; index += 4)
                (result[index], result[index + 2]) = (result[index + 2], result[index]);
        }
        return result;
    }

    private static void ToRgba(ReadOnlySpan<byte> bgra, Span<byte> rgba)
    {
        bgra.CopyTo(rgba);
        for (var index = 0; index < rgba.Length; index += 4)
            (rgba[index], rgba[index + 2]) = (rgba[index + 2], rgba[index]);
    }

    private enum PixelLayout
    {
        Bgra,
        Rgba,
        Dxt5,
    }

    private readonly record struct DdsHeader(int Width, int Height, int DataOffset, int MipCount, PixelLayout Layout);
    private readonly record struct SidecarHeader(int Width, int Height, int MipCount, int DataOffset);
}
