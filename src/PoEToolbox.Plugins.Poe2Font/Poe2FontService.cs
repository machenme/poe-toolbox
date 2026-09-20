using System.IO;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using LibBundle3.Records;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.Poe2Font;

/// <summary>
/// 字号写入计划。<see cref="Sizes"/> 只登记「相对官方模板要改」的条目，键为作用域路径 + id；
/// 未登记的条目由 <see cref="Poe2FontTemplate"/> 决定——自带 size 的写回官方值，纯继承的不碰。
/// </summary>
public sealed record Poe2FontOptions(string Typeface, IReadOnlyDictionary<string, int> Sizes);

public sealed record Poe2FontChange(string Key, int OfficialSize, int TargetSize);

public sealed record Poe2FontResult(
    string GameDataPath,
    string Typeface,
    int BaseFileSize,
    int TraditionalFileSize,
    string BaselinePath,
    IReadOnlyList<Poe2FontChange> Changes,
    int UnknownEntryCount);

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

    /// <summary>可写入的绝对字号区间（基准分辨率 2560 下的像素）。官方最大值是 79。</summary>
    public const int MinFontSize = 1;
    public const int MaxFontSize = 256;

    public static Poe2FontResult Apply(
        string gameDataPath,
        Poe2FontOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        return GameDataLoader.Use(gameDataPath, GameDataMode.ReadWrite, gameData =>
        {
            if (!gameData.IsPoe2Client)
                throw new InvalidOperationException("当前游戏数据不是 POE2 客户端，已停止字体配置。 ");
            var targets = GetTargets(gameData);
            var backup = EnsureBaseline(gameData.GameDataPath, targets.Base.Read().ToArray(), targets.Traditional.Read().ToArray());
            var baseBytes = File.ReadAllBytes(backup.BasePath);
            var generated = GenerateBaseXml(baseBytes, options);
            var generatedBase = generated.Bytes;
            var generatedTraditional = GenerateTraditionalXml();

            cancellationToken.ThrowIfCancellationRequested();
            var indexBackup = IndexBackupService.Begin(gameData);
            targets.Base.Write(generatedBase);
            targets.Traditional.Write(generatedTraditional);
            gameData.Save();
            IndexBackupService.Complete(gameData, indexBackup, "poe2-font", new Dictionary<string, string>
            {
                ["typeface"] = options.Typeface,
                ["changedEntries"] = generated.Changes.Count.ToString(CultureInfo.InvariantCulture),
                ["unknownEntries"] = generated.UnknownEntryCount.ToString(CultureInfo.InvariantCulture),
                ["sizes"] = string.Join(',', generated.Changes.Select(change =>
                    $"{change.Key}:{change.OfficialSize}->{change.TargetSize}")),
                ["baseVirtualPath"] = BaseVirtualPath,
                ["traditionalVirtualPath"] = TraditionalVirtualPath,
            });

            return new Poe2FontResult(
                gameData.GameDataPath,
                options.Typeface,
                generatedBase.Length,
                generatedTraditional.Length,
                backup.DirectoryPath,
                generated.Changes,
                generated.UnknownEntryCount);
        });
    }

    public static Poe2FontRestoreResult Restore(
        string gameDataPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backup = GetBaselinePaths(gameDataPath);
        if (!File.Exists(backup.BasePath) || !File.Exists(backup.TraditionalPath))
            throw new FileNotFoundException("尚未找到字体功能创建的原始文件备份。", backup.DirectoryPath);

        return GameDataLoader.Use(gameDataPath, GameDataMode.ReadWrite, gameData =>
        {
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
        });
    }

    public static bool HasBaseline(string gameDataPath)
    {
        var paths = GetBaselinePaths(gameDataPath);
        return File.Exists(paths.BasePath) && File.Exists(paths.TraditionalPath);
    }

    internal sealed record GeneratedBaseXml(
        byte[] Bytes,
        IReadOnlyList<Poe2FontChange> Changes,
        int UnknownEntryCount);

    internal static GeneratedBaseXml GenerateBaseXml(byte[] sourceBytes, Poe2FontOptions options)
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

        var template = Poe2FontTemplate.Default;
        var changes = new List<Poe2FontChange>();
        var unknownEntryCount = 0;
        foreach (var font in root.Descendants("Font"))
        {
            var id = font.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(id))
                continue;

            var typeface = font.Attribute("typeface");
            if (typeface is not null)
                typeface.Value = options.Typeface;

            if (!template.ByKey.TryGetValue(Poe2FontTemplate.MakeKey(ScopeOf(font), id), out var entry))
            {
                // 模板没收录（客户端版本不同）：宁可保持原样，也不要按猜出来的基准改写一个官方值。
                if (font.Attribute("size") is not null)
                    unknownEntryCount++;
                continue;
            }

            var target = options.Sizes.TryGetValue(entry.Key, out var overrideSize)
                ? overrideSize
                : entry.HasDeclaredSize ? ParsedDeclaredSize(entry) : (int?)null;
            if (target is not int targetSize)
                continue;

            var sizeAttribute = font.Attribute("size");
            if (sizeAttribute is not null)
            {
                // 即便是官方值也写回去：基线备份可能是玩家在第三方改版上首次应用时存的，
                // 只有按模板落一遍绝对字号才能保证「同一份配置在任何客户端上看起来一样」。
                sizeAttribute.Value = targetSize.ToString(CultureInfo.InvariantCulture);
            }
            else if (targetSize != entry.Size)
            {
                font.SetAttributeValue("size", targetSize);
            }
            else
            {
                continue;
            }

            if (targetSize != entry.Size)
                changes.Add(new Poe2FontChange(entry.Key, entry.Size, targetSize));
        }

        foreach (var fallback in root.Descendants("FallbackFont"))
        {
            var fallbackId = fallback.Attribute("id")?.Value;
            if (fallbackId is not ("CJK" or "Any"))
                continue;

            var fonts = (fallback.Attribute("fonts")?.Value ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(font => !string.Equals(font, options.Typeface, StringComparison.OrdinalIgnoreCase))
                .Prepend(options.Typeface);
            fallback.SetAttributeValue("fonts", string.Join(',', fonts));
        }

        root.AddFirst(new XComment(BuildComment(options, changes, unknownEntryCount)));
        return new GeneratedBaseXml(EncodeXml(document, encoding), changes, unknownEntryCount);
    }

    /// <summary>条目所在的嵌套作用域，形如 <c>PathOfExile/ETradeMarketPanel</c>；同名 id 靠它区分。</summary>
    internal static string ScopeOf(XElement element) => string.Join('/', element
        .Ancestors("Props")
        .Reverse()
        .Select(props => props.Attribute("id")?.Value)
        .Where(id => !string.IsNullOrEmpty(id)));

    private static string BuildComment(Poe2FontOptions options, IReadOnlyList<Poe2FontChange> changes, int unknownEntryCount)
    {
        var text = new StringBuilder($"{GeneratedCommentPrefix}：字体类型 = {SafeCommentValue(options.Typeface)}。");
        text.Append(changes.Count == 0
            ? "全部字号保持官方值"
            : $"共 {changes.Count} 项字号相对官方值调整：{string.Join("，", changes.Take(6).Select(change =>
                $"{SafeCommentValue(change.Key[(change.Key.LastIndexOf('/') + 1)..])} {change.OfficialSize}→{change.TargetSize}"))}"
                + (changes.Count > 6 ? $" 等共 {changes.Count} 项" : ""));
        if (unknownEntryCount > 0)
            text.Append($"；另有 {unknownEntryCount} 项不在内置模板内，保持原样。");
        return text.Append('。').ToString();
    }

    private static int ParsedDeclaredSize(Poe2FontTemplateEntry entry)
        => int.TryParse(entry.DeclaredSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : entry.Size;

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
        foreach (var (key, size) in options.Sizes)
        {
            if (size is < MinFontSize or > MaxFontSize)
                throw new ArgumentOutOfRangeException(nameof(options), $"条目 {key} 的字号必须在 {MinFontSize} 到 {MaxFontSize} 之间。 ");
        }
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
