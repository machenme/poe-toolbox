using System.IO;
using System.Security.Cryptography;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.AffixWorkbench;

/// <summary>词缀数据源定义：分组名（中文）、显示名（中文）、游戏内路径。
/// <see cref="IsDirectory"/> 为 true 时 <see cref="GamePath"/> 是目录前缀，该目录下所有 csd 合并为一个数据源。</summary>
public sealed record AffixSourceDef(string Group, string DisplayName, string GamePath, bool IsDirectory = false);

/// <summary>
/// 词缀上色数据层：连接客户端读取全部词缀描述文件（data/statdescriptions 下的 csd）与 uisettings.xml，
/// 解析为可浏览条目并按分组归类；按方案计算修改后字节；应用前校验文件与连接时一致（SPEC 控制流）。
/// 基线随应用成功推进（上一次写入结果成为新基线），因此方案可反复修改、重复应用；
/// 外部改动会导致校验失败并要求重新连接。
/// </summary>
public sealed class AffixDataService : IDisposable
{
    public const string UiSettingsPath = "metadata/ui/uisettings.xml";

    /// <summary>词缀描述文件根目录（客户端内）。</summary>
    public const string DescriptionsRoot = "data/statdescriptions/";

    /// <summary>技能专属词缀描述目录（562 个按技能拆分的 csd）。</summary>
    public const string SpecificSkillDirectory = "data/statdescriptions/specific_skill_stat_descriptions/";

    /// <summary>语言覆盖层目录前缀（<c>data/balance/&lt;语言&gt;/xxx.datc64</c>），用来判定客户端语言。</summary>
    public const string BalanceRoot = "data/balance/";

    /// <summary>装备词缀按 tier 分档上色时只染最高的这几档（T1~T5），更低的档没有价值、一律不上色。</summary>
    public const int MaxColoredTiers = 8;

    /// <summary>词缀数据源目录：分组顺序即界面展示顺序。
    /// 不在目录内的 csd（游戏更新新增）会自动归入「其他 · 未分类」，不会漏掉。</summary>
    public static readonly IReadOnlyList<AffixSourceDef> Catalog =
    [
        // ── 装备词缀 ──
        new("装备词缀", "装备词缀（主库）", "data/statdescriptions/stat_descriptions.csd"),
        new("装备词缀", "隐匿词缀", "data/statdescriptions/advanced_mod_stat_descriptions.csd"),

        // ── 地图与图鉴 ──
        new("地图与图鉴", "地图词缀", "data/statdescriptions/map_stat_descriptions.csd"),
        new("地图与图鉴", "终局地图", "data/statdescriptions/endgame_map_stat_descriptions.csd"),
        new("地图与图鉴", "异界图鉴", "data/statdescriptions/atlas_stat_descriptions.csd"),
        new("地图与图鉴", "异界图鉴（变体）", "data/statdescriptions/atlas_variant_stat_descriptions.csd"),
        new("地图与图鉴", "石板", "data/statdescriptions/tablet_stat_descriptions.csd"),
        new("地图与图鉴", "原初祭坛", "data/statdescriptions/primordial_altar_stat_descriptions.csd"),
        new("地图与图鉴", "地图房间", "data/statdescriptions/map_temple_room_stat_descriptions.csd"),

        // ── 掉落容器与遗物 ──
        new("掉落容器与遗物", "宝箱", "data/statdescriptions/chest_stat_descriptions.csd"),
        new("掉落容器与遗物", "远征遗物", "data/statdescriptions/expedition_relic_stat_descriptions.csd"),
        new("掉落容器与遗物", "远征遗物（特殊）", "data/statdescriptions/expedition_relic_special_stat_descriptions.csd"),
        new("掉落容器与遗物", "圣所遗物", "data/statdescriptions/sanctum_relic_stat_descriptions.csd"),
        new("掉落容器与遗物", "夺宝装备", "data/statdescriptions/heist_equipment_stat_descriptions.csd"),
        new("掉落容器与遗物", "赛季石", "data/statdescriptions/leaguestone_stat_descriptions.csd"),
        new("掉落容器与遗物", "哨卫", "data/statdescriptions/sentinel_stat_descriptions.csd"),

        // ── 技能与天赋 ──
        new("技能与天赋", "技能", "data/statdescriptions/skill_stat_descriptions.csd"),
        new("技能与天赋", "技能石", "data/statdescriptions/gem_stat_descriptions.csd"),
        new("技能与天赋", "主动技能石", "data/statdescriptions/active_skill_gem_stat_descriptions.csd"),
        new("技能与天赋", "元技能石", "data/statdescriptions/meta_gem_stat_descriptions.csd"),
        new("技能与天赋", "天赋", "data/statdescriptions/passive_skill_stat_descriptions.csd"),
        new("技能与天赋", "天赋光环", "data/statdescriptions/passive_skill_aura_stat_descriptions.csd"),
        new("技能与天赋", "天赋（变体）", "data/statdescriptions/passive_skill_variant_stat_descriptions.csd"),
        new("技能与天赋", "技能专属描述", SpecificSkillDirectory, IsDirectory: true),

        // ── 其他 ──
        new("其他", "怪物", "data/statdescriptions/monster_stat_descriptions.csd"),
        new("其他", "药剂增益", "data/statdescriptions/utility_flask_buff_stat_descriptions.csd"),
        new("其他", "角色面板说明", "data/statdescriptions/character_panel_stat_descriptions.csd"),
        new("其他", "手柄面板说明", "data/statdescriptions/character_panel_gamepad_stat_descriptions.csd"),
    ];

