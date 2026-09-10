using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using PoEToolbox.Core.Assets;

internal static class DdsTextReplacer
{
    private const int DdsHeaderSize = 128;
    private const int DdsRgbFlag = 0x40;
    private const int DdsAlphaPixelsFlag = 0x1;

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: dds-text <input-dir> <output-dir> [template] [font-size] [color] [replace|overlay]");
            Console.Error.WriteLine("  template: {number} (default), {name}, or literal text");
            Console.Error.WriteLine("  color: HTML color such as #FF3030 (default)");
            Console.Error.WriteLine("  mode: replace clears the original image (default); overlay keeps it");
            return 1;
        }

        var inputRoot = Path.GetFullPath(args[0]);
        var outputRoot = Path.GetFullPath(args[1]);
        var template = args.Length > 2 ? args[2] : "{number}";
        var fontSize = args.Length > 3 && float.TryParse(args[3], out var parsedSize) ? parsedSize : 28f;
        var colorText = args.Length > 4 ? args[4] : "#FF3030";
        var mode = args.Length > 5 ? args[5].ToLowerInvariant() : "replace";

        if (!Directory.Exists(inputRoot))
        {
            Console.Error.WriteLine($"Input directory not found: {inputRoot}");
            return 1;
        }
        if (fontSize <= 0)
        {
            Console.Error.WriteLine("Font size must be greater than zero.");
            return 1;
        }
        if (mode is not ("replace" or "overlay"))
        {
            Console.Error.WriteLine("Mode must be 'replace' or 'overlay'.");
            return 1;
        }

        Color color;
        try
        {
            color = ColorTranslator.FromHtml(colorText);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Invalid color '{colorText}': {ex.Message}");
            return 1;
        }

        var files = Directory.EnumerateFiles(inputRoot, "*.dds", SearchOption.AllDirectories).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"No DDS files found: {inputRoot}");
            return 1;
        }

        Directory.CreateDirectory(outputRoot);
        var succeeded = 0;
        var failed = 0;
        foreach (var inputPath in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(inputRoot, inputPath);
            var outputPath = Path.Combine(outputRoot, relativePath);
            try
            {
                Transform(inputPath, outputPath, template, fontSize, color, mode == "overlay");
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[FAIL] {relativePath}: {ex.Message}");
            }
        }

        Console.WriteLine($"DDS text replacement complete: {succeeded} succeeded, {failed} failed.");
        return failed == 0 ? 0 : 2;
    }

    public static int RunMapNumbers(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: dds-mapnumbers <input-dir> <output-dir> [font-size]");
            return 1;
        }

        var inputRoot = Path.GetFullPath(args[0]);
        var outputRoot = Path.GetFullPath(args[1]);
        var fontSize = args.Length > 2 && float.TryParse(args[2], out var parsedSize) ? parsedSize : 28f;
        if (!Directory.Exists(inputRoot))
        {
            Console.Error.WriteLine($"Input directory not found: {inputRoot}");
            return 1;
        }
        if (fontSize <= 0)
        {
            Console.Error.WriteLine("Font size must be greater than zero.");
            return 1;
        }

        Directory.CreateDirectory(outputRoot);
        var succeeded = 0;
        var failed = 0;
        for (var number = 1; number <= 16; number++)
        {
            var fileName = $"mapnumbers{number}.dds";
            var inputPath = Path.Combine(inputRoot, fileName);
            var outputPath = Path.Combine(outputRoot, fileName);
            try
            {
                if (!File.Exists(inputPath))
                    throw new FileNotFoundException("File not found", inputPath);
                var original = File.ReadAllBytes(inputPath);
                var replacement = DdsMapNumberRenderer.Render(original, number, fontSize);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, replacement);
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[FAIL] {fileName}: {ex.Message}");
            }
        }

        Console.WriteLine($"Mapnumber replacement complete: {succeeded} succeeded, {failed} failed.");
        return failed == 0 ? 0 : 2;
    }

    private static void Transform(string inputPath, string outputPath, string template, float fontSize, Color color, bool overlay)
    {
        var dds = File.ReadAllBytes(inputPath);
        var header = ParseHeader(dds);
        if (header.IsCompressed)
            throw new InvalidDataException($"Compressed DDS is not supported ({header.FourCc}). Convert it to 32-bit uncompressed first.");
        if (header.Width <= 0 || header.Height <= 0)
            throw new InvalidDataException("Invalid DDS dimensions.");
        if (header.RgbBitCount != 32 || header.PixelFormatFlags != (DdsRgbFlag | DdsAlphaPixelsFlag))
            throw new InvalidDataException("Only 32-bit ARGB DDS with an alpha channel is supported.");

        var rowBytes = checked(header.Width * 4);
        var pixelBytes = checked(rowBytes * header.Height);
        if (dds.Length < DdsHeaderSize + pixelBytes)
            throw new InvalidDataException("DDS pixel data is truncated.");

        var pixels = new byte[pixelBytes];
        if (overlay)
            Buffer.BlockCopy(dds, DdsHeaderSize, pixels, 0, pixelBytes);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(header.Width, header.Height, rowBytes, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var text = ResolveText(template, Path.GetFileNameWithoutExtension(inputPath));
            using var font = new Font("Arial", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            var measured = graphics.MeasureString(text, font);
            var x = (header.Width - measured.Width) / 2f;
            var y = (header.Height - measured.Height) / 2f;
            graphics.DrawString(text, font, brush, x, y);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var output = new byte[dds.Length];
            Buffer.BlockCopy(dds, 0, output, 0, DdsHeaderSize);
            Marshal.Copy(handle.AddrOfPinnedObject(), output, DdsHeaderSize, pixelBytes);
            File.WriteAllBytes(outputPath, output);
        }
        finally
        {
            handle.Free();
        }
    }

    private static string ResolveText(string template, string stem)
    {
        var number = ExtractTrailingNumber(stem);
        return template
            .Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
            .Replace("{number}", number ?? stem, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractTrailingNumber(string value)
    {
        var end = value.Length;
        var start = end;
        while (start > 0 && char.IsDigit(value[start - 1]))
            start--;
        return start == end ? null : value[start..end];
    }

    private static DdsHeader ParseHeader(byte[] dds)
    {
        if (dds.Length < DdsHeaderSize || Encoding.ASCII.GetString(dds, 0, 4) != "DDS ")
            throw new InvalidDataException("Not a DDS file.");

        var flags = BitConverter.ToInt32(dds, 80);
        var fourCc = Encoding.ASCII.GetString(dds, 84, 4).Trim('\0', ' ');
        return new(
            Width: BitConverter.ToInt32(dds, 16),
            Height: BitConverter.ToInt32(dds, 12),
            PixelFormatFlags: flags,
            RgbBitCount: BitConverter.ToInt32(dds, 88),
            FourCc: fourCc,
            IsCompressed: fourCc.Length != 0);
    }

    private readonly record struct DdsHeader(
        int Width,
        int Height,
        int PixelFormatFlags,
        int RgbBitCount,
        string FourCc,
        bool IsCompressed);
}
