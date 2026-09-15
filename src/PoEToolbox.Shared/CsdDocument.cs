using System.Text;
using System.Text.RegularExpressions;

namespace PoEToolbox.Shared;

/// <summary>
/// 词缀描述文件（data/statdescriptions/*.csd）的行级文档模型。
/// csd 是 tab 缩进的行式文本：顶层为 description / include "..." / no_description &lt;id&gt;；
/// stat 块头是「&lt;id数量&gt; &lt;id...&gt;」，块内 lang "语言" 切换语言段，
/// 显示文本在缩进 ≥2 的行内引号中（每行一个，无转义引号）。
/// 解析→序列化必须与原字节一致（CsdDocumentTests 的 round-trip 是所有写操作的安全底座）；
/// 上色只触碰语言段内引号中的显示文本，外来标签（非本方案 id）原样保留。
/// <para>
/// 语言只处理三种：<b>简中 &gt; 繁中 &gt; 英文</b>（英文是块内无 lang 标记的默认段）。
/// 显示文本与上色都以「客户端语言」优先（由 <see cref="AffixDataService"/> 判定并传入）：
/// 简体中文客户端只给简中段上色、繁体中文客户端只给繁中段上色，缺该段时按上述顺序退回；
/// <b>一个词缀只打一处标签</b>。其余 7 种语言不跟踪、不改动。
/// </para>
/// 颜色标签语法与实例补丁一致：csd 内 <c>&lt;ColorId&gt;{{原文}}</c><c></ColorId></c>，
/// uisettings.xml 内对应 <c>&lt;Colour id="ColorId" value="r,g,b"/&gt;</c>（见 UISettingsDoc）。
/// </summary>
public sealed class CsdDocument
{
    /// <summary>默认语言段的键名（csd 里英文段没有 lang 标记，是 stat 块内第一段）。</summary>
    public const string DefaultLanguage = "English";

    /// <summary>跟踪的语言及其优先级：取文本按此顺序回退，上色对存在的段都生效。</summary>
    public static readonly IReadOnlyList<string> LanguagePriority =
    [
        "Simplified Chinese",
        "Traditional Chinese",
        DefaultLanguage,
    ];

    /// <summary>兼容旧名：目标语言 = 优先级最高的简体中文。</summary>
    public const string TargetLanguage = "Simplified Chinese";

    /// <summary>颜色标签 id 约束：字母开头、字母数字、≤16 字符，避开 XML 名与 csd 语法的保留字符。</summary>
    public static readonly Regex ColorIdPattern = new("^[A-Za-z][A-Za-z0-9]{0,15}$", RegexOptions.Compiled);

    private static readonly Regex LangLineRegex = new("""^\t*lang\s+"([^"]*)"\s*$""", RegexOptions.Compiled);
    private static readonly Regex QuotedRegex = new("\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_%+.-]*$", RegexOptions.Compiled);

    /// <summary>显示行里的数值占位符（连同紧随的 <c>%</c>）：<c>{0}</c> / <c>{0:+d}</c> / <c>{0}%</c>。</summary>
    internal static readonly Regex ValuePlaceholderRegex = new(@"\{[^{}]*\}%?", RegexOptions.Compiled);

    /// <summary>显示行行首的区间标记：<c>125|149</c> / <c>1|#</c> / <c>#|-1</c>（<c>#</c> = 无界）。</summary>
    private static readonly Regex RangeHeadRegex = new(@"^([-0-9#]+)\|([-0-9#]+)\s", RegexOptions.Compiled);

    /// <summary>带正负号的数值占位符（<c>{0:+d}</c>）——有它才说明该词缀需要按正负拆成两行。</summary>
    private static readonly Regex SignedPlaceholderRegex = new(@"\{[^{}]*\+[^{}]*\}", RegexOptions.Compiled);

    /// <summary>无区间的显示行行首（<c>\t\t# </c>），用于按正负拆分时改写行首。</summary>
    private static readonly Regex PlainHeadRegex = new(@"^(\t+)#\s", RegexOptions.Compiled);

    /// <summary>负向全区间行首（<c>#|-1</c>）：原版方向拆行 / 本工具拆出的「负值行」，按负向分档时改写它。</summary>
    private static readonly Regex NegativeFullHeadRegex = new(@"^(\t+)#\|-1\s", RegexOptions.Compiled);

    /// <summary>可作为「按档拆行」起点的行首：无区间 <c>#</c>，或原版自带的正向全区间 <c>1|#</c>
    /// （如「攻击技能的闪电伤害提高」这类原版方向拆行词缀的正值行）。<c>#|-1</c> 这类负值行不匹配。</summary>
    private static readonly Regex TierSplitHeadRegex = new(@"^(\t+)(?:#|1\|#)\s", RegexOptions.Compiled);

    private readonly List<string> _lines = [];
    private readonly List<string> _endings = [];
    private readonly Encoding _encoding;
    private readonly byte[] _bom;
    private readonly List<CsdStat> _stats = [];
    private readonly Dictionary<string, CsdStat> _statByKey = new(StringComparer.Ordinal);

    private CsdDocument(Encoding encoding, byte[] bom)
    {
        _encoding = encoding;
        _bom = bom;
    }

    /// <summary>解析出的 stat 条目（顺序与文件一致）。不含 no_description 条目。</summary>
    public IReadOnlyList<CsdStat> Stats => _stats;