    /// <summary>数据源统计。<see cref="GamePath"/> 是目录型数据源的前缀键，也是条目的 <c>SourceKey</c>。</summary>
    public sealed record SourceInfo(string Group, string DisplayName, string GamePath, int FileCount, int StatCount, long SizeBytes);

    /// <summary>一条可浏览/可上色的词缀。<paramref name="SourceKey"/> 指向所属数据源（可能是目录），
    /// <paramref name="GamePath"/> 是实际文件；<paramref name="Language"/> 是显示文本实际来自的语言；
    /// <paramref name="Lines"/> 是按显示行拆开的纯文本（一条 stat 常有多行，如"提高/降低"两个方向，
    /// 游戏按数值只显示其中一行，所以列表要逐行呈现）。
    /// <paramref name="HasColorTag"/> = 该条词缀的显示行里真的带有颜色标签（可能是第三方补丁打的）：
    /// 列表预览因此按「分段」渲染，外来颜色才看得见（纯文本列表会把标签信息丢掉）。</summary>
    public sealed record AffixEntry(
        string StatKey,
        string Group,
        string SourceKey,
        string GamePath,
        string DisplayText,
        string Language,
        IReadOnlyList<string> Lines,
        bool HasColorTag = false);

    private readonly List<(string Path, CsdDocument Doc, byte[] Sha)> _csd = [];
    private UISettingsDoc? _uiDoc;
    private byte[] _uiSha = [];
    private GameDataAccess? _gd;
    private bool _stale = true;
    private ModTierIndex? _tierIndex;
    private bool _tierIndexTried;
    private Dictionary<string, CsdDocument>? _previewDocs;

    /// <summary>tier 阶梯索引：首次需要分档上色时才从 <c>mods.datc64</c> 构建（秒级），之后复用。</summary>
    private ModTierIndex? TierIndex()
    {
        if (_tierIndexTried)
            return _tierIndex;
        _tierIndexTried = true;
        if (_gd is null)
            return null;
        try
        {
            _tierIndex = ModTierIndex.Build(_gd);
        }
        catch
        {
            _tierIndex = null; // 客户端缺表或 schema 不匹配时静默降级（分档不可用，其他功能不受影响）
        }
        return _tierIndex;
    }

    public bool IsConnected => _gd is not null && !_stale;
    public IReadOnlyList<SourceInfo> Sources { get; private set; } = [];
    public IReadOnlyList<AffixEntry> Entries { get; private set; } = [];

