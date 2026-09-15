using System.Text.RegularExpressions;
using PoEToolbox.Core.Binary.Datc64;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.AffixWorkbench;

/// <summary>
/// 从 <c>data/balance/mods.datc64</c> 提取「普通词缀的 tier 阶梯」：某个 stat 在各个物品/词缀线上的数值区间。
/// <para>
/// 同一个 stat 常有多条**互不相干**的线（如「魔力上限」有单手武器线 <c>IncreasedMana</c>、
/// 双手武器线 <c>IncreasedManaTwoHandWeapon</c>、武器上的法术伤害+魔力混合线 <c>SpellDamageAndManaOnWeapon</c>…），
/// 它们共用同一句显示文本，所以拆行时必须选一条线做基准——取**档数最多**的那条
/// （并列时取起点数值最小的），它在数据上就是最完整的基础 tier 线。
/// </para>
/// 只在首次需要分档上色时构建（解析 mods.datc64 约 1 秒级），之后缓存复用。
/// </summary>
public sealed class ModTierIndex
{
    private static readonly Regex KeyRegex = new(@"Key\((\d+)\)", RegexOptions.Compiled);
    private static readonly Regex RangeRegex = new(@"\[\s*(-?\d+)\s*,\s*(-?\d+)\s*\]", RegexOptions.Compiled);
    private static readonly Regex LineSuffixRegex = new(@"_?\d+_?$", RegexOptions.Compiled);

    /// <summary>mod Id 尾部的档位编号（<c>LightningDamagePercent5</c> → 5）。</summary>
    private static readonly Regex SuffixRegex = new(@"(\d+)_?$", RegexOptions.Compiled);

    private readonly Dictionary<string, IReadOnlyList<(int Min, int Max, int Tier, int LineTiers)>> _tiers = new(StringComparer.Ordinal);

    private ModTierIndex() { }

    /// <summary>有 tier 阶梯的词缀数量。</summary>
    public int StatCount => _tiers.Count;

    /// <summary>全部有 tier 阶梯的词缀名（用于"一键给所有装备词缀分档上色"）。</summary>
    public IReadOnlyCollection<string> StatKeys => _tiers.Keys;

    /// <summary>该词缀各档的数值区间、<strong>游戏内的等阶</strong>（1 = 最高档 T1）与所属线的档数
    /// （按数值升序；同一 stat 的多条线全部收录，同一等阶可能有多段区间）。没有阶梯时返回空。</summary>
    public IReadOnlyList<(int Min, int Max, int Tier, int LineTiers)> RangesOf(string statName)
        => _tiers.TryGetValue(statName, out var ranges) ? ranges : [];

    public static ModTierIndex Build(GameDataAccess gd)
    {
        var index = new ModTierIndex();

        var statsBytes = gd.ReadFile(StatsPath);
        var modsBytes = gd.ReadFile(ModsPath);
        if (statsBytes is null || modsBytes is null)
            return index; // 客户端没有这些表时静默降级（分档功能不可用，其他功能不受影响）

        var stats = Datc64File.FromBytes(statsBytes, Datc64Constants.TableNameFromPath(StatsPath));
        var statNames = stats.Rows.Select(r => Text(r.GetValueOrDefault("Id"))).ToList();
        var mods = Datc64File.FromBytes(modsBytes, Datc64Constants.TableNameFromPath(ModsPath));

        // stat → 线（mod Id 去掉尾部数字）→ 该线的档位区间
        var byStat = new Dictionary<string, Dictionary<string, List<(int Min, int Max)>>>(StringComparer.Ordinal);
        foreach (var row in mods.Rows)
        {
            var generation = Text(row.GetValueOrDefault("GenerationType"));
            if (generation != "1" && generation != "2")
                continue; // 只看普通前缀(1)/后缀(2)；传奇专属(3)、腐化(5) 等不算 tier 线
            var line = LineSuffixRegex.Replace(Text(row.GetValueOrDefault("Id")), "");
            for (var slot = 1; slot <= 8; slot++)
            {
                var refId = KeyRegex.Match(Text(row.GetValueOrDefault("Stat" + slot)));
                if (!refId.Success || !int.TryParse(refId.Groups[1].Value, out var rowIndex))
                    continue;
                if (rowIndex < 0 || rowIndex >= statNames.Count)
                    continue;
                var statName = statNames[rowIndex];
                if (statName.Length == 0)
                    continue;
                var range = RangeRegex.Match(Text(row.GetValueOrDefault("Stat" + slot + "Value")));
                if (!range.Success)
                    continue;
                var (min, max) = (int.Parse(range.Groups[1].Value), int.Parse(range.Groups[2].Value));
                if (!byStat.TryGetValue(statName, out var lines))
                    byStat[statName] = lines = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
                if (!lines.TryGetValue(line, out var ranges))
                    lines[line] = ranges = [];
                ranges.Add((min, max));
            }
        }

        foreach (var (statName, lines) in byStat)
        {
            var merged = new List<(int Min, int Max, int Tier, int LineTiers)>();
            foreach (var (_, ranges) in lines)
            {
                // 线内等阶按数值位置计算（升序第 i 档 → 等阶 n-i，最高值 = T1）。
                // 注：不能用 mod Id 尾号反推——个别线的编号与数值顺序不一致
                // （如 IncreasedManaTwoHandWeapon12=231-251 反而低于 11=299-328），
                // 但已核实的样本（闪电/冰霜/混沌抗性）证明「数值位置 = 游戏等阶」。
                // LineTiers = 该线档数，供色带合成时仲裁：重叠处信「档数最多的线」
                var ordered = ranges.Where(r => r.Min >= 0 && r.Max >= r.Min)
                    .Select(r => (r.Min, r.Max)).Distinct().OrderBy(r => r.Min).ToList();
                if (ordered.Count < 2)
                    continue; // 单档没有阶梯可言
                for (var i = 0; i < ordered.Count; i++)
                    merged.Add((ordered[i].Min, ordered[i].Max, ordered.Count - i, ordered.Count));
            }
            if (merged.Count < 2)
                continue;
            merged.Sort((a, b) => a.Min.CompareTo(b.Min));
            index._tiers[statName] = merged;
        }
        return index;
    }

    private const string StatsPath = "data/balance/stats.datc64";
    private const string ModsPath = "data/balance/mods.datc64";

    private static string Text(object? value) => value switch
    {
        null => "",
        // 列表列（如 Stat1Value）要保留成 [min, max] 形态，便于直接正则解析
        System.Collections.IEnumerable items and not string
            => "[" + string.Join(", ", items.Cast<object?>().Select(x => x?.ToString())) + "]",
        _ => value.ToString() ?? "",
    };
}
