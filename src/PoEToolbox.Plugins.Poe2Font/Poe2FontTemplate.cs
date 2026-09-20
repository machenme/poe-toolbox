using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEToolbox.Plugins.Poe2Font;

public sealed record Poe2FontTemplateEntry(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("typeface")] string Typeface,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("declaredSize")] string DeclaredSize,
    [property: JsonPropertyName("inherits")] string Inherits,
    [property: JsonPropertyName("style")] string Style)
{
    /// <summary>写盘与界面共用的条目键。同名 id 会出现在不同作用域里，各自一份。</summary>
    public string Key => Poe2FontTemplate.MakeKey(Scope, Id);

    /// <summary>XML 里是否自带 size 属性；false 表示纯继承条目，默认不该被写死字号。</summary>
    public bool HasDeclaredSize => int.TryParse(DeclaredSize, out _);
}

public sealed record Poe2FontFallback(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ranges")] string Ranges,
    [property: JsonPropertyName("fonts")] string Fonts);

internal sealed record Poe2FontTemplateDocument(
    [property: JsonPropertyName("baseResolution")] int BaseResolution,
    [property: JsonPropertyName("fonts")] List<Poe2FontTemplateEntry> Fonts,
    [property: JsonPropertyName("fallbackFonts")] List<Poe2FontFallback> FallbackFonts);

/// <summary>
/// 内置官方字体模板：从未经修改的 <c>metadata/ui/uisettings.xml</c> 抽出的 156 条字号
/// （生成脚本见 <c>tools/BuildPoe2FontTemplate.ps1</c>）。它既是「整体 ±N 个字号」的基准，
/// 也让写入结果与玩家当前文件的差异无关——同一份配置在任何客户端上都落到同样的绝对字号。
/// </summary>
public sealed class Poe2FontTemplate
{
    private const string ResourceName = "PoEToolbox.Plugins.Poe2Font.Assets.official-uisettings-fonts.json";

    private static readonly Lazy<Poe2FontTemplate> Shared = new(Load);

    private Poe2FontTemplate(Poe2FontTemplateDocument document)
    {
        BaseResolution = document.BaseResolution;
        Entries = document.Fonts;
        ByKey = Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        FallbackFonts = document.FallbackFonts;
    }

    public static Poe2FontTemplate Default => Shared.Value;

    public int BaseResolution { get; }

    public IReadOnlyList<Poe2FontTemplateEntry> Entries { get; }

    public IReadOnlyDictionary<string, Poe2FontTemplateEntry> ByKey { get; }

    public IReadOnlyList<Poe2FontFallback> FallbackFonts { get; }

    public static string MakeKey(string scope, string id) => $"{scope}/{id}";

    private static Poe2FontTemplate Load()
    {
        using var stream = typeof(Poe2FontTemplate).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded font template: {ResourceName}");
        var document = JsonSerializer.Deserialize<Poe2FontTemplateDocument>(stream)
            ?? throw new InvalidOperationException("The embedded font template is empty.");
        return new Poe2FontTemplate(document);
    }
}