    /// <summary>连接时判定的客户端语言（简体中文 / 繁体中文 / 英文）：
    /// 显示文本与上色都用这一种语言段（该段缺失时按 简中 &gt; 繁中 &gt; 英文 退回）。</summary>
    public string ClientLanguage { get; private set; } = CsdDocument.TargetLanguage;

    /// <summary>连接完成（成功或失败）后在 UI 线程回调。</summary>
    public event Action? ConnectFinished;

    /// <summary>游戏界面设置文件里已有、但不属于当前方案的颜色定义（id → 颜色）。
    /// 第三方配色补丁的标签靠它才能在列表里显示出颜色；只读，不参与色阶合成，也不会被方案色覆盖。</summary>
    public IReadOnlyList<AffixColorDef> ExternalColors { get; private set; } = [];

    /// <summary>游戏里<strong>正在被词缀引用</strong>的外来颜色（第三方补丁带来的 / 游戏自带的）：
    /// <c>uisettings.xml</c> 里有定义，且某个词缀描述文件里真的写了这个标签。
    /// 原版 uisettings 有 300 多个颜色定义，全列到界面上没法看——只列被引用的那批。</summary>
    public IReadOnlyList<AffixColorDef> ReferencedExternalColors { get; private set; } = [];

    /// <summary>解析后的文档（供检查器预览），按 GamePath 索引。</summary>
    public CsdDocument? TryGetDoc(string gamePath) => _csd.FirstOrDefault(s => s.Path == gamePath).Doc;

    public async Task ConnectAsync(string? gameDataPath)
    {
        var path = (gameDataPath ?? GameDataPathPreference.Get())?.Trim();
        await Task.Run(() => ConnectCore(path));
        ConnectFinished?.Invoke();
    }

    /// <summary>本次连接过程中的附加说明（如自动修复），连接结束后由界面展示。</summary>
    public IReadOnlyList<string> ConnectNotes => _connectNotes;
    private readonly List<string> _connectNotes = [];

    private void ConnectCore(string? path)
    {
        _stale = true;
        _gd?.Dispose();
        _gd = null;
        _connectNotes.Clear();

        var resolved = GameDataLoader.ResolvePath(path);
        try
        {
            ConnectGameData(resolved);
        }
        catch (Exception ex) when (IsMissingPatchBundle(ex) || ex is DirectoryNotFoundException)
        {
            // 索引引用的 PATCHED bundle 文件丢失：文件级丢的是 FileNotFoundException；
            // 整个 PATCHED 目录被删时抛 DirectoryNotFoundException（找不到路径的一部分）。
            // 两种都先尝试从基线备份自动修复，修不动再把原异常包成可操作的中文错误抛出去。
            FileLogger.App.Warn($"读取游戏数据失败（补丁 bundle 文件丢失）：{ex.Message}");
            var repaired = PatchBundleRepair.RepairIfBroken(resolved, msg => _connectNotes.Add(msg));
            if (repaired <= 0)
                throw MissingPatchBundleUnrecoverable(ex);
            try
            {
                ConnectGameData(resolved);
            }
            catch (Exception retry) when (IsMissingPatchBundle(retry) || retry is DirectoryNotFoundException)
            {
                throw MissingPatchBundleUnrecoverable(retry);
            }
        }
    }

    /// <summary>修复也无法恢复时的报错：说清成因（恢复默认游戏数据/客户端校验删了补丁文件）
    /// 与出路（特效补丁页「恢复游戏原版」，或启动器验证游戏文件），不再把英文 IO 异常原样甩给界面。</summary>
    private static InvalidOperationException MissingPatchBundleUnrecoverable(Exception cause) => new(
        "游戏数据里仍有文件指向已丢失的补丁文件，自动修复未能恢复。" +
        "常见原因：补丁文件被「恢复默认游戏数据」「恢复游戏原版」或客户端文件校验删除，而索引仍指向它们。"
        + cause.Message,
        cause);

    /// <summary>异常是否指向丢失的 PATCHED bundle 文件（读取时才打开 bundle，索引本身能正常加载）。</summary>
    internal static bool IsMissingPatchBundle(Exception ex)
        => ex is FileNotFoundException { FileName: { } missing }
           && missing.EndsWith(".bundle.bin", StringComparison.OrdinalIgnoreCase)
           && Path.GetFileName(Path.GetDirectoryName(missing))?.Equals("PATCHED", StringComparison.OrdinalIgnoreCase) == true;

