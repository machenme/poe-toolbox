using System.Text;

internal static class DdsRegionCleaner
{
    private const int DdsHeaderSize = 128;

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: dds-remove-map-t <input-dir> <output-dir> [first] [last]");
            Console.Error.WriteLine("  Clears the T prefix from mapnumbers1.dds through mapnumbers16.dds.");
            return 1;
        }

        var inputRoot = Path.GetFullPath(args[0]);
        var outputRoot = Path.GetFullPath(args[1]);
        var first = args.Length > 2 && int.TryParse(args[2], out var parsedFirst) ? parsedFirst : 1;
        var last = args.Length > 3 && int.TryParse(args[3], out var parsedLast) ? parsedLast : 16;
        if (!Directory.Exists(inputRoot))
        {
            Console.Error.WriteLine($"Input directory not found: {inputRoot}");
            return 1;
        }
        if (first < 1 || last < first)
        {
            Console.Error.WriteLine("The number range is invalid.");
            return 1;
        }

        Directory.CreateDirectory(outputRoot);
        var succeeded = 0;
        var failed = 0;
        for (var number = first; number <= last; number++)
        {
            var fileName = $"mapnumbers{number}.dds";
            var inputPath = Path.Combine(inputRoot, fileName);
            var outputPath = Path.Combine(outputRoot, fileName);
            try
            {
                if (!File.Exists(inputPath))
                    throw new FileNotFoundException("File not found", inputPath);
                ClearT(inputPath, outputPath);
                succeeded++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[FAIL] {fileName}: {ex.Message}");
            }
        }

        Console.WriteLine($"DDS T removal complete: {succeeded} succeeded, {failed} failed.");
        return failed == 0 ? 0 : 2;
    }

    private static void ClearT(string inputPath, string outputPath)
    {
        var dds = File.ReadAllBytes(inputPath);
        if (dds.Length < DdsHeaderSize || Encoding.ASCII.GetString(dds, 0, 4) != "DDS ")
            throw new InvalidDataException("Not a DDS file.");

        var width = BitConverter.ToInt32(dds, 16);
        var height = BitConverter.ToInt32(dds, 12);
        var pixelFlags = BitConverter.ToInt32(dds, 80);
        var bitsPerPixel = BitConverter.ToInt32(dds, 88);
        var fourCc = Encoding.ASCII.GetString(dds, 84, 4).Trim('\0', ' ');
        var redMask = BitConverter.ToUInt32(dds, 92);
        var greenMask = BitConverter.ToUInt32(dds, 96);
        var blueMask = BitConverter.ToUInt32(dds, 100);
        var alphaMask = BitConverter.ToUInt32(dds, 104);
        if (fourCc.Length != 0 || bitsPerPixel != 32 || pixelFlags != 0x41
            || redMask != 0x00FF0000 || greenMask != 0x0000FF00
            || blueMask != 0x000000FF || alphaMask != 0xFF000000)
        {
            throw new InvalidDataException("Expected uncompressed 32-bit ARGB DDS.");
        }

        var rowBytes = checked(width * 4);
        var pixelBytes = checked(rowBytes * height);
        if (dds.Length < DdsHeaderSize + pixelBytes)
            throw new InvalidDataException("DDS pixel data is truncated.");

        // Coordinates are based on the 80x80 mapnumber texture layout.
        var left = Math.Clamp(17, 0, width);
        var right = Math.Clamp(34, 0, width - 1);
        var top = Math.Clamp(20, 0, height);
        var bottom = Math.Clamp(59, 0, height - 1);
        for (var y = top; y <= bottom; y++)
        {
            var row = DdsHeaderSize + y * rowBytes;
            for (var x = left; x <= right; x++)
                dds[row + x * 4 + 3] = 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, dds);
    }
}
