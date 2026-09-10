using System.IO;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using LibBundle3.Records;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.Poe2Font;

public sealed record Poe2FontOptions(string Typeface, double SizeScalePercent);

public sealed record Poe2FontResult(
    string GameDataPath,
    string Typeface,
    double SizeScalePercent,
    int BaseFileSize,
    int TraditionalFileSize,
    string BaselinePath);

public sealed record Poe2FontRestoreResult(
    string GameDataPath,
    int BaseFileSize,
    int TraditionalFileSize,
    string BaselinePath);

/// <summary>
/// Generates the two POE2 UI settings files from the original files and user options.
/// </summary>
public static class Poe2FontService
{
    public const string BaseVirtualPath = "metadata/ui/uisettings.xml";
    public const string TraditionalVirtualPath = "metadata/ui/uisettings.traditional chinese.xml";

    private const string BackupDirectoryName = "poe2-fonts";
    private const string BaseBackupName = "uisettings.xml";
    private const string TraditionalBackupName = "uisettings.traditional chinese.xml";
    private const string GeneratedCommentPrefix = "POE Toolbox: 字体配置器";

    public static readonly string[] TypefacePresets =
    [
        "方正准圆",
        "Noto Sans CJK TC",
        "Microsoft JhengHei",
        "Microsoft YaHei",
        "Spoqa Han Sans Neo",
    ];

    public static Poe2FontResult Apply(
        string gameDataPath,
        Poe2FontOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        using var gameData = GameDataAccess.Open(gameDataPath);
        if (!gameData.IsPoe2Client)
            throw new InvalidOperationException("当前游戏数据不是 POE2 客户端，已停止字体配置。 ");
        var targets = GetTargets(gameData);
        var backup = EnsureBaseline(gameData.GameDataPath, targets.Base.Read().ToArray(), targets.Traditional.Read().ToArray());
        var baseBytes = File.ReadAllBytes(backup.BasePath);
        var generatedBase = GenerateBaseXml(baseBytes, options);
        var generatedTraditional = GenerateTraditionalXml();

        cancellationToken.ThrowIfCancellationRequested();
        var indexBackup = IndexBackupService.Begin(gameData);
        targets.Base.Write(generatedBase);
        targets.Traditional.Write(generatedTraditional);
        gameData.Save();
        IndexBackupService.Complete(gameData, indexBackup, "poe2-font", new Dictionary<string, string>
        {
            ["typeface"] = options.Typeface,
            ["sizeScalePercent"] = options.SizeScalePercent.ToString("0.##", CultureInfo.InvariantCulture),
            ["baseVirtualPath"] = BaseVirtualPath,
            ["traditionalVirtualPath"] = TraditionalVirtualPath,
        });

        return new Poe2FontResult(
            gameData.GameDataPath,
            options.Typeface,
            options.SizeScalePercent,
            generatedBase.Length,
            generatedTraditional.Length,
            backup.DirectoryPath);
    }

    public static Poe2FontRestoreResult Restore(
        string gameDataPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backup = GetBaselinePaths(gameDataPath);
        if (!File.Exists(backup.BasePath) || !File.Exists(backup.TraditionalPath))
            throw new FileNotFoundException("尚未找到字体功能创建的原始文件备份。", backup.DirectoryPath);

        using var gameData = GameDataAccess.Open(gameDataPath);
        if (!gameData.IsPoe2Client)
            throw new InvalidOperationException("当前游戏数据不是 POE2 客户端，已停止恢复字体。 ");
        var targets = GetTargets(gameData);
        var baseBytes = File.ReadAllBytes(backup.BasePath);
        var traditionalBytes = File.ReadAllBytes(backup.TraditionalPath);
        var indexBackup = IndexBackupService.Begin(gameData);
        targets.Base.Write(baseBytes);
        targets.Traditional.Write(traditionalBytes);
        gameData.Save();
        IndexBackupService.Complete(gameData, indexBackup, "poe2-font-restore", new Dictionary<string, string>
        {
            ["baseVirtualPath"] = BaseVirtualPath,
            ["traditionalVirtualPath"] = TraditionalVirtualPath,
        });

        return new Poe2FontRestoreResult(
            gameData.GameDataPath,
            baseBytes.Length,
            traditionalBytes.Length,
            backup.DirectoryPath);
    }

    public static bool HasBaseline(string gameDataPath)
    {
        var paths = GetBaselinePaths(gameDataPath);
        return File.Exists(paths.BasePath) && File.Exists(paths.TraditionalPath);
    }