    public static CsdDocument Parse(byte[] bytes)
    {
        var (encoding, bom) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length);
        var doc = new CsdDocument(encoding, bom);
        doc.SplitLines(text);
        doc.BuildIndex();
        return doc;
    }

    /// <summary>序列化回字节，编码/BOM/行尾与解析源完全一致。</summary>
    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _lines.Count; i++)
        {
            sb.Append(_lines[i]);
            sb.Append(_endings[i]);
        }
        var payload = _encoding.GetBytes(sb.ToString());
        if (_bom.Length == 0)
            return payload;
        var result = new byte[_bom.Length + payload.Length];
        Buffer.BlockCopy(_bom, 0, result, 0, _bom.Length);
        Buffer.BlockCopy(payload, 0, result, _bom.Length, payload.Length);
        return result;
    }

    /// <summary>给 stat 的<strong>客户端语言段</strong>包裹颜色标签：<paramref name="preferredLanguage"/> 有该段就打该段，
    /// 否则按 简中 &gt; 繁中 &gt; 英文 取第一段有的；其余语言不动、其他段也不动（一个词缀只打一处）。
    /// 已带「&lt;x&gt;{{...}}</x&gt;」形态标签的文本跳过（分层共栖）。</summary>
    /// <returns>false = stat 不存在或该 stat 三种语言都没有显示文本。</returns>
    public bool TryApplyColor(string statKey, string colorId, string? preferredLanguage = null)
    {
        if (!ColorIdPattern.IsMatch(colorId))
            throw new ArgumentException($"颜色标签 id 不合法：{colorId}", nameof(colorId));
        var stat = FindStat(statKey);
        if (stat is null)
            return false;

        var wrapped = 0;
        foreach (var lineIndex in stat.ResolveLines(preferredLanguage))
        {
            var replaced = QuotedRegex.Replace(_lines[lineIndex], match =>
            {
                var text = match.Groups[1].Value;
                if (text.Contains('<') && text.Contains("{{"))
                    return match.Value; // 已是标签包裹（含外来标签），不动
                wrapped++;
                return $"\"<{colorId}>{{{{{text}}}}}\"";
            });
            _lines[lineIndex] = replaced;
        }
        return wrapped > 0;
    }

    /// <summary>只给行内的<strong>数值占位符</strong>包颜色标签（<c>{0}%</c> → <c>&lt;X&gt;{{0}}%</c>），
    /// 不包整段文本——与"数值分档染色"的做法一致：游戏里只有数字变色，词缀名保持默认色。</summary>
    /// <returns>false = stat 不存在、该语言没有显示行、或行里没有数值占位符。</returns>
    public bool TryApplyColorToValue(string statKey, string colorId, string? preferredLanguage = null)
    {
        if (!ColorIdPattern.IsMatch(colorId))
            throw new ArgumentException($"颜色标签 id 不合法：{colorId}", nameof(colorId));
        var stat = FindStat(statKey);
        if (stat is null)
            return false;

        var wrapped = 0;
        foreach (var lineIndex in stat.ResolveLines(preferredLanguage))
        {
            if (ApplyValueColor(lineIndex, colorId))
                wrapped++;
        }
        return wrapped > 0;
    }

    /// <summary>按行首数值区间从高到低依次使用色阶颜色，且只给数值占位符染色；
    /// 对"没有区间但带正负号"的行会先拆成提高/降低两行（降低行用 <paramref name="negativeColorIds"/> 的首档）。
    /// <paramref name="colorIds"/> 按「最高档 → 最低档」排列；行数多于色阶时，多出来的低档行沿用最后一个颜色。</summary>
    public bool TryApplyColorRamp(
        string statKey,
        IReadOnlyList<string> colorIds,
        IReadOnlyList<string>? negativeColorIds = null,
        string? preferredLanguage = null)
    {
        if (colorIds.Count == 0)
            return false;
        foreach (var id in colorIds)
        {
            if (!ColorIdPattern.IsMatch(id))
                throw new ArgumentException($"颜色标签 id 不合法：{id}", nameof(colorIds));
        }
        if (negativeColorIds is { Count: > 0 })
        {
            foreach (var id in negativeColorIds)
            {
                if (!ColorIdPattern.IsMatch(id))
                    throw new ArgumentException($"颜色标签 id 不合法：{id}", nameof(negativeColorIds));
            }
        }
        var stat = FindStat(statKey);
        if (stat is null)
            return false;

        // ① 先把"无区间 + 带正负号"的行拆成正/负两行（倒序插入，避免行号漂移）
        var splitTargets = stat.ResolveLines(preferredLanguage).Where(CanSplitDirectional).ToList();
        var negativeColor = negativeColorIds is { Count: > 0 } ? negativeColorIds[0] : colorIds[^1];
        var splitLanguage = stat.ResolveLanguage(preferredLanguage);
        for (var t = splitTargets.Count - 1; t >= 0; t--)
            SplitDirectionalLine(splitTargets[t], splitLanguage, stat, colorIds[0], negativeColor);

        // ② 有区间的行按区间从高到低分档；其余行只染数值（用首档颜色）
        var wrapped = 0;
        var ranked = stat.ResolveLines(preferredLanguage)
            .Where(i => RangeHeadRegex.IsMatch(_lines[i].TrimStart('\t')))
            .Select(i => (LineIndex: i, Key: RangeKey(_lines[i])))
            .OrderByDescending(x => x.Key)
            .ToList();

        for (var rank = 0; rank < ranked.Count; rank++)
        {
            var colorId = IsNegativeRange(_lines[ranked[rank].LineIndex])
                ? negativeColor
                : colorIds[Math.Min(rank, colorIds.Count - 1)];
            if (ApplyValueColor(ranked[rank].LineIndex, colorId))
                wrapped++;
        }
        var rankedIndexes = ranked.Select(x => x.LineIndex).ToHashSet();
        foreach (var lineIndex in stat.ResolveLines(preferredLanguage))
        {
            if (rankedIndexes.Contains(lineIndex))
                continue;
            if (ApplyValueColor(lineIndex, colorIds[0]))
                wrapped++;
        }
        return wrapped > 0 || splitTargets.Count > 0;
    }

    /// <summary>该行的区间是否只覆盖非正值（如 <c>#|-1</c>）——这类行属于"降低/负面"方向。</summary>
    private static bool IsNegativeRange(string line)
    {
        var match = RangeHeadRegex.Match(line.TrimStart('\t'));
        if (!match.Success)
            return false;
        var upper = match.Groups[2].Value;
        return upper != "#" && long.TryParse(upper, out var value) && value <= 0;
    }

    /// <summary>把「无区间但带正负号占位符」的显示行<strong>拆成两行</strong>并分别染色：
    /// 正值行 <c>1|# "…{0:+d}"</c> 用 <paramref name="positiveColorId"/>、
    /// 负值行 <c>#|-1 "…{0:+d}" negate 1</c> 用 <paramref name="negativeColorId"/>。
    /// 与"数值分档"补丁对技能等级那类词缀的做法一致；只有数值占位符被染色，词缀名保持默认色。</summary>
    /// <returns>false = stat 不存在，或没有可拆的行（已有区间 / 没有正负占位符 / 已带颜色标签）。</returns>
    public bool TrySplitByDirection(string statKey, string positiveColorId, string negativeColorId, string? preferredLanguage = null)
    {
        foreach (var id in new[] { positiveColorId, negativeColorId })
        {
            if (!ColorIdPattern.IsMatch(id))
                throw new ArgumentException($"颜色标签 id 不合法：{id}", nameof(positiveColorId));
        }
        var stat = FindStat(statKey);
        if (stat is null)
            return false;

        var targets = stat.ResolveLines(preferredLanguage).Where(CanSplitDirectional).ToList();
        if (targets.Count == 0)
            return false;

        var language = stat.ResolveLanguage(preferredLanguage);
        // 倒序处理：插入新行会让后面的行号整体后移
        for (var t = targets.Count - 1; t >= 0; t--)
            SplitDirectionalLine(targets[t], language, stat, positiveColorId, negativeColorId);
        return true;
    }

    /// <summary>按 tier 阶梯<strong>拆开显示行并只给最高的若干档染色</strong>（默认只染 T1~T5，更低的档不上色）：
    /// 每档一行 <c>min|max "原文本"</c>，最高档用 <c>|#</c> 兜住超出范围的值，未染色的低档合并成一行 <c>1|…</c> 兜底。
    /// 只染数值占位符；<paramref name="tierRanges"/> 按数值升序（最低档在前）、<paramref name="colorIds"/> 按 T1→Tn 排列（最亮在前）。
    /// 可拆的起点行 = 无区间行（<c>#</c>）与原版自带的正向全区间行（<c>1|#</c>，如「攻击技能的闪电伤害提高」）；
    /// 其余已带区间的行（含 <c>#|-1</c> 负值行）不拆，负值行统一染最暗一档。</summary>
    public bool TrySplitByTier(
        string statKey,
        IReadOnlyList<(int Min, int Max, int Tier, int LineTiers)> tierRanges,
        IReadOnlyList<string> colorIds,
        int maxColoredTiers = 5,
        string? preferredLanguage = null)
    {
        if (tierRanges.Count == 0 || colorIds.Count == 0 || maxColoredTiers <= 0)
            return false;
        foreach (var id in colorIds)
        {
            if (!ColorIdPattern.IsMatch(id))
                throw new ArgumentException($"颜色标签 id 不合法：{id}", nameof(colorIds));
        }
        var stat = FindStat(statKey);
        if (stat is null)
            return false;

        // 把「多线 + 各线等阶」压成一条互不重叠、按数值升序的色带序列
        var bands = BuildTierBands(tierRanges, maxColoredTiers);
        if (bands.Count == 0)
            return false;

        // 起点行：无区间行，以及原版自带的正向全区间行 1|#（如「攻击技能的闪电伤害提高」）。
        // 后者此前会被跳过、掉进按正负染色的兜底路径——正向一律用首档色，导致低档（T5）也显示最亮色。
        var targets = stat.ResolveLines(preferredLanguage)
            .Where(i => TierSplitHeadRegex.IsMatch(_lines[i]))
            .ToList();

        var language = stat.ResolveLanguage(preferredLanguage);
        // 倒序处理：插入新行会让后面的行号整体后移
        for (var t = targets.Count - 1; t >= 0; t--)
            SplitByTierLine(targets[t], language, stat, bands, colorIds);

        // 负向行（#|-1 … negate 1）按「绝对值由大到小」镜像分档：|-5| 与 |+5| 同为 T1
        var negativeTargets = stat.ResolveLines(preferredLanguage)
            .Where(i => NegativeFullHeadRegex.IsMatch(_lines[i]))
            .ToList();
        for (var t = negativeTargets.Count - 1; t >= 0; t--)
            SplitNegativeByTier(negativeTargets[t], language, stat, bands, colorIds);

        // 其余负向行（游戏原版自带的 -99|-1 等）不拆，统一染最暗一档
        foreach (var lineIndex in stat.ResolveLines(preferredLanguage))
        {
            if (IsNegativeRange(_lines[lineIndex]))
                ApplyValueColor(lineIndex, colorIds[^1]);
        }
        return targets.Count > 0 || negativeTargets.Count > 0;
    }

    /// <summary>把「多线 + 各线等阶」压成一条<strong>互不重叠、按数值升序</strong>的色带序列。
    /// 同一个等阶可以有多段不连续区间（戒指线 27-30 与武器线 105-119 同为 T1）。
    /// 区间重叠时的仲裁：取<strong>档数最多的线</strong>的等阶（最完整的线最权威，如冰霜抗性取 8 档主线而非 6 档手甲线），
    /// 平手取更好的档；等阶超过染色窗口的段不染色。</summary>
    private static List<(long Lo, long Hi, int? Tier)> BuildTierBands(
        IReadOnlyList<(int Min, int Max, int Tier, int LineTiers)> tierRanges,
        int maxColoredTiers)
    {
        // 防御：只滤负数与 min>max（游戏原版 csd 自己就在用 min==max 单点标记，合法）
        var clean = tierRanges.Where(r => r.Min >= 0 && r.Max >= r.Min && r.Tier >= 1).ToList();
        if (clean.Count == 0)
            return [];

        var points = new SortedSet<long>();
        foreach (var r in clean)
        {
            points.Add(r.Min);
            points.Add((long)r.Max + 1);
        }
        var bounds = points.ToList();

        var bands = new List<(long Lo, long Hi, int? Tier)>();
        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            var lo = Math.Max(bounds[i], 1); // 左端恒 ≥1：0|… 这种标记游戏不认
            var hi = bounds[i + 1] - 1;
            if (hi < lo)
                continue;

            int? best = null;
            (int Tier, int Weight)? pick = null;
            foreach (var r in clean)
            {
                if (r.Min > lo || r.Max < hi)
                    continue; // 该档不覆盖这一段
                // 重叠仲裁：信档数最多的线（即使它超出染色窗口），平手取更好的档
                if (pick is null || r.LineTiers > pick.Value.Weight
                    || (r.LineTiers == pick.Value.Weight && r.Tier < pick.Value.Tier))
                    pick = (r.Tier, r.LineTiers);
            }
            if (pick is { } p && p.Tier <= maxColoredTiers)
                best = p.Tier; // 权威线的等阶超出染色窗口 → 该段不染色

            // 与前一色带同档且相邻 → 合并
            if (bands.Count > 0 && bands[^1].Tier == best && bands[^1].Hi + 1 == lo)
                bands[^1] = (bands[^1].Lo, hi, best);
            else
                bands.Add((lo, hi, best));
        }
        return bands;
    }

    /// <summary>把一行按色带拆成多行；等阶 1 用 colorIds[0]（T1），未染色的段保持原文本。</summary>
    private void SplitByTierLine(
        int lineIndex,
        string language,
        CsdStat stat,
        IReadOnlyList<(long Lo, long Hi, int? Tier)> bands,
        IReadOnlyList<string> colorIds)
    {
        var line = _lines[lineIndex];
        // 起点行可能是无区间行（#），也可能是原版自带的正向全区间行（1|#）——两者都要能改写行首
        var head = TierSplitHeadRegex.Match(line);
        if (!head.Success)
            return;
        var indent = head.Groups[1].Value;
        var rest = line[head.Length..]; // 引号内容及其后的参数（原 # / 1|# 前缀已随 head 剥离）
        if (!QuotedRegex.IsMatch(rest))
            return;

        var built = new List<string>();
        // 兜底行覆盖 [1, 最低色带-1]；最低色带从 1 开始时省略（生成 1|0 会被游戏拒载）
        if (bands[0].Lo > 1)
            built.Add($"{indent}1|{bands[0].Lo - 1} {rest}");

        for (var i = 0; i < bands.Count; i++)
        {
            var isLast = i == bands.Count - 1;
            var upperText = isLast ? "#" : bands[i].Hi.ToString(); // 最高段用 |# 兜住更大数值
            var content = bands[i].Tier is int tier
                ? WrapValue(rest, colorIds[Math.Min(tier, colorIds.Count) - 1])
                : rest;
            built.Add($"{indent}{bands[i].Lo}|{upperText} {content}");
        }

        _lines[lineIndex] = built[0];
        for (var i = 1; i < built.Count; i++)
        {
            InsertLineAt(lineIndex + i, built[i]);
            stat.AddLine(language, lineIndex + i); // 新行也要登记到该 stat
        }
        BumpCountLine(stat, language, built.Count - 1); // 段数量行同步：显示行多了 built.Count-1 行
    }

    /// <summary>把负向全区间行（<c>#|-1 … negate 1</c>）按 <strong>绝对值</strong> 镜像分档：
    /// 绝对值最大的档与正向最高档同为 T1（如 -5 与 +5 都按 T1 染），依次往下递减；
    /// 低于染色窗口的合并成一行「-N|-1」兜底（不上色）。行序为「最负在前」，各档互不重叠。</summary>
    private void SplitNegativeByTier(
        int lineIndex,
        string language,
        CsdStat stat,
        IReadOnlyList<(long Lo, long Hi, int? Tier)> bands,
        IReadOnlyList<string> colorIds)
    {
        var line = _lines[lineIndex];
        var head = NegativeFullHeadRegex.Match(line);
        if (!head.Success)
            return;
        var indent = head.Groups[1].Value;
        var rest = line[head.Length..]; // 引号内容 + negate 1 等参数
        if (!QuotedRegex.IsMatch(rest))
            return;

        // 原始值是负数，区间按原始值书写：正向段 [Lo,Hi] 镜像为原始值 [-Hi,-Lo]。
        // 行序「最负在前」：从数值最高的色带开始往下镜像，与正向色带一一对应
        var built = new List<string>();
        long prevHi = long.MinValue; // 上一段（更负）的最不负端，用于 clamp 保证不重叠
        for (var i = bands.Count - 1; i >= 0; i--)
        {
            var isTop = i == bands.Count - 1;
            // 最高段下界无界（#），兜住绝对值更大的值；右端恒 ≤ -1（0 会跨正负，游戏不认）
            var lo = isTop ? long.MinValue : -bands[i].Hi;
            var hi = Math.Min(-bands[i].Lo, -1);
            if (!isTop)
                lo = Math.Max(lo, prevHi + 1);
            if (hi < lo)
                continue; // 该段已被更负的段覆盖
            var content = bands[i].Tier is int tier
                ? WrapValue(rest, colorIds[Math.Min(tier, colorIds.Count) - 1])
                : rest;
            var loText = isTop ? "#" : lo.ToString();
            built.Add($"{indent}{loText}|{hi} {content}");
            prevHi = hi;
        }

        // 绝对值低于最低色带的部分合并成兜底行（不上色），与正向的 1|floor 兜底对称
        var floorMagnitude = bands[0].Lo - 1;
        if (floorMagnitude >= 1)
        {
            var lo = Math.Max(-floorMagnitude, prevHi + 1);
            if (-1 >= lo)
                built.Add($"{indent}{lo}|-1 {rest}");
        }

        _lines[lineIndex] = built[0];
        for (var i = 1; i < built.Count; i++)
        {
            InsertLineAt(lineIndex + i, built[i]);
            stat.AddLine(language, lineIndex + i); // 新行也要登记到该 stat
        }
        BumpCountLine(stat, language, built.Count - 1); // 段数量行同步
    }

    /// <summary>该行能否按正负拆分：无区间前缀、含带符号占位符、且未带颜色标签。</summary>
    private bool CanSplitDirectional(int lineIndex)
    {
        var line = _lines[lineIndex];
        if (RangeHeadRegex.IsMatch(line.TrimStart('\t')))
            return false; // 文件已给出区间（方向或档位）
        var match = QuotedRegex.Match(line);
        if (!match.Success)
            return false;
        var text = match.Groups[1].Value;
        if (text.Contains('<') && text.Contains("{{"))
            return false; // 已带标签（含外来标签）
        return SignedPlaceholderRegex.IsMatch(text);
    }

    /// <summary>把一行拆成正/负两行，复刻外部补丁的写法（<c>1|#</c> 与 <c>#|-1 … negate 1</c>）。</summary>
    private void SplitDirectionalLine(int lineIndex, string language, CsdStat stat, string positiveColorId, string negativeColorId)
    {
        var line = _lines[lineIndex];
        var head = PlainHeadRegex.Match(line);
        if (!head.Success)
            return;
        var indent = head.Groups[1].Value;
        var rest = line[head.Length..]; // 引号内容及其后的参数
        if (!QuotedRegex.IsMatch(rest))
            return;

        _lines[lineIndex] = indent + "1|# " + WrapValue(rest, positiveColorId);
        InsertLineAt(lineIndex + 1, indent + "#|-1 " + WrapValue(rest, negativeColorId) + " negate 1");
        stat.AddLine(language, lineIndex + 1); // 新行也要登记到该 stat，否则它只知道原来那一行
        BumpCountLine(stat, language, 1); // 段数量行同步：显示行多了 1 行
    }

    /// <summary>在指定位置插入一行，并同步所有 stat 里记录的行号（插入点及之后的都 +1）。</summary>
    private void InsertLineAt(int index, string content)
    {
        var ending = index > 0 && index - 1 < _endings.Count ? _endings[index - 1] : "\r\n";
        _lines.Insert(index, content);
        _endings.Insert(index, ending);
        foreach (var stat in _stats)
        {
            foreach (var lines in stat.Lines.Values)
            {
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i] >= index)
                        lines[i]++;
                }
            }
            foreach (var language in stat.CountLines.Keys)
            {
                if (stat.CountLines[language] >= index)
                    stat.CountLines[language]++;
            }
        }
    }

    /// <summary>把某语言段的数量行（= 段内显示行数）递增 <paramref name="delta"/>。
    /// 拆行插入新显示行后必须调用，否则游戏按旧数量读行、错位后整文件拒载。</summary>
    private void BumpCountLine(CsdStat stat, string language, int delta)
    {
        if (delta == 0 || !stat.CountLines.TryGetValue(language, out var index))
            return;
        var line = _lines[index];
        var trimmed = line.TrimStart('\t').Trim();
        if (!int.TryParse(trimmed, out var count))
            return;
        _lines[index] = line[..(line.Length - trimmed.Length)] + (count + delta);
    }

    /// <summary>给文本里第一个数值占位符包颜色标签（没有占位符则原样返回）。</summary>
    private static string WrapValue(string text, string colorId)
    {
        var placeholder = ValuePlaceholderRegex.Match(text);
        if (!placeholder.Success)
            return text;
        return string.Concat(
            text.AsSpan(0, placeholder.Index),
            $"<{colorId}>{{{{{placeholder.Value}}}}}",
            text.AsSpan(placeholder.Index + placeholder.Length));
    }

    /// <summary>给一行里的第一个数值占位符包颜色标签；已带标签的行原样返回。</summary>
    private bool ApplyValueColor(int lineIndex, string colorId)
    {
        var wrapped = 0;
        var replaced = QuotedRegex.Replace(_lines[lineIndex], match =>
        {
            var text = match.Groups[1].Value;
            if (text.Contains('<') && text.Contains("{{"))
                return match.Value; // 已带标签（含外来标签），不动
            if (!ValuePlaceholderRegex.IsMatch(text))
                return match.Value; // 该行没有数值占位符
            wrapped++;
            return "\"" + WrapValue(text, colorId) + "\"";
        });
        _lines[lineIndex] = replaced;
        return wrapped > 0;
    }

    /// <summary>行首区间的排序键（取区间下限；<c>#</c> = 无界，按最小算）——用于"数值越高档位越高"。</summary>
    private static long RangeKey(string line)
    {
        var match = RangeHeadRegex.Match(line.TrimStart('\t'));
        if (!match.Success)
            return long.MinValue;
        var lower = match.Groups[1].Value;
        return lower != "#" && long.TryParse(lower, out var value) ? value : long.MinValue;
    }

    /// <summary>全文档剥离指定颜色标签（只动方案拥有的 id，外来标签保留）。返回剥离的标签数。
    /// 同时认两种写法：<c>&lt;X&gt;{{...}}&lt;/X&gt;</c>（本工具生成）与 <c>&lt;X&gt;{{...}}</c>（外部补丁常用的无闭合形式）。</summary>
    public int StripColors(IReadOnlyCollection<string> colorIds)
    {
        var total = 0;
            foreach (var id in colorIds.Where(i => ColorIdPattern.IsMatch(i)).Distinct())
            {
                var marker = "<" + id + ">";
                // 内容按「非花括号字符或完整花括号组」匹配，避免懒惰匹配吞掉数值占位符的右括号
                var regex = new Regex(
                    Regex.Escape(marker) + "\\{\\{((?:[^{}]|\\{[^{}]*\\})*)\\}\\}(?:" + Regex.Escape("</" + id + ">") + ")?",
                    RegexOptions.Compiled);
            for (var i = 0; i < _lines.Count; i++)
            {
                if (!_lines[i].Contains(marker))
                    continue;
                var count = 0;
                _lines[i] = regex.Replace(_lines[i], m => { count++; return m.Groups[1].Value; });
                total += count;
            }
        }
        return total;
    }

    /// <summary>按语言优先级取显示文本做预览分段（颜色标签分段 + [Tag|文本] 解析为文本）；
    /// 一条 stat 可能有多行文本，按行拼接（搜索/匹配用的折叠形式，见 <see cref="GetPreviewLines"/>）。</summary>
    public IReadOnlyList<CsdTextSegment> GetPreviewSegments(string statKey, string? preferredLanguage = null)
        => [.. GetPreviewLines(statKey, preferredLanguage).SelectMany(line => line)];

    /// <summary>按<strong>显示行</strong>分别返回预览分段：csd 里一条 stat 常有多行文本
    /// （"提高/降低"两个方向、或不同档位区间），游戏按实际数值只显示其中一行，
    /// 所以列表要逐行呈现，拼成一段会失真。</summary>
    public IReadOnlyList<IReadOnlyList<CsdTextSegment>> GetPreviewLines(string statKey, string? preferredLanguage = null)
    {
        var stat = FindStat(statKey);
        if (stat is null)
            return [];
        var lines = new List<IReadOnlyList<CsdTextSegment>>();
        foreach (var lineIndex in stat.ResolveLines(preferredLanguage))
        {
            var sb = new StringBuilder();
            foreach (Match m in QuotedRegex.Matches(_lines[lineIndex]))
                sb.Append(m.Groups[1].Value);
            if (sb.Length == 0)
                continue;
            lines.Add(CsdMarkup.Parse(sb.ToString()));
        }
        return lines;
    }

    /// <summary>按显示行返回纯文本（已剥离颜色标签与 [Tag|x] 标记），用于列表分行预览。</summary>
    public IReadOnlyList<string> GetDisplayLines(string statKey, string? preferredLanguage = null)
        => [.. GetPreviewLines(statKey, preferredLanguage).Select(line => string.Concat(line.Select(s => s.Text)))];

    /// <summary>该 stat 的显示文本实际来自哪种语言（客户端语言优先，其次 简中 &gt; 繁中 &gt; 英文）。</summary>
    public string ResolveLanguage(string statKey, string? preferredLanguage = null)
        => FindStat(statKey)?.ResolveLanguage(preferredLanguage) ?? preferredLanguage ?? DefaultLanguage;

    /// <summary>显示文本的纯文本（多行拼接、剥离所有标签与 [Tag|x] 标记），用于搜索与规则匹配。</summary>
    public string GetDisplayText(string statKey, string? preferredLanguage = null)
        => string.Concat(GetDisplayLines(statKey, preferredLanguage));

    /// <summary>文档内是否存在任一指定颜色标签（用于跳过无需重写的文件）。</summary>
    public bool ContainsAnyColorTag(IReadOnlyCollection<string> colorIds)
    {
        foreach (var line in _lines)
        {
            if (!line.Contains('<'))
                continue;
            foreach (var id in colorIds)
            {
                if (ColorIdPattern.IsMatch(id) && line.Contains("<" + id + ">", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private CsdStat? FindStat(string statKey)
        => _statByKey.TryGetValue(statKey, out var stat) ? stat : null;

    // ═══ 解析 ═══════════════════════════════════════════════════

    private void SplitLines(string text)
    {
        var start = 0;
        while (true)
        {
            var nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                if (start < text.Length || _lines.Count == 0)
                {
                    _lines.Add(text[start..]);
                    _endings.Add("");
                }
                break;
            }
            var contentEnd = nl;
            string ending = "\n";
            if (contentEnd > start && text[contentEnd - 1] == '\r')
            {
                contentEnd--;
                ending = "\r\n";
            }
            _lines.Add(text[start..contentEnd]);
            _endings.Add(ending);
            start = nl + 1;
        }
    }

    private void BuildIndex()
    {
        CsdStat? current = null;
        string? currentLang = null;
        // 块头 / lang 行之后，段内第一行「纯数字行」就是该语言段的数量行
        var awaitCountLine = false;

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Length == 0 || line[0] != '\t')
            {
                // 顶层行（description / include / no_description / 空行）：块边界
                current = null;
                currentLang = null;
                awaitCountLine = false;
                continue;
            }

            var langMatch = LangLineRegex.Match(line);
            if (langMatch.Success)
            {
                currentLang = langMatch.Groups[1].Value;
                awaitCountLine = true;
                continue;
            }

            var content = line.TrimStart('\t');
            var indent = line.Length - content.Length;
            var spaceIndex = content.IndexOf(' ');
            var firstToken = spaceIndex < 0 ? content : content[..spaceIndex];
            var rest = spaceIndex < 0 ? "" : content[(spaceIndex + 1)..].Trim();

            if (indent == 1)
            {
                if (firstToken.All(char.IsDigit) && rest.Length > 0)
                {
                    // stat 块头：「<id数量> <id...>」
                    current = new CsdStat(rest);
                    _stats.Add(current);
                    currentLang = null; // 头之后的第一段是默认语言（英文）
                    awaitCountLine = true;
                }
                else if (IdentifierRegex.IsMatch(firstToken))
                {
                    // 无数量前缀的块头（容错）：整行当作 id 串
                    current = new CsdStat(content);
                    _stats.Add(current);
                    currentLang = null;
                    awaitCountLine = true;
                }
                else if (awaitCountLine && current is not null && firstToken.All(char.IsDigit))
                {
                    // 语言段的数量行：登记行号（拆行插入显示行后必须同步递增）
                    current.SetCountLine(currentLang ?? DefaultLanguage, i);
                    awaitCountLine = false;
                }
                // 纯数字层级行 / 其他未知行：不改变状态
                continue;
            }

            awaitCountLine = false; // 显示行开始后，段内不会再有数量行

            // 缩进 ≥2 的显示行：只记录跟踪的三种语言（简中 / 繁中 / 默认段英文）
            if (current is not null && line.Contains('"'))
            {
                var language = currentLang ?? DefaultLanguage;
                if (IsTrackedLanguage(language))
                    current.AddLine(language, i);
            }
        }

        foreach (var stat in _stats)
            _statByKey.TryAdd(stat.Key, stat); // 同键取首条（与既有 FirstOrDefault 语义一致）
    }

    private static bool IsTrackedLanguage(string language)
        => LanguagePriority.Any(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase));

    private static (Encoding Encoding, byte[] Bom) DetectEncoding(byte[] bytes)
    {
        var (encoding, bomLength) = TextEncodingDetector.Detect(bytes);
        return (encoding, bomLength switch
        {
            3 => [0xEF, 0xBB, 0xBF],
            2 => encoding.CodePage == Encoding.BigEndianUnicode.CodePage ? [0xFE, 0xFF] : [0xFF, 0xFE],
            _ => [],
        });
    }
}

    /// <summary>csd 内的一个 stat 条目。</summary>
    public sealed class CsdStat
    {
        public CsdStat(string key) => Key = key;

        /// <summary>stat 块头的 id 串（空格连接），文件内唯一，作为上色指派的键。</summary>
        public string Key { get; }

        /// <summary>语言名 → 该语言段的显示行号（默认段记为 <see cref="CsdDocument.DefaultLanguage"/>）。
        /// 只登记被跟踪的三种语言：简中 / 繁中 / 英文。</summary>
        internal Dictionary<string, List<int>> Lines { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>语言名 → 该语言段「数量行」的行号。数量行（段头单独一行的数字）= 段内显示行数，
        /// 游戏按它读取显示行；<b>拆行插入新显示行后必须同步递增</b>，否则游戏读行错位、整文件拒载
        /// （报 unexpected marker）。</summary>
        internal Dictionary<string, int> CountLines { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal void SetCountLine(string language, int lineIndex) => CountLines[language] = lineIndex;

        internal void AddLine(string language, int lineIndex)
        {
            if (!Lines.TryGetValue(language, out var lines))
                Lines[language] = lines = [];
            lines.Add(lineIndex);
        }

    /// <summary>该语言的显示行号（没有该语言段则空）。</summary>
    public IReadOnlyList<int> LinesOf(string language)
        => Lines.TryGetValue(language, out var lines) ? lines : [];

    /// <summary>按优先级返回第一段有内容的行号：<paramref name="preferredLanguage"/>（客户端语言）优先，
    /// 该语言段不存在时退回 <see cref="CsdDocument.LanguagePriority"/> 的顺序；三种都没有则空。</summary>
    public IReadOnlyList<int> ResolveLines(string? preferredLanguage = null)
    {
        if (preferredLanguage is not null)
        {
            var preferred = LinesOf(preferredLanguage);
            if (preferred.Count > 0)
                return preferred;
        }
        foreach (var language in CsdDocument.LanguagePriority)
        {
            var lines = LinesOf(language);
            if (lines.Count > 0)
                return lines;
        }
        return [];
    }

    /// <summary>返回显示文本实际来自哪种语言；<paramref name="preferredLanguage"/> 优先。</summary>
    public string ResolveLanguage(string? preferredLanguage = null)
    {
        if (preferredLanguage is not null && LinesOf(preferredLanguage).Count > 0)
            return preferredLanguage;
        foreach (var language in CsdDocument.LanguagePriority)
        {
            if (LinesOf(language).Count > 0)
                return language;
        }
        return CsdDocument.DefaultLanguage;
    }

    /// <summary>该 stat 实际带有文本的语言（按优先级，最多三种）。</summary>
    public IReadOnlyList<string> Languages()
        => [.. CsdDocument.LanguagePriority.Where(l => LinesOf(l).Count > 0)];

    /// <summary>兼容旧名：简体中文段的显示行号（为空 = 该 stat 没有简中文本）。</summary>
    public IReadOnlyList<int> TargetLanguageLineIndexes => LinesOf(CsdDocument.TargetLanguage);

    /// <summary>兼容旧名：默认段（英文）的显示行号。</summary>
    public IReadOnlyList<int> DefaultLanguageLineIndexes => LinesOf(CsdDocument.DefaultLanguage);
}

/// <summary>预览文本的一个着色分段。</summary>
public sealed record CsdTextSegment(string Text, string? ColorId);

/// <summary>csd 显示文本内的行内标记解析：颜色标签与 [Tag|显示文本]。</summary>
public static class CsdMarkup
{
    // 游戏只认「无闭合」颜色标签 <X>{{…}}（带 </X> 会被当普通文本打印并导致颜色失效）；
    // 标签内容常是数值占位符（如 {{{0:+d}}} 三个连排花括号），懒惰匹配会吞掉右括号，
    // 因此内容按「非花括号字符或完整花括号组」匹配；带闭合（历史产物/外部补丁）同样认
    private static readonly Regex ColorTagRegex = new(
        "<([A-Za-z][A-Za-z0-9]{0,15})>\\{\\{((?:[^{}]|\\{[^{}]*\\})*)\\}\\}(?:</\\1>)?", RegexOptions.Compiled);
    private static readonly Regex BracketTagRegex = new("\\[[^\\]|]*\\|([^\\]]*)\\]", RegexOptions.Compiled);

    /// <summary>把带标记的显示文本拆成着色分段；[Tag|x] 就地解析为 x。</summary>
    public static IReadOnlyList<CsdTextSegment> Parse(string text)
    {
        var segments = new List<CsdTextSegment>();
        var pos = 0;
        foreach (Match m in ColorTagRegex.Matches(text))
        {
            if (m.Index > pos)
                segments.Add(new CsdTextSegment(ResolveBrackets(text[pos..m.Index]), null));
            segments.Add(new CsdTextSegment(ResolveBrackets(m.Groups[2].Value), m.Groups[1].Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            segments.Add(new CsdTextSegment(ResolveBrackets(text[pos..]), null));
        return segments;
    }

    private static string ResolveBrackets(string text) => BracketTagRegex.Replace(text, "$1");

    /// <summary>模拟「只染数值」的预览分段：把文本里第一个数值占位符拆出来标 <paramref name="colorId"/>，
    /// 其余部分（词缀名等）无色——与 <see cref="CsdDocument.TrySplitByTier"/> / TryApplyColorRamp 写入游戏后的实际效果一致。</summary>
    public static IReadOnlyList<CsdTextSegment> ParseValueOnly(string text, string? colorId)
    {
        if (colorId is null)
            return [new CsdTextSegment(text, null)];
        var match = CsdDocument.ValuePlaceholderRegex.Match(text);
        if (!match.Success)
            return [new CsdTextSegment(text, null)];
        var segments = new List<CsdTextSegment>(3);
        if (match.Index > 0)
            segments.Add(new CsdTextSegment(text[..match.Index], null));
        segments.Add(new CsdTextSegment(match.Value, colorId));
        if (match.Index + match.Length < text.Length)
            segments.Add(new CsdTextSegment(text[(match.Index + match.Length)..], null));
        return segments;
    }

    /// <summary>显示文本里只有占位符、没有任何字母或汉字 = 内容由游戏运行时组合填充，
    /// 离线预览无法还原（如 <c>{0} {1}</c> 这种"前缀词 + 后缀词"的组合型词缀）。</summary>
    public static bool IsPlaceholderOnly(string text)
        => text.Length > 0 && text.Contains('{') && !text.Any(char.IsLetter);
}