    private void ConnectGameData(string resolved)
    {
        var gd = GameDataAccess.OpenReadOnlyMapped(resolved);
        try
        {
            var allCsd = new List<string>();
            var balanceDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in gd.Index.Files.Values)
            {
                var filePath = file.Path;
                if (filePath is null)
                    continue;
                if (filePath.Length > BalanceRoot.Length
                    && filePath.StartsWith(BalanceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = filePath[BalanceRoot.Length..];
                    var slash = rest.IndexOf('/');
                    if (slash > 0)
                        balanceDirs.Add(rest[..slash]);
                }
                if (filePath.Length > DescriptionsRoot.Length
                    && filePath.StartsWith(DescriptionsRoot, StringComparison.OrdinalIgnoreCase)
                    && filePath.EndsWith(".csd", StringComparison.OrdinalIgnoreCase))
                    allCsd.Add(filePath);
            }
            allCsd.Sort(StringComparer.Ordinal);

            var clientLanguage = DetectClientLanguage(balanceDirs);

            var sources = new List<SourceInfo>();
            var entries = new List<AffixEntry>();
            var csd = new List<(string, CsdDocument, byte[])>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencedColorIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var def in Catalog)
            {
                var paths = def.IsDirectory
                    ? allCsd.Where(p => p.StartsWith(def.GamePath, StringComparison.OrdinalIgnoreCase)).ToList()
                    : allCsd.Where(p => p.Equals(def.GamePath, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var p in paths)
                    claimed.Add(p);
                LoadSource(gd, def.Group, def.DisplayName, def.GamePath, paths, clientLanguage, sources, entries, csd, referencedColorIds);
            }

            // 目录未覆盖的新文件（游戏更新新增）：归入「其他 · 未分类」，保证不漏
            var extras = allCsd.Where(p => !claimed.Contains(p)).ToList();
            foreach (var p in extras)
            {
                var name = Path.GetFileNameWithoutExtension(p);
                LoadSource(gd, "其他", name, p, [p], clientLanguage, sources, entries, csd, referencedColorIds);
            }

            var uiBytes = gd.ReadFile(UiSettingsPath)
                ?? throw new FileNotFoundException($"客户端缺少界面设置文件：{UiSettingsPath}");
            var uiDoc = UISettingsDoc.Parse(uiBytes);

            _csd.Clear();
            _csd.AddRange(csd);
            _uiDoc = uiDoc;
            _uiSha = SHA256.HashData(uiBytes);
            ExternalColors = uiDoc.GetColorDefs();
            ReferencedExternalColors = [.. ExternalColors.Where(c => referencedColorIds.Contains(c.Id))];
            Sources = sources;
            Entries = entries;
            _gd = gd;
            _stale = false;
            ClientLanguage = clientLanguage;
        }
        finally
        {
            if (!ReferenceEquals(_gd, gd))
                gd.Dispose();
        }
    }

    /// <summary>按客户端资源判定语言：有 <c>data/balance/simplified chinese/</c> 覆盖层 = 简体中文客户端；
    /// 否则有 <c>traditional chinese</c> = 繁体中文客户端；两者都没有 = 英文（默认段）。</summary>
    private static string DetectClientLanguage(IReadOnlyCollection<string> balanceDirs)
    {
        if (balanceDirs.Contains("simplified chinese"))
            return CsdDocument.TargetLanguage;
        if (balanceDirs.Contains("traditional chinese"))
            return "Traditional Chinese";
        return CsdDocument.DefaultLanguage;
    }

