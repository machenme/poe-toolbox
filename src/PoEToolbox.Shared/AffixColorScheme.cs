using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PoEToolbox.Shared;
/// <summary>
/// 词缀上色的上色方案：颜色定义 + 关键词规则组 + 手动指派。
/// 持久化为 <see cref="ConfigService.AffixSchemesDirectory"/> 下的 JSON（文件名 = 方案名），
/// 导入/导出同一格式，可直接分享（PRD Story 5）。
/// </summary>
public sealed class AffixColorScheme
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<AffixColorDef> Colors { get; set; } = [];
    public List<AffixRuleGroup> Rules { get; set; } = [];
    public List<AffixAssignment> Assignments { get; set; } = [];

    public AffixColorDef? FindColor(string id) => Colors.FirstOrDefault(c => c.Id == id);

    /// <summary>从颜色命名推断出的<strong>色阶</strong>（见 <see cref="AffixColorRamp.FromColors"/>）。</summary>
    public IReadOnlyList<AffixColorRamp> Ramps() => AffixColorRamp.FromColors(Colors);

    /// <summary>按前缀找色阶。</summary>
    public AffixColorRamp? FindRamp(string prefix)
        => Ramps().FirstOrDefault(r => string.Equals(r.Prefix, prefix, StringComparison.Ordinal));

    /// <summary>该 id 是否指向色阶（而不是单个颜色）：本身不是颜色、但能作为色阶前缀匹配到。</summary>
    public bool IsRampId(string id)
        => FindColor(id) is null && FindRamp(id) is not null;

    /// <summary>所有颜色 id（去重，保持顺序）。</summary>
    public IReadOnlyList<string> ColorIds => [.. Colors.Select(c => c.Id).Distinct()];

    /// <summary>指派/规则引用了不存在的颜色 id 时返回问题清单；空 = 方案自洽。</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        var known = new HashSet<string>(Colors.Select(c => c.Id), StringComparer.Ordinal);
        foreach (var rule in Rules.Where(r => !known.Contains(r.ColorId)))
            issues.Add($"规则「{rule.Name}」引用了未定义的颜色 {rule.ColorId}");
        foreach (var a in Assignments.Where(a => !known.Contains(a.ColorId)))
            issues.Add($"词缀 {a.StatKey} 的指派引用了未定义的颜色 {a.ColorId}");
        var duplicated = Colors.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var id in duplicated)
            issues.Add($"颜色 id 重复定义：{id}");
        return issues;
    }

    // ═══ 持久化 ═════════════════════════════════════════════════

    /// <summary>测试缝：覆盖方案存储目录（默认 ConfigService.AffixSchemesDirectory）。</summary>
    internal static Func<string>? StorageDirectoryOverride { get; set; }

    private static string StorageDirectory => StorageDirectoryOverride?.Invoke() ?? ConfigService.AffixSchemesDirectory;

    public static IReadOnlyList<string> ListSchemeNames()
    {
        if (!Directory.Exists(StorageDirectory))
            return [];
        return [.. Directory.GetFiles(StorageDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    public static AffixColorScheme Load(string name)
    {
        var path = PathOf(name);
        var scheme = JsonSerializer.Deserialize<AffixColorScheme>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidDataException($"方案文件为空：{path}");
        scheme.Name = name;
        return scheme;
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("方案名不能为空。");
        Directory.CreateDirectory(StorageDirectory);
        var serialized = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(PathOf(Name), serialized);
    }

    /// <summary>从外部 JSON 文件导入（方案名以目标文件名为准）。</summary>
    public static AffixColorScheme Import(string jsonPath, string? newName = null)
    {
        var scheme = JsonSerializer.Deserialize<AffixColorScheme>(File.ReadAllText(jsonPath), JsonOpts)
            ?? throw new InvalidDataException($"方案文件为空：{jsonPath}");
        scheme.Name = newName ?? scheme.Name;
        return scheme;
    }

    public void Export(string jsonPath) => File.WriteAllText(jsonPath, JsonSerializer.Serialize(this, JsonOpts));

    public static string PathOf(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            if (name.Contains(c))
                throw new ArgumentException($"方案名包含非法字符：{c}");
        return Path.Combine(StorageDirectory, name + ".json");
    }
}

/// <summary>一个颜色标签：id 对应 uisettings.xml 的 <c>&lt;Colour id&gt;</c> 与 csd 的包裹标签。
/// A 是透明度（0-255）：同一种色系靠 alpha 递减可以表达"等级深浅"
/// （如 255 / 205 / 155 / 105，越暗代表等级越低），游戏里两种写法都认。</summary>
public sealed record AffixColorDef(string Id, byte R, byte G, byte B, byte A = 255)
{
    [JsonIgnore]
    public string Hex => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    /// <summary>是否带透明度（写 uisettings 时决定用 3 段还是 4 段格式）。</summary>
    [JsonIgnore]
    public bool HasAlpha => A != 255;
}

/// <summary>关键词规则组：按包含匹配批量命中词缀（P1 不做正则，PRD 约束）。</summary>
public sealed record AffixRuleGroup(string Id, string Name, string ColorId, string Pattern, bool Enabled);

/// <summary>手动指派：某文件里的某个 stat 上某色。
/// <paramref name="LineText"/> 非空时只作用于显示文本与之相同的那些行——同一条 stat 常有
/// 多行变体（如「提高/降低」两个方向），行级指派可以把它们分开各上各的色。
/// 行匹配用剥离标签后的纯文本精确比对；空串 = 整条 stat（旧行为）。</summary>
public sealed record AffixAssignment(string StatKey, string FilePath, string ColorId, string LineText = "");

/// <summary>色阶：一组按档位从高到低排列的颜色 id（由颜色命名推断，见 <see cref="FromColors"/>）。
/// 用于"按数值分档染色"：区间最高的行用第一个颜色，依次往下。</summary>
public sealed record AffixColorRamp(string Prefix, IReadOnlyList<string> ColorIds)
{
    public string Label => $"{Prefix}（{ColorIds.Count} 级）";

    /// <summary>从颜色命名推断色阶：同前缀 + 数字（如 <c>AT1/AT2/AT3</c>），数字小的排前面（= 档位高、颜色亮）。
    /// 至少 2 个同前缀颜色才算一个色阶。不持久化——「新增下一级」生成的颜色会自动成组。</summary>
    public static IReadOnlyList<AffixColorRamp> FromColors(IEnumerable<AffixColorDef> colors)
    {
        var groups = new Dictionary<string, List<(int Level, string Id)>>(StringComparer.Ordinal);
        foreach (var color in colors)
        {
            var match = Regex.Match(color.Id, "^([A-Za-z]+)([0-9]+)$");
            if (!match.Success)
                continue;
            var prefix = match.Groups[1].Value;
            var level = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!groups.TryGetValue(prefix, out var list))
                groups[prefix] = list = [];
            list.Add((level, color.Id));
        }
        return
        [
            .. groups
                .Where(g => g.Value.Count >= 2)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new AffixColorRamp(g.Key, [.. g.Value.OrderBy(x => x.Level).Select(x => x.Id)])),
        ];
    }
}