    internal static byte[] GenerateBaseXml(byte[] sourceBytes, Poe2FontOptions options)
    {
        ValidateOptions(options);
        var (source, encoding) = DecodeXml(sourceBytes);
        var document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("uisettings.xml 缺少根节点。 ");

        foreach (var comment in root.Nodes().OfType<XComment>().Where(comment =>
                     comment.Value.StartsWith(GeneratedCommentPrefix, StringComparison.Ordinal)))
        {
            comment.Remove();
        }

        var scale = options.SizeScalePercent / 100.0;
        foreach (var font in root.Descendants("Font"))
        {
            var typeface = font.Attribute("typeface");
            if (typeface is not null)
                typeface.Value = options.Typeface;

            var size = font.Attribute("size");
            if (size is not null && double.TryParse(size.Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsedSize))
            {
                size.Value = Math.Max(1, (int)Math.Round(parsedSize * scale, MidpointRounding.AwayFromZero))
                    .ToString(CultureInfo.InvariantCulture);
            }
        }

        foreach (var fallback in root.Descendants("FallbackFont"))
        {
            var id = fallback.Attribute("id")?.Value;
            if (id is not ("CJK" or "Any"))
                continue;

            var fonts = (fallback.Attribute("fonts")?.Value ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(font => !string.Equals(font, options.Typeface, StringComparison.OrdinalIgnoreCase))
                .Prepend(options.Typeface);
            fallback.SetAttributeValue("fonts", string.Join(',', fonts));
        }

        root.AddFirst(new XComment(
            $"{GeneratedCommentPrefix}：字体类型 = {SafeCommentValue(options.Typeface)}，字号倍率 = {options.SizeScalePercent.ToString("0.##", CultureInfo.InvariantCulture)}%。"));
        return EncodeXml(document, encoding);
    }

    internal static byte[] GenerateTraditionalXml()
    {
        const string xml = "<?xml version=\"1.0\"?>\n"
            + "<Props id=\"PathOfExile\" inherits=\"Metadata/UI/UISettings.xml\">\n"
            + "  <!-- POE Toolbox: 字体配置统一由 metadata/ui/uisettings.xml 控制。 -->\n"
            + "</Props>\n";
        return EncodeText(xml, new UnicodeEncoding(false, true));
    }

    private static (string Text, Encoding Encoding) DecodeXml(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true));
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        return (Encoding.UTF8.GetString(bytes), new UTF8Encoding(false));
    }

    private static byte[] EncodeXml(XDocument document, Encoding encoding)
        => EncodeText(document.ToString(SaveOptions.DisableFormatting), encoding);

    private static byte[] EncodeText(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var content = encoding.GetBytes(text);
        return preamble.Length == 0
            ? content
            : preamble.Concat(content).ToArray();
    }

    private static void ValidateOptions(Poe2FontOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Typeface))
            throw new ArgumentException("字体类型不能为空。", nameof(options));
        if (options.Typeface.Contains("--", StringComparison.Ordinal)
            || options.Typeface.Contains('<')
            || options.Typeface.Contains('>'))
            throw new ArgumentException("字体类型包含 XML 不支持的字符。", nameof(options));
        if (double.IsNaN(options.SizeScalePercent)
            || double.IsInfinity(options.SizeScalePercent)
            || options.SizeScalePercent < 25
            || options.SizeScalePercent > 300)
            throw new ArgumentOutOfRangeException(nameof(options), "字号倍率必须在 25% 到 300% 之间。 ");
    }

    private static (FileRecord Base, FileRecord Traditional) GetTargets(GameDataAccess gameData)
    {
        if (!gameData.TryGetFile(BaseVirtualPath, out var baseFile) || baseFile is null)
            throw new FileNotFoundException("索引中未找到 POE2 基础字体文件。", BaseVirtualPath);
        if (!gameData.TryGetFile(TraditionalVirtualPath, out var traditionalFile) || traditionalFile is null)
            throw new FileNotFoundException("索引中未找到 POE2 繁体中文字体文件。", TraditionalVirtualPath);
        return (baseFile, traditionalFile);
    }

    private static BaselinePaths EnsureBaseline(string gameDataPath, byte[] baseBytes, byte[] traditionalBytes)
    {
        var paths = GetBaselinePaths(gameDataPath);
        Directory.CreateDirectory(paths.DirectoryPath);
        WriteOnce(paths.BasePath, baseBytes);
        WriteOnce(paths.TraditionalPath, traditionalBytes);
        return paths;
    }

    private static void WriteOnce(string path, byte[] bytes)
    {
        if (File.Exists(path))
            return;

        var temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static BaselinePaths GetBaselinePaths(string gameDataPath)
    {
        var directory = Path.Combine(IndexBackupService.GetBackupDirectory(gameDataPath), BackupDirectoryName);
        return new BaselinePaths(
            directory,
            Path.Combine(directory, BaseBackupName),
            Path.Combine(directory, TraditionalBackupName));
    }

    private static string SafeCommentValue(string value) => value.Replace("--", "- -");

    private sealed record BaselinePaths(string DirectoryPath, string BasePath, string TraditionalPath);
}