    private static void LoadSource(
        GameDataAccess gd,
        string group,
        string displayName,
        string sourceKey,
        IReadOnlyList<string> paths,
        string clientLanguage,
        List<SourceInfo> sources,
        List<AffixEntry> entries,
        List<(string, CsdDocument, byte[])> csd,
        HashSet<string> referencedColorIds)
    {
        if (paths.Count == 0)
        {
            sources.Add(new SourceInfo(group, displayName, sourceKey, 0, 0, 0));
            return;
        }

        long size = 0;
        var statCount = 0;
        foreach (var path in paths)
        {
            var bytes = gd.ReadFile(path)
                ?? throw new FileNotFoundException($"客户端缺少词缀描述文件：{path}");
            var doc = CsdDocument.Parse(bytes);
            csd.Add((path, doc, SHA256.HashData(bytes)));
            size += bytes.LongLength;
            // 整个文件扫一次攒下「游戏里实际在用哪些颜色」——原版 uisettings 有 300 多个定义，全列出来没法看
            foreach (var id in doc.CollectColorIds())
                referencedColorIds.Add(id);
            foreach (var stat in doc.Stats)
            {
                var lines = doc.GetDisplayLines(stat.Key, clientLanguage);
                if (lines.Count == 0)
                    continue; // 客户端语言与回退链上的三种语言都没有显示文本
                statCount++;
                // 按条判定该词缀的显示行是否真的带颜色标签（文件级判断会让同文件的
                // 未着色条目一起被当成「有颜色」，列表排序就失效了）
                var hasColorTag = doc.StatHasColorTag(stat.Key, clientLanguage);
                entries.Add(new AffixEntry(
                    stat.Key, group, sourceKey, path,
                    string.Concat(lines), stat.ResolveLanguage(clientLanguage), lines, hasColorTag));
            }
        }
        sources.Add(new SourceInfo(group, displayName, sourceKey, paths.Count, statCount, size));
    }

    /// <summary>词缀的生效颜色：手动指派优先（单个颜色 id，或色阶前缀——预览用色阶首档色），
    /// 其次按顺序第一条命中的启用规则。行级指派（LineText 非空）单独返回，
    /// 列表预览只给对应的那几行上色（同一条 stat 的多行变体可分开各上各色）。</summary>
    /// <returns>ColorId = 整条生效色；LineColors = 行纯文本 → 行级指派色。</returns>
    public static (string? ColorId, IReadOnlyDictionary<string, string> LineColors) EffectiveMatch(
        AffixColorScheme scheme, AffixEntry entry)
    {
        var assignment = scheme.Assignments.FirstOrDefault(
            a => a.StatKey == entry.StatKey && a.FilePath == entry.GamePath && a.LineText.Length == 0);
        string? colorId = null;
        if (assignment is not null)
        {
            if (scheme.FindColor(assignment.ColorId) is { } byAssign)
                colorId = byAssign.Id;
            // 色阶指派（"Tier" 或 "Tier|负向"）：游戏内按档逐行染数值，列表预览用色阶首档色标示
            else
            {
                var primary = assignment.ColorId.Split('|', 2)[0];
                if (scheme.FindRamp(primary) is not null)
                    colorId = primary;
            }
        }
        if (colorId is null)
        {
            foreach (var rule in scheme.Rules)
            {
                if (!rule.Enabled || rule.Pattern.Length == 0)
                    continue;
                if (entry.DisplayText.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase)
                    && scheme.FindColor(rule.ColorId) is { } byRule)
                {
                    colorId = byRule.Id;
                    break;
                }
            }
        }

        var lineColors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in scheme.Assignments)
        {
            if (line.StatKey != entry.StatKey || line.FilePath != entry.GamePath || line.LineText.Length == 0)
                continue;
            if (scheme.FindColor(line.ColorId) is { } def)
                lineColors[line.LineText] = def.Id; // 同一行多条指派时后写覆盖，与整条指派的 RemoveAll+Add 行为一致
        }
        return (colorId, lineColors);
    }

    /// <summary>还原目标 = 连接时读到的文件，只去掉本方案的颜色标签。
    /// 第三方补丁打上的标签不在 <paramref name="colorIds"/> 里，因此原样保留——
    /// 卸载本工具的补丁只撤自己那一层，不会把别人的整套配色一起抹掉。
    /// （要回到无补丁的干净状态，用「恢复原版」走索引基线，不走这里的还原。）</summary>
    private static byte[] StripSchemeTags(CsdDocument doc, IReadOnlyList<string> colorIds, byte[] current)
    {
        if (!doc.ContainsAnyColorTag(colorIds))
            return current; // 客户端里没有本方案的痕迹：还原目标就是连接时的字节
        var copy = CsdDocument.Parse(current);
        copy.StripColors(colorIds);
        return copy.Serialize();
    }

    /// <summary>按 SPEC 控制流计算全部文件变更；内容无变化的文件不进补丁。</summary>
    public IReadOnlyList<AffixPatchBuilder.FileChange> ComputeChanges(AffixColorScheme scheme, bool forExport = false)
    {
        ObjectDisposedException.ThrowIf(_gd is null, this);
        var colorIds = scheme.ColorIds;
        var changes = new List<AffixPatchBuilder.FileChange>();
        // 有指派色阶时才需要 tier 阶梯（首次构建约 1 秒），没有就完全不碰 mods 表
        var tierIndex = scheme.Ramps().Count > 0 ? TierIndex() : null;

        foreach (var (path, doc, _) in _csd)
        {
            var modified = RebuildCsd(doc, scheme, colorIds, path, ClientLanguage, tierIndex);
            if (modified is null)
                continue; // 既无本方案标签、也无词缀命中：整文件不动
            var current = doc.Serialize(); // round-trip 保证与连接时读到的字节一致
            if (current.AsSpan().SequenceEqual(modified))
                continue; // 客户端已经是目标状态
            var original = StripSchemeTags(doc, colorIds, current);
            changes.Add(new AffixPatchBuilder.FileChange(path, original, modified));
        }

        if (_uiDoc is not null)
        {
            var ui = UISettingsDoc.Parse(_uiDoc.Serialize());
            ui.SetColors(scheme.Colors.Select(c => (c.Id, c.R, c.G, c.B, c.A)));
            var modifiedUi = ui.Serialize();
            var currentUi = _uiDoc.Serialize();
            // forExport：无条件带上颜色定义——分发对象客户端上很可能还没有这些颜色
            if (forExport || !currentUi.AsSpan().SequenceEqual(modifiedUi))
            {
                // 同上：只删本方案的颜色定义，第三方补丁的 AT1/DA1 之类原样保留
                var stripped = UISettingsDoc.Parse(currentUi);
                var originalUi = stripped.StripColors(colorIds) > 0 ? stripped.Serialize() : currentUi;
                changes.Add(new AffixPatchBuilder.FileChange(UiSettingsPath, originalUi, modifiedUi));
            }
        }

        if (changes.Count == 0)
            throw new InvalidOperationException("当前方案没有产生任何文件变更（没有词缀被上色）。");
        return changes;
    }

    /// <returns>null = 该文件无需重写（不含本方案标签且没有任何词缀命中）。</returns>
    private static byte[]? RebuildCsd(
        CsdDocument doc,
        AffixColorScheme scheme,
        IReadOnlyList<string> colorIds,
        string gamePath,
        string clientLanguage,
        ModTierIndex? tierIndex)
    {
        // byAssignment = 用户手动指派（而不是关键词规则命中）：只有它才接管第三方已上色的行。
        // 行级指派单独收集：只染显示文本与之相同的那一行（同一条 stat 的多行变体可分开各上各色）。
        var hits = new List<(string Key, string ColorId, bool ByAssignment)>();
        var lineHits = new List<(string Key, string LineText, string ColorId)>();
        foreach (var stat in doc.Stats)
        {
            var plain = doc.GetDisplayText(stat.Key, clientLanguage);
            if (plain.Length == 0)
                continue;
            foreach (var a in scheme.Assignments)
            {
                if (a.StatKey != stat.Key || a.FilePath != gamePath || a.LineText.Length == 0)
                    continue;
                if (scheme.FindColor(a.ColorId) is { } lineDef)
                    lineHits.Add((stat.Key, a.LineText, lineDef.Id));
            }
            var match = MatchColor(scheme, stat.Key, gamePath, plain);
            if (match.ColorId is not null)
                hits.Add((stat.Key, match.ColorId, match.ByAssignment));
        }

        if (hits.Count == 0 && lineHits.Count == 0 && !doc.ContainsAnyColorTag(colorIds))
            return null;

        // 在文档副本上重算：Parse 出的 doc 不直接改，避免污染浏览基线
        var copy = CsdDocument.Parse(doc.Serialize());
        copy.StripColors(colorIds);
        // 本方案之外的标签 = 第三方补丁打的。显式指派要"改掉"这些行时先把对方的标签清掉再打自己的，
        // 否则「已有标签不动」的分层共栖规则会让指派永远打不上去。规则命中不算——批量规则会误伤大片别人的配色。
        var foreignIds = copy.CollectColorIds().Where(id => !colorIds.Contains(id)).ToList();
        // ① 行级指派先行：只动目标行。之后整条上色会跳过已带标签的行，行级颜色得以保留
        foreach (var (key, lineText, color) in lineHits)
        {
            if (foreignIds.Count > 0)
                copy.StripColorsOfLine(key, lineText, foreignIds, clientLanguage);
            copy.TryApplyColorToLine(key, color, lineText, clientLanguage);
        }
        // ② 整条指派 / 规则命中（原逻辑）
        foreach (var (key, color, byAssignment) in hits)
        {
            if (byAssignment && foreignIds.Count > 0)
                copy.StripColorsOf(key, foreignIds, clientLanguage);
            // 指派色阶（如 AT）时：有 tier 阶梯的词缀按阶梯拆行、只染最高的 5 档（T1~T5）；
            // 没有阶梯的退回"按已有数值区间分档 + 正负拆分"。指派写作 "正向色阶|负向色阶" 时降低行用负向色阶。
            var parts = color.Split('|', 2);
            if (scheme.FindRamp(parts[0]) is { } ramp)
            {
                var negative = parts.Length > 1 ? scheme.FindRamp(parts[1]) : null;
                var tiers = tierIndex?.RangesOf(key) ?? [];
                // 显示行已被外部补丁拆过区间（如 efarm 的 1|# / #|-1 方向行）时 tier 拆行会放弃——
                // 此时必须退回按区间染色，否则该词缀一个标签都打不上（表现为"是 T1 却不变色"）
                if (tiers.Count < 2 || !copy.TrySplitByTier(key, tiers, ramp.ColorIds, Math.Min(ramp.ColorIds.Count, MaxColoredTiers), clientLanguage))
                    copy.TryApplyColorRamp(key, ramp.ColorIds, negative?.ColorIds, clientLanguage);
            }
            else
            {
                copy.TryApplyColor(key, color, clientLanguage);
            }
        }
        return copy.Serialize();
    }

    private static (string? ColorId, bool ByAssignment) MatchColor(
        AffixColorScheme scheme, string statKey, string gamePath, string plainText)
    {
        // 只匹配整条指派；行级指派（LineText 非空）由 RebuildCsd 的 lineHits 单独处理
        var assignment = scheme.Assignments.FirstOrDefault(
            a => a.StatKey == statKey
                 && a.LineText.Length == 0
                 && (a.FilePath == gamePath || a.FilePath.Length == 0));
        if (assignment is not null)
        {
            if (scheme.FindColor(assignment.ColorId) is { } byAssign)
                return (byAssign.Id, true);
            // 色阶指派（"Tier" 或 "Tier|负向"）：原样返回整串，由 RebuildCsd 拆分并展开成分档染色
            var primary = assignment.ColorId.Split('|', 2)[0];
            if (scheme.FindRamp(primary) is not null)
                return (assignment.ColorId, true);
        }
        foreach (var rule in scheme.Rules)
        {
            if (!rule.Enabled || rule.Pattern.Length == 0)
                continue;
            if (plainText.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase)
                && scheme.FindColor(rule.ColorId) is { } byRule)
                return (byRule.Id, false);
        }
        return (null, false);
    }

    // ═══ 应用前预览 ═════════════════════════════════════════════

    /// <summary>当前是否处于预览模式（列表展示应用后的效果）。</summary>
    public bool PreviewActive => _previewDocs is not null;

    /// <summary>预览版文档（按 GamePath 索引）；预览未生成或该文件无变化时返回 null。</summary>
    public CsdDocument? TryGetPreviewDoc(string gamePath)
        => _previewDocs is not null && _previewDocs.TryGetValue(gamePath, out var doc) ? doc : null;

    /// <summary>退出预览模式。</summary>
    public void ClearPreview() => _previewDocs = null;

    /// <summary>按当前方案在内存里模拟一次「应用词缀修改」，把会变化的文件生成为预览文档。
    /// 之后 <see cref="TryGetPreviewDoc"/> 即可取到应用后的文档（含按档拆行与颜色标签）。
    /// 返回会变化的文件数；不写任何盘上数据。需要处于已连接状态。</summary>
    public int ComputePreview(AffixColorScheme scheme)
    {
        ObjectDisposedException.ThrowIf(_gd is null, this);
        var colorIds = scheme.ColorIds;
        var tierIndex = scheme.Ramps().Count > 0 ? TierIndex() : null;
        var docs = new Dictionary<string, CsdDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, doc, _) in _csd)
        {
            var modified = RebuildCsd(doc, scheme, colorIds, path, ClientLanguage, tierIndex);
            if (modified is not null)
                docs[path] = CsdDocument.Parse(modified);
        }
        _previewDocs = docs;
        return docs.Count;
    }

    /// <summary>应用前一致性校验：逐文件重读并与基线 SHA 对比。返回不一致的文件清单（空 = 通过）。</summary>
    public IReadOnlyList<string> VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(_gd is null, this);
        var mismatched = new List<string>();
        foreach (var (path, _, sha) in _csd)
        {
            var live = _gd.ReadFile(path);
            if (live is null || !SHA256.HashData(live).AsSpan().SequenceEqual(sha))
                mismatched.Add(path);
        }
        if (_uiDoc is not null)
        {
            var liveUi = _gd.ReadFile(UiSettingsPath);
            if (liveUi is null || !SHA256.HashData(liveUi).AsSpan().SequenceEqual(_uiSha))
                mismatched.Add(UiSettingsPath);
        }
        return mismatched;
    }

    /// <summary>应用成功后推进基线：写入的内容成为新基线，方案可继续修改并再次应用。</summary>
    public void AdvanceBaseline(IReadOnlyList<AffixPatchBuilder.FileChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.GamePath == UiSettingsPath)
            {
                _uiDoc = UISettingsDoc.Parse(change.Modified);
                _uiSha = SHA256.HashData(change.Modified);
                continue;
            }
            var index = _csd.FindIndex(s => s.Path == change.GamePath);
            if (index >= 0)
            {
                var (path, _, _) = _csd[index];
                _csd[index] = (path, CsdDocument.Parse(change.Modified), SHA256.HashData(change.Modified));
            }
        }
    }

    /// <summary>有 tier 阶梯的词缀名集合（"一键给所有装备词缀分档上色"用）。
    /// 首次调用会构建索引（约 1 秒），之后复用；客户端缺表时返回空集合。</summary>
    public IReadOnlyCollection<string> TieredStatKeys()
        => TierIndex()?.StatKeys ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>取某词缀的档位区间（按数值升序，第一个是最低档）；没有阶梯时返回空。</summary>
    public IReadOnlyList<(int Min, int Max, int Tier, int LineTiers)> TierRangesOf(string statName)
        => TierIndex()?.RangesOf(statName) ?? [];

    /// <summary>恢复原版后基线失效，需要重新连接。</summary>
    public void MarkStale()
    {
        _stale = true;
        Sources = [];
        Entries = [];
        _previewDocs = null;
        DropTierIndex();
    }

    public void ReleaseFileLocks()
    {
        _gd?.Dispose();
        _gd = null;
        _stale = true;
        _previewDocs = null;
        DropTierIndex();
    }

    private void DropTierIndex()
    {
        _tierIndex = null;
        _tierIndexTried = false;
    }

    public void Dispose() => ReleaseFileLocks();
}
