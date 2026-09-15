using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.AffixWorkbench;

/// <summary>
/// 「词缀上色」视图：浏览/搜索简中词缀 → 指派颜色（手动或规则）→ 生成补丁并应用/恢复。
/// 数据层是 <see cref="AffixDataService"/>；引擎执行走 Shared 的 FxEngineRunner（与特效补丁互斥）。
/// </summary>
public partial class AffixWorkbenchView : UserControl
{
    private sealed class EntryVm : INotifyPropertyChanged
    {
        private readonly HashSet<(string StatKey, string GamePath)> _checkedKeys;
        private readonly Action _onCheckedChanged;
        private bool _isChecked;

        public EntryVm(
            AffixDataService.AffixEntry entry,
            string? colorId,
            bool colorValueOnly,
            IReadOnlyList<IReadOnlyList<CsdTextSegment>>? previewOverride,
            string clientLanguage,
            HashSet<(string StatKey, string GamePath)> checkedKeys,
            Action onCheckedChanged)
        {
            Entry = entry;
            ColorId = colorId;

            IReadOnlyList<IReadOnlyList<CsdTextSegment>> lines;
            if (previewOverride is not null)
            {
                // 预览模式：行与分段来自模拟应用后的文档（含按档拆行与真实颜色标签）
                lines = previewOverride;
                PreviewColored = previewOverride.Any(line => line.Any(s => s.ColorId is not null));
            }
            else
            {
                // 少数词缀（如镜像珠宝变体）有多达数十行显示文本，全渲染会把行撑到几百像素高；
                // 列表只展示前几行，其余归入折叠提示
                var text = entry.Lines;
                if (text.Count > MaxPreviewLines)
                    text = [.. text.Take(MaxPreviewLines),
                        $"……其余 {text.Count - MaxPreviewLines} 行是同一词缀的其他变体/档位"];
                // 色阶（分档）指派与游戏内一致：只有数值变色、词缀名保持默认色；单色指派是整行变色
                lines = [.. text.Select(line => colorValueOnly
                    ? CsdMarkup.ParseValueOnly(line, colorId)
                    : (IReadOnlyList<CsdTextSegment>)[new CsdTextSegment(line, colorId)])];
                PreviewColored = colorId is not null;
            }

            if (lines.Count > MaxPreviewLines)
                lines = [.. lines.Take(MaxPreviewLines),
                    (IReadOnlyList<CsdTextSegment>)[new CsdTextSegment($"……其余 {lines.Count - MaxPreviewLines} 行是同一词缀的其他变体/档位", null)]];
            PreviewLines = lines;

            LanguageLabel = entry.Language == clientLanguage ? "" : ToDisplayName(entry.Language);
            PlaceholderNote = CsdMarkup.IsPlaceholderOnly(entry.DisplayText) ? "由前后缀组合，游戏内填充" : "";
            _checkedKeys = checkedKeys;
            _onCheckedChanged = onCheckedChanged;
            _isChecked = checkedKeys.Contains((entry.StatKey, entry.GamePath));
        }

        /// <summary>该词缀在当前方案/预览下会变色（预览模式下排在列表前面）。</summary>
        public bool PreviewColored { get; }

        /// <summary>列表预览最多显示的行数。</summary>
        private const int MaxPreviewLines = 4;

        public AffixDataService.AffixEntry Entry { get; }
        public string StatKey => Entry.StatKey;
        public string DisplayText => Entry.DisplayText;
        public string? ColorId { get; }

        /// <summary>按显示行拆开的预览（每行一个分段列表）：一条词缀常有多行文本
        /// （"提高/降低"两个方向、或不同档位），游戏按数值只显示其中一行。</summary>
        public IReadOnlyList<IReadOnlyList<CsdTextSegment>> PreviewLines { get; }

        /// <summary>显示文本不是客户端语言时标出实际语言（与客户端一致时不标，避免整列刷屏）。</summary>
        public string LanguageLabel { get; }

        /// <summary>组合型词缀的说明（文本只有占位符、内容由游戏运行时填充）；不是这类条目则为空。</summary>
        public string PlaceholderNote { get; }

        /// <summary>勾选状态（批量上色用）。真实状态存在视图的集合里，因此重建列表不会丢。</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                    return;
                _isChecked = value;
                if (value)
                    _checkedKeys.Add((Entry.StatKey, Entry.GamePath));
                else
                    _checkedKeys.Remove((Entry.StatKey, Entry.GamePath));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                _onCheckedChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly IEventBus _eventBus;
    private readonly AffixDataService _service = new();
    private AffixColorScheme _scheme = CreateDefaultScheme();
    private string? _gameDataPath;
    private PoeGameKind _gameKind;
    private List<EntryVm> _allEntries = [];
    private string? _sourceFilter;
    private string? _groupFilter;
    private readonly HashSet<(string StatKey, string GamePath)> _checked = [];
    private bool _bulkChecking;
    private bool _busy;

    public AffixWorkbenchView(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        InitializeComponent();
        _eventBus.Subscribe<GameContextChanged>(OnGameContextChanged);
        _service.ConnectFinished += () => Dispatcher.BeginInvoke(OnConnectFinished);
        WorkbenchPalette.Update(_scheme.Colors);
        RefreshSchemeCombo(initial: true);
        InitTierCountCombo();
        RefreshInspector();
    }

    /// <summary>档位数下拉：2~8 档；默认取当前 Tier 色阶的颜色数（没有则 4）。</summary>
    private bool _suppressTierComboEvents;

    private void InitTierCountCombo()
    {
        TierCountCombo.ItemsSource = Enumerable.Range(2, 7).ToList(); // 2~8
        var current = _scheme.FindRamp("Tier")?.ColorIds.Count ?? 4;
        // 只是把下拉的显示值同步成现状，不是用户操作：屏蔽事件，避免打开页面就重建色阶、覆盖已自定义的颜色。
        _suppressTierComboEvents = true;
        try
        {
            TierCountCombo.SelectedItem = Math.Clamp(current, 2, 8);
        }
        finally
        {
            _suppressTierComboEvents = false;
        }
    }

    /// <summary>用户改档位数：按比例从 T1 颜色渐变到最深色，重建 Tier1~TierN。</summary>
    private void TierCountCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressTierComboEvents || TierCountCombo.SelectedItem is not int count)
            return;
        RegenerateTierGradient(count);
    }

    /// <summary>按比例重建 Tier 色阶：以现有 Tier1 颜色为起点（没有就用默认荧光黄绿），
    /// 线性渐变到最深档，生成 Tier1~TierN；多余的 Tier 档（TierN+1…）移除。生成后仍可在「颜色修改」逐档自定义。</summary>
    private void RegenerateTierGradient(int count)
    {
        var anchor = _scheme.FindColor("Tier1");
        var (r0, g0, b0, a0) = anchor is null ? ((byte)200, (byte)255, (byte)70, (byte)255) : (anchor.R, anchor.G, anchor.B, anchor.A);
        const byte er = 35, eg = 105, eb = 45, ea = 185; // 最深档：暗绿

        // 移除旧的 Tier 档（Tier1~TierN 及遗留的多余档）
        for (var i = _scheme.Colors.Count - 1; i >= 0; i--)
        {
            if (_scheme.Colors[i].Id.Length > 4
                && _scheme.Colors[i].Id.StartsWith("Tier", StringComparison.Ordinal)
                && _scheme.Colors[i].Id[4..].All(char.IsDigit))
                _scheme.Colors.RemoveAt(i);
        }
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0 : (double)i / (count - 1);
            _scheme.Colors.Add(new AffixColorDef(
                $"Tier{i + 1}",
                (byte)Math.Round(r0 + (er - r0) * t),
                (byte)Math.Round(g0 + (eg - g0) * t),
                (byte)Math.Round(b0 + (eb - b0) * t),
                (byte)Math.Round(a0 + (ea - a0) * t)));
        }
        _scheme.Save();
        WorkbenchPalette.Update(_scheme.Colors);
        RefreshRampCombo();
        RebuildEntryList();
        RefreshInspector();
        Output.AppendLog($"等级色阶已重建为 {count} 档（按 T1 → 最深色比例渐变）；可在「颜色修改」逐档自定义颜色。");
    }

    /// <summary>语言名 → 界面显示名。</summary>
    private static string ToDisplayName(string language) => language switch
    {
        CsdDocument.TargetLanguage => "简体中文",
        "Traditional Chinese" => "繁体中文",
        CsdDocument.DefaultLanguage => "英文",
        _ => language,
    };

    /// <summary>由插件在切换页面/退出/锁请求时调用。</summary>
    public bool ReleaseFileLocks()    {
        var hadConnection = _service.IsConnected;
        _service.ReleaseFileLocks();
        if (hadConnection)
            Output.SetStatus("已释放游戏文件句柄；返回本页后请重新连接。", UiStatus.Kind.Neutral);
        return true;
    }

    private void OnGameContextChanged(GameContextChanged context)
    {
        if (string.IsNullOrWhiteSpace(context.GameDataPath))
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGameContextChanged(context));
            return;
        }
        _gameDataPath = context.GameDataPath;
        _gameKind = context.Game;
    }

    // ═══ 连接 ═══════════════════════════════════════════════════

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePoe2())
            return;
        await RunBusyAsync("打开游戏文件", async () =>
        {
            Output.SetStatus("连接中……（打开索引并解析词缀描述，首次约需数秒）", UiStatus.Kind.Neutral);
            await _service.ConnectAsync(_gameDataPath);
            return _service.IsConnected
                ? $"已连接：{_service.Sources.Count} 个数据源，{_service.Entries.Count} 条词缀（客户端语言：{ToDisplayName(_service.ClientLanguage)}）。"
                : "连接失败，详见任务日志。";
        });
    }

    private void OnConnectFinished()
    {
        if (_service.IsConnected)
        {
            _sourceFilter = null;
            _groupFilter = null;
            _checked.Clear(); // 换了客户端/重新连接：勾选重置
            SourceTree.ItemsSource = BuildSourceTree(_service.Sources);
            RebuildEntryList();
            RefreshCheckedSummary();
            Output.SetStatus($"已连接：{_service.Sources.Count} 个数据源，{_service.Entries.Count} 条词缀（客户端语言：{ToDisplayName(_service.ClientLanguage)}）。", UiStatus.Kind.Success);
            Output.AppendLog($"已连接游戏数据，客户端语言 {ToDisplayName(_service.ClientLanguage)}；加载 {_service.Entries.Count} 条词缀（{_service.Sources.Count} 个数据源）。");
            foreach (var note in _service.ConnectNotes)
                Output.AppendLog(note);
        }
        else
        {
            _allEntries = [];
            _checked.Clear();
            EntryList.ItemsSource = null;
            SourceTree.ItemsSource = null;
            FilterSummary.Text = "";
            CheckedSummary.Text = "";
            SourceSummary.Text = "未连接";
        }
    }

    /// <summary>左栏分组树：分组顺序沿用数据源目录的定义顺序。</summary>
    private static List<AffixSourceGroupVm> BuildSourceTree(IReadOnlyList<AffixDataService.SourceInfo> sources)
        => sources
            .GroupBy(s => s.Group)
            .Select(g => new AffixSourceGroupVm(g.Key, g.Select(s => new AffixSourceVm(s)).ToList()))
            .ToList();

    private bool RequirePoe2()
    {
        if (_gameKind != PoeGameKind.Poe2)
        {
            Output.SetStatus("词缀上色仅支持 POE2 客户端，请先在「价格标签」等页面完成游戏数据选择。", UiStatus.Kind.Warning);
            return false;
        }
        return true;
    }

    // ═══ 列表与检查器 ═══════════════════════════════════════════

    private void RebuildEntryList()
    {
        _allEntries = _service.Entries
            .Select(e =>
            {
                var colorId = AffixDataService.EffectiveColorId(_scheme, e);
                var previewOverride = _service.TryGetPreviewDoc(e.GamePath) is { } previewDoc
                    ? previewDoc.GetPreviewLines(e.StatKey, _service.ClientLanguage)
                    : null;
                return new EntryVm(e, colorId, colorId is not null && _scheme.IsRampId(colorId),
                    previewOverride, _service.ClientLanguage, _checked, RefreshCheckedSummary);
            })
            .ToList();
        SourceSummary.Text = $"{_service.Sources.Count} 个数据源 · {_service.Entries.Count} 条词缀 · {ToDisplayName(_service.ClientLanguage)}";
        ApplyFilter();
        WorkbenchPalette.Update(_scheme.Colors);
        RefreshRampCombo();
        EntryList.Items.Refresh();
    }

    private void ApplyFilter()
    {
        IEnumerable<EntryVm> query = _allEntries;
        if (_sourceFilter is { } source)
            query = query.Where(e => string.Equals(e.Entry.SourceKey, source, StringComparison.OrdinalIgnoreCase));
        if (_groupFilter is { } group)
            query = query.Where(e => e.Entry.Group == group);

        var keyword = SearchBox.Text.Trim();
        if (keyword.Length > 0)
        {
            query = RegexToggle.IsChecked == true
                ? ApplyRegex(query, keyword)
                : query.Where(e =>
                    e.DisplayText.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || e.StatKey.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var list = query.ToList();
        if (_service.PreviewActive)
        {
            // 预览模式：会把色的词缀排在最前面，方便先检查效果（其余保持原顺序）
            list = [.. list.OrderByDescending(vm => vm.PreviewColored)];
        }
        EntryList.ItemsSource = list;

        var scope = _sourceFilter is not null
            ? _service.Sources.FirstOrDefault(s => string.Equals(s.GamePath, _sourceFilter, StringComparison.OrdinalIgnoreCase))?.DisplayName
              ?? "当前来源"
            : _groupFilter ?? "全部";
        FilterSummary.Text = $"· {scope} {list.Count} 条";
    }

    /// <summary>正则过滤：同时匹配显示文本与 stat ID；表达式非法时给出提示并保持原列表。</summary>
    private IEnumerable<EntryVm> ApplyRegex(IEnumerable<EntryVm> source, string pattern)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return [.. source.Where(e => regex.IsMatch(e.DisplayText) || regex.IsMatch(e.StatKey))];
        }
        catch (ArgumentException ex)
        {
            Output.SetStatus($"正则表达式无效：{ex.Message}", UiStatus.Kind.Warning);
            return source;
        }
    }

    private void RegexToggle_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    // ═══ 勾选与批量上色 ═════════════════════════════════════════

    /// <summary>当前列表里可见的条目（勾选操作只作用于筛选结果，不会误伤全集）。</summary>
    private List<EntryVm> VisibleEntries() => [.. EntryList.ItemsSource?.Cast<EntryVm>() ?? []];

    private void RefreshCheckedSummary()
    {
        if (_bulkChecking)
            return;
        CheckedSummary.Text = _checked.Count == 0 ? "" : $"已勾选 {_checked.Count} 条";
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        _bulkChecking = true;
        try
        {
            foreach (var vm in VisibleEntries())
                vm.IsChecked = true;
        }
        finally
        {
            _bulkChecking = false;
        }
        RefreshCheckedSummary();
    }

    private void InvertCheck_Click(object sender, RoutedEventArgs e)
    {
        _bulkChecking = true;
        try
        {
            foreach (var vm in VisibleEntries())
                vm.IsChecked = !vm.IsChecked;
        }
        finally
        {
            _bulkChecking = false;
        }
        RefreshCheckedSummary();
    }

    private void ClearCheck_Click(object sender, RoutedEventArgs e)
    {
        _bulkChecking = true;
        try
        {
            foreach (var vm in _allEntries)
                vm.IsChecked = false;
        }
        finally
        {
            _bulkChecking = false;
        }
        _checked.Clear();
        RefreshCheckedSummary();
    }

    /// <summary>用检查器里选中的颜色，一次给所有勾选的词缀指派颜色。</summary>
    private void ApplyColorToChecked_Click(object sender, RoutedEventArgs e)
    {
        var targets = _allEntries.Where(vm => vm.IsChecked).ToList();
        if (targets.Count == 0)
        {
            Output.SetStatus("还没有勾选任何词缀：先用列表左侧的勾选框，或点「全选 / 反选」。", UiStatus.Kind.Warning);
            return;
        }
        if (InspectorColorCombo.SelectedItem is not string colorLabel)
        {
            Output.SetStatus("请先在右侧「条目检查器」里选一个颜色，再用它给勾选项批量上色。", UiStatus.Kind.Warning);
            return;
        }

        var colorId = colorLabel.Split('　')[0];
        foreach (var vm in targets)
        {
            _scheme.Assignments.RemoveAll(a => a.StatKey == vm.StatKey && a.FilePath == vm.Entry.GamePath);
            _scheme.Assignments.Add(new AffixAssignment(vm.StatKey, vm.Entry.GamePath, colorId));
        }
        Output.AppendLog($"批量上色：{targets.Count} 条勾选词缀 → {colorId}");
        Output.SetStatus($"已给 {targets.Count} 条词缀指派颜色 {colorId}；记得点「应用词缀修改」写入游戏，或先「保存方案」。", UiStatus.Kind.Success);
        RefreshPreview();
        RebuildEntryList();
        RefreshInspector();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void SourceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        switch (e.NewValue)
        {
            case AffixSourceVm source:
                _sourceFilter = source.Source.GamePath;
                _groupFilter = null;
                break;
            case AffixSourceGroupVm group:
                _sourceFilter = null;
                _groupFilter = group.Name;
                break;
            default:
                _sourceFilter = null;
                _groupFilter = null;
                break;
        }
        ApplyFilter();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        _sourceFilter = null;
        _groupFilter = null;
        SourceTree.ItemsSource = BuildSourceTree(_service.Sources); // 重建即取消选中
        ApplyFilter();
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshInspector();

    /// <summary>默认等级色阶：只建 T1~T4 四档，绿系明度/色调/透明度三重梯度、相邻档差距拉大
    /// （Tier1 荧光黄绿极醒目，逐档大幅变暗变深）。一键上色时若方案里还没有色阶，就自动建这一套；
    /// 想换色去「颜色修改」改即可。</summary>
    private static readonly (string Id, byte R, byte G, byte B, byte A)[] DefaultTierRamp =
    [
        ("Tier1", 200, 255, 70, 255),
        ("Tier2", 110, 235, 60, 240),
        ("Tier3", 65, 180, 55, 215),
        ("Tier4", 40, 120, 45, 185),
    ];

    /// <summary>历史版默认绿的 RGB 集合：用于把早期自动创建的暗绿色阶升级为当前梯度（用户改过色的不动）。</summary>
    private static readonly (byte R, byte G, byte B)[] LegacyDefaultTierGreens =
    [
        (90, 200, 70),
        (120, 230, 80),
        (170, 255, 100),
        (110, 235, 60),
        (65, 155, 60),
    ];

    /// <summary>取当前选中的色阶；没有就用/新建默认绿色阶。
    /// 方案里已存在按旧默认绿自动创建的 Tier1~Tier5 时，先升级成新梯度并移除 Tier5。</summary>
    private AffixColorRamp? EnsureTierRamp()
    {
        if (RampCombo.SelectedItem is AffixColorRamp selected)
            return selected;
        if (_scheme.FindRamp("Tier") is { } existing)
        {
            UpgradeLegacyTierRampColors();
            if (RampCombo.Items.Count == 0)
                RefreshRampCombo();
            RampCombo.SelectedItem = _scheme.FindRamp("Tier") ?? existing;
            return _scheme.FindRamp("Tier") ?? existing;
        }

        foreach (var (id, r, g, b, a) in DefaultTierRamp)
        {
            if (_scheme.FindColor(id) is null)
                _scheme.Colors.Add(new AffixColorDef(id, r, g, b, a));
        }
        RebuildEntryList(); // 刷新调色板与色阶下拉
        var ramp = _scheme.FindRamp("Tier");
        if (ramp is not null)
            RampCombo.SelectedItem = ramp;
        Output.AppendLog("已创建默认等级色阶 Tier1~Tier4（绿，T1 最醒目、逐档变暗）；想换颜色去「颜色修改」改即可。");
        return ramp;
    }

    /// <summary>把按历史版默认绿自动创建的 Tier1~Tier5 升级为当前四档梯度（含透明度）并移除 Tier5；
    /// 用户自己改过颜色的（RGB 不是任何历史默认值）保持不动。</summary>
    private void UpgradeLegacyTierRampColors()
    {
        var changed = false;
        for (var i = _scheme.Colors.Count - 1; i >= 0; i--)
        {
            var def = _scheme.Colors[i];
            var tierIndex = def.Id switch
            {
                "Tier1" => 0,
                "Tier2" => 1,
                "Tier3" => 2,
                "Tier4" => 3,
                _ => -1,
            };
            // T5 整档取消：默认梯度不再包含它（等阶 5+ 本来就不染色）
            if (def.Id == "Tier5")
            {
                if (LegacyDefaultTierGreens.Contains((def.R, def.G, def.B)))
                {
                    _scheme.Colors.RemoveAt(i);
                    changed = true;
                }
                continue;
            }
            if (tierIndex < 0)
                continue;
            var rgb = (def.R, def.G, def.B);
            if (!LegacyDefaultTierGreens.Contains(rgb))
                continue;
            var (_, r, g, b, a) = DefaultTierRamp[tierIndex];
            _scheme.Colors[i] = def with { R = r, G = g, B = b, A = a };
            changed = true;
        }
        if (changed)
        {
            WorkbenchPalette.Update(_scheme.Colors);
            Output.AppendLog("已把默认等级色阶升级为 T1~T4 四档（档位差距更大，T5 已取消）；想换色去「颜色修改」改即可。");
        }
    }

    private void RampCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshRampPreview();

    /// <summary>分档预览：按当前色阶 + 选中词缀的真实档位，渲染各等级的显示效果。</summary>
    private void RefreshRampPreview()
    {
        if (RampCombo.SelectedItem is not AffixColorRamp ramp)
        {
            RampPreview.ItemsSource = null;
            return;
        }

        var vm = EntryList.SelectedItem as EntryVm;
        var template = vm?.DisplayText ?? "示例：{0} 词缀数值";
        var ranges = vm is not null ? _service.TierRangesOf(vm.StatKey) : [];
        var rows = new List<TierPreviewVm>();

        if (ranges.Count >= 2)
        {
            // 按游戏内的等阶分档预览：同一等阶可能来自多条线（戒指线/武器线…），逐条列出
            var shown = ranges.Where(r => r.Tier >= 1 && r.Tier <= Math.Min(ramp.ColorIds.Count, AffixDataService.MaxColoredTiers))
                              .OrderBy(r => r.Tier)
                              .ToList();
            for (var i = 0; i < shown.Count; i++)
            {
                var tier = shown[i].Tier;
                var colorId = ramp.ColorIds[Math.Min(tier, ramp.ColorIds.Count) - 1];
                rows.Add(new TierPreviewVm($"T{tier}", colorId, PreviewOf(template, shown[i]), i == shown.Count - 1));
            }
        }
        else
        {
            // 没有档位数据时退化成"只预览色阶本身"
            for (var rank = 0; rank < ramp.ColorIds.Count; rank++)
                rows.Add(new TierPreviewVm($"T{rank + 1}", ramp.ColorIds[rank], $"{ramp.ColorIds[rank]}", rank == ramp.ColorIds.Count - 1));
        }
        RampPreview.ItemsSource = rows;
    }

    /// <summary>把词缀模板里的数值占位符换成该档的实际数值，用于预览文案。</summary>
    private static string PreviewOf(string template, (int Min, int Max, int Tier, int LineTiers) range)
        => System.Text.RegularExpressions.Regex.Replace(template, @"\{0([^{}]*)\}", match =>
            match.Groups[1].Value.Contains('+') ? $"+{range.Min}" : $"{range.Min}")
          + $"　（{range.Min}-{range.Max}）";

    private void RefreshInspector()
    {
        var vm = EntryList.SelectedItem as EntryVm;
        InspectorPanel.Visibility = vm is null ? Visibility.Collapsed : Visibility.Visible;
        InspectorStatKey.Text = vm?.StatKey ?? "未选择词缀";
        ApplyColorButton.IsEnabled = RemoveColorButton.IsEnabled = vm is not null;
        ApplyRampButton.IsEnabled = vm is not null && RampCombo.Items.Count > 0;

        InspectorColorCombo.ItemsSource = _scheme.Colors
            .Select(c => $"{c.Id}　{c.Hex}").ToList();
        var effective = vm?.ColorId;
        var index = _scheme.Colors.ToList().FindIndex(c => c.Id == effective);
        InspectorColorCombo.SelectedIndex = index;

        var brush = WorkbenchPalette.TryGet(effective, out var color)
            ? new SolidColorBrush(color)
            : Brushes.Transparent;
        EffectiveColorSwatch.Background = brush;

        LivePreview.ItemsSource = vm is not null && _service.TryGetDoc(vm.Entry.GamePath) is { } doc
            ? doc.GetPreviewLines(vm.Entry.StatKey, _service.ClientLanguage)
            : null;
        RefreshRampPreview();
    }

    private void ApplyColor_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryVm vm)
            return;
        if (InspectorColorCombo.SelectedItem is not string colorLabel)
        {
            Output.SetStatus("请先选择颜色。", UiStatus.Kind.Warning);
            return;
        }
        var colorId = colorLabel.Split('　')[0];
        _scheme.Assignments.RemoveAll(a => a.StatKey == vm.StatKey && a.FilePath == vm.Entry.GamePath);
        _scheme.Assignments.Add(new AffixAssignment(vm.StatKey, vm.Entry.GamePath, colorId));
        Output.AppendLog($"上色：{vm.StatKey} → {colorId}");
        RefreshPreview();
        RebuildEntryList();
        RefreshInspector();
    }

    /// <summary>一键：给所有带 tier 阶梯的词缀批量指派当前色阶（装备词缀不必逐个勾选）。
    /// 首次会构建档位索引（约 1 秒），之后复用。</summary>
    private void ApplyRampToAllTiered_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected)
        {
            Output.SetStatus("请先连接游戏数据（顶部「选择游戏数据」）。", UiStatus.Kind.Warning);
            return;
        }
        var ramp = EnsureTierRamp();
        if (ramp is null)
        {
            Output.SetStatus("无法创建等级色阶：方案里没有可用的颜色。", UiStatus.Kind.Warning);
            return;
        }

        Output.AppendLog("正在读取词缀档位数据（首次约 1 秒）…");
        var tiered = _service.TieredStatKeys();
        var targets = _allEntries.Where(vm => tiered.Contains(vm.StatKey)).ToList();
        if (targets.Count == 0)
        {
            Output.SetStatus("没有可用的档位数据：该客户端可能缺少 mods/stats 数据表。", UiStatus.Kind.Warning);
            return;
        }
        AssignRamp(targets, ramp);
    }

    /// <summary>给当前选中的词缀按数值区间分档染色（用色阶，只染数值占位符）。</summary>
    private void ApplyRamp_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryVm vm)
            return;
        if (RampCombo.SelectedItem is not AffixColorRamp ramp)
        {
            Output.SetStatus("请先选择色阶。色阶由同前缀的颜色自动成组（如 AT1/AT2/AT3 合成 AT），可用颜色修改里的「新增下一级」生成。", UiStatus.Kind.Warning);
            return;
        }
        AssignRamp([vm], ramp);
    }

    private void ApplyRampToChecked_Click(object sender, RoutedEventArgs e)
    {
        var targets = _allEntries.Where(vm => vm.IsChecked).ToList();
        if (targets.Count == 0)
        {
            Output.SetStatus("还没有勾选任何词缀：先用列表左侧的勾选框，或点「全选 / 反选」。", UiStatus.Kind.Warning);
            return;
        }
        if (RampCombo.SelectedItem is not AffixColorRamp ramp)
        {
            Output.SetStatus("请先在右侧「条目检查器」里选择一个色阶。", UiStatus.Kind.Warning);
            return;
        }
        AssignRamp(targets, ramp);
    }

    /// <summary>把色阶指派给若干词缀：指派里存色阶前缀（可写作「正向|负向」），
    /// 生成补丁时按行首数值区间展开成分档颜色、并把带正负号的行拆成提高/降低两行。</summary>
    private void AssignRamp(IReadOnlyList<EntryVm> targets, AffixColorRamp ramp)
    {
        var negative = NegativeRampCombo.SelectedItem as AffixColorRamp;
        var colorId = negative is null ? ramp.Prefix : $"{ramp.Prefix}|{negative.Prefix}";
        foreach (var vm in targets)
        {
            _scheme.Assignments.RemoveAll(a => a.StatKey == vm.StatKey && a.FilePath == vm.Entry.GamePath);
            _scheme.Assignments.Add(new AffixAssignment(vm.StatKey, vm.Entry.GamePath, colorId));
        }
        var detail = negative is null ? "按数值区间分档" : $"按数值区间分档 + 正负拆分（负向 {negative.Prefix}）";
        Output.AppendLog($"分档上色：{targets.Count} 条词缀 → {colorId}（{detail}）");
        Output.SetStatus($"已给 {targets.Count} 条词缀指派 {colorId}（{detail}，只染数值）；记得点「应用词缀修改」写入游戏。", UiStatus.Kind.Success);
        RefreshPreview();
        RebuildEntryList();
        RefreshInspector();
    }

    /// <summary>重建色阶下拉（颜色变化时），尽量保留原来的选择。</summary>
    private void RefreshRampCombo()
    {
        var ramps = _scheme.Ramps();
        FillRampCombo(RampCombo, ramps, applyWhenEmpty: true);
        FillRampCombo(NegativeRampCombo, ramps, applyWhenEmpty: false);
        ApplyRampCheckedButton.IsEnabled = ramps.Count > 0;
        ApplyRampButton.IsEnabled = EntryList.SelectedItem is EntryVm && ramps.Count > 0;
    }

    private static void FillRampCombo(System.Windows.Controls.ComboBox combo, IReadOnlyList<AffixColorRamp> ramps, bool applyWhenEmpty)
    {
        var previous = (combo.SelectedItem as AffixColorRamp)?.Prefix;
        combo.ItemsSource = ramps;
        if (ramps.Count == 0)
        {
            combo.SelectedIndex = -1;
            return;
        }
        var index = previous is null ? -1 : ramps.ToList().FindIndex(r => r.Prefix == previous);
        if (index < 0 && !applyWhenEmpty)
        {
            combo.SelectedIndex = -1; // 负向色阶允许留空
            return;
        }
        combo.SelectedIndex = Math.Max(0, index);
    }

    private void RemoveColor_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryVm vm)
            return;
        var removed = _scheme.Assignments.RemoveAll(a => a.StatKey == vm.StatKey && a.FilePath == vm.Entry.GamePath);
        Output.AppendLog(removed > 0 ? $"移除指派：{vm.StatKey}" : $"该词缀没有手动指派（规则命中不受影响）：{vm.StatKey}");
        RefreshPreview();
        RebuildEntryList();
        RefreshInspector();
    }

    // ═══ 方案管理 ═══════════════════════════════════════════════

    /// <summary>把语义别名色映射到默认色阶的某档：改默认色阶时这里自动跟随。</summary>
    private static AffixColorDef TierMapped(string tierId, string aliasId)
    {
        var tier = DefaultTierRamp.First(t => t.Id == tierId);
        return new AffixColorDef(aliasId, tier.R, tier.G, tier.B);
    }

    private static AffixColorScheme CreateDefaultScheme() => new()
    {
        Name = "我的方案",
        Colors =
        [
            TierMapped("Tier1", "VeryLucky"),   // 高收益 = T1 荧光黄绿（最醒目）
            TierMapped("Tier4", "Lucky"),       // 收益 = T4 深绿（低调）
            new AffixColorDef("Dangerous", 255, 140, 0),    // 危险·橙
            new AffixColorDef("VeryDangerous", 255, 30, 30) // 高危·红
        ],
        Rules = [],
        Assignments = [],
    };

    private void RefreshSchemeCombo(bool initial = false)
    {
        var names = AffixColorScheme.ListSchemeNames();
        if (initial && names.Count == 0)
        {
            _scheme.Save();
            names = [_scheme.Name];
        }
        SchemeCombo.ItemsSource = names;
        var current = _scheme.Name;
        SchemeCombo.SelectedItem = names.Contains(current) ? current : names.FirstOrDefault();
    }

    private void SchemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SchemeCombo.SelectedItem is not string name || name == _scheme.Name)
            return;
        try
        {
            _scheme = AffixColorScheme.Load(name);
            WorkbenchPalette.Update(_scheme.Colors);
            RefreshPreview();
            RebuildEntryList();
            RefreshInspector();
            Output.AppendLog($"已加载方案：{name}（{_scheme.Colors.Count} 颜色 / {_scheme.Rules.Count} 规则 / {_scheme.Assignments.Count} 指派）");
        }
        catch (Exception ex)
        {
            Output.SetStatus($"加载方案失败：{ex.Message}", UiStatus.Kind.Error);
        }
    }

    private void SaveScheme_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _scheme.Save();
            RefreshSchemeCombo();
            Output.SetStatus($"方案「{_scheme.Name}」已保存。", UiStatus.Kind.Success);
        }
        catch (Exception ex)
        {
            Output.SetStatus($"保存方案失败：{ex.Message}", UiStatus.Kind.Error);
        }
    }

    private void DeleteScheme_Click(object sender, RoutedEventArgs e)
    {
        var name = SchemeCombo.SelectedItem as string;
        if (name is null)
            return;
        var result = MessageBox.Show(Window.GetWindow(this)!, $"确定删除方案「{name}」？（不影响已应用到游戏的补丁）",
            "删除方案", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;
        try
        {
            File.Delete(AffixColorScheme.PathOf(name));
            if (_scheme.Name == name)
                _scheme = CreateDefaultScheme();
            RefreshSchemeCombo();
            RebuildEntryList();
            Output.SetStatus($"方案「{name}」已删除。", UiStatus.Kind.Success);
        }
        catch (Exception ex)
        {
            Output.SetStatus($"删除方案失败：{ex.Message}", UiStatus.Kind.Error);
        }
    }

    private void ImportScheme_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "导入上色方案", Filter = "上色方案 (*.json)|*.json" };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var name = Path.GetFileNameWithoutExtension(dlg.FileName);
            _scheme = AffixColorScheme.Import(dlg.FileName, name);
            _scheme.Save();
            RefreshSchemeCombo();
            WorkbenchPalette.Update(_scheme.Colors);
            RefreshPreview();
            RebuildEntryList();
            RefreshInspector();
            var issues = _scheme.Validate();
            Output.SetStatus(issues.Count == 0
                ? $"已导入并保存方案「{name}」。"
                : $"已导入方案「{name}」，但存在配置问题：{string.Join("；", issues)}", UiStatus.Kind.Warning);
        }
        catch (Exception ex)
        {
            Output.SetStatus($"导入方案失败：{ex.Message}", UiStatus.Kind.Error);
        }
    }

    private void ExportScheme_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "导出上色方案", FileName = _scheme.Name, Filter = "上色方案 (*.json)|*.json" };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            _scheme.Export(dlg.FileName);
            Output.SetStatus($"方案已导出：{dlg.FileName}", UiStatus.Kind.Success);
        }
        catch (Exception ex)
        {
            Output.SetStatus($"导出方案失败：{ex.Message}", UiStatus.Kind.Error);
        }
    }

    private void ColorEditor_Click(object sender, RoutedEventArgs e)
    {
        var window = new ColorEditorWindow(_scheme) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true)
        {
            WorkbenchPalette.Update(_scheme.Colors);
            RefreshPreview();
            RebuildEntryList();
            RefreshInspector();
        }
    }

    // ═══ 应用 / 恢复 ═══════════════════════════════════════════

    /// <summary>方案被修改后自动重算「应用后效果」：在内存里模拟一次应用（不写盘），
    /// 随后的列表重建就会展示变色后的真实样子（按档拆行、只染数值），会变色的排最前面。</summary>
    private void RefreshPreview()
    {
        if (!_service.IsConnected)
        {
            _service.ClearPreview();
            return;
        }
        try
        {
            _service.ComputePreview(_scheme);
        }
        catch (Exception ex)
        {
            _service.ClearPreview();
            Output.AppendLog($"预览应用效果失败（不影响已保存的方案）：{ex.Message}");
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePoe2() || !_service.IsConnected)
        {
            Output.SetStatus("请先连接游戏。", UiStatus.Kind.Warning);
            return;
        }
        await RunBusyAsync("应用词缀修改", async () =>
        {
            var mismatched = _service.VerifyUnchanged();
            if (mismatched.Count > 0)
            {
                return $"游戏文件与连接时不一致（{string.Join("、", mismatched)}），" +
                       "可能被其他补丁或外部工具修改过。请点击「打开游戏文件」重读后再试。";
            }

            // 从原始索引基线提取真原版（约 10~30 秒）：即使客户端被历史坏补丁污染，
            // 本次应用的原版备份也是真原版，之后「恢复原版」才能回到干净状态
            Output.AppendLog("正在提取原始索引基线中的原版文件（首次约 10~30 秒）……");
            await Task.Run(() => _service.LoadTrueOriginals());

            IReadOnlyList<AffixPatchBuilder.FileChange> changes;
            try
            {
                changes = _service.ComputeChanges(_scheme);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }

            var jsonPath = AffixPatchBuilder.Build(changes);
            Output.AppendLog($"补丁已生成（Version 递增）：{jsonPath}");

            // 引擎要整文件替换 _.index.bin；本页连接时持有的只读映射会阻塞替换
            // （Windows：对有映射打开的文件拒绝删除/替换），必须在写盘前释放
            _service.ReleaseFileLocks();
            Output.AppendLog("已释放游戏数据句柄；本次操作完成后如需继续调整，请重新连接。");

            var ok = await FxEngineRunner.RunAsync(
                "应用词缀修改",
                [new FxEngineRunner.Invocation(null, [_gameDataPath!, jsonPath, "apply"])],
                _gameDataPath,
                skipGameData: false,
                Output.ClearLog,
                Output.AppendLog,
                (text, kind) => Output.SetStatus(text, kind),
                enabled => SetEngineBusy(!enabled));
            if (!ok)
                return "应用未完成，详见引擎输出。";

            _service.AdvanceBaseline(changes);
            _service.ClearPreview();
            RebuildEntryList();
            return "词缀颜色补丁已应用。可在游戏中查看效果；继续调整方案前请先重新连接游戏。";
        });
    }

    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        var jsonPath = Path.Combine(ConfigService.PatchesDirectory, AffixPatchBuilder.PatchId, AffixPatchBuilder.PatchId + ".patch.json");
        if (!File.Exists(jsonPath))
        {
            Output.SetStatus("还没有应用过词缀补丁，无需恢复。", UiStatus.Kind.Neutral);
            return;
        }
        var result = MessageBox.Show(Window.GetWindow(this)!, "将全部词缀描述与界面颜色恢复为应用前状态？（补丁生成产物保留，可重新应用）",
            "恢复原版", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("恢复原版", async () =>
        {
            // 引擎要整文件替换 _.index.bin；本页连接时持有的只读映射会阻塞替换，必须先释放
            _service.ReleaseFileLocks();
            Output.AppendLog("已释放游戏数据句柄；恢复完成后请重新连接。");

            var ok = await FxEngineRunner.RunAsync(
                "恢复原版",
                [new FxEngineRunner.Invocation(null, [_gameDataPath!, jsonPath, "revert"])],
                _gameDataPath,
                skipGameData: false,
                Output.ClearLog,
                Output.AppendLog,
                (text, kind) => Output.SetStatus(text, kind),
                enabled => SetEngineBusy(!enabled));
            if (!ok)
                return "恢复未完成，详见引擎输出。";
            _service.MarkStale();
            RebuildEntryList();
            return "已恢复原版。数据基线已失效，请点击「打开游戏文件」重新加载。";
        });
    }

    // ═══ 通用 ═══════════════════════════════════════════════════

    private void SetEngineBusy(bool busy)
    {
        _busy = busy;
        ApplyButton.IsEnabled = RevertButton.IsEnabled = ConnectButton.IsEnabled = !busy;
    }

    private async Task RunBusyAsync(string action, Func<Task<string>> work)
    {
        if (_busy)
        {
            Output.SetStatus("已有任务在执行，请稍候。", UiStatus.Kind.Warning);
            return;
        }
        _busy = true;
        SetEngineBusy(true);
        try
        {
            var message = await work();
            var warning = message.Contains("不一致") || message.Contains("失败") || message.Contains("未完成");
            Output.SetStatus(message, warning ? UiStatus.Kind.Warning : UiStatus.Kind.Success);
            Output.AppendLog(message);
        }
        catch (Exception ex)
        {
            Output.SetStatus($"{action}出错：{ex.Message}", UiStatus.Kind.Error);
            Output.AppendLog($"[错误] {ex.Message}");
            FileLogger.App.Error($"词缀上色 {action} 失败。", ex);
        }
        finally
        {
            _busy = false;
            SetEngineBusy(false);
        }
    }
}

/// <summary>左栏数据源节点（单个 csd 或一个目录）。显示名与分组名一律用中文。</summary>
public sealed class AffixSourceVm(AffixDataService.SourceInfo source)
{
    public AffixDataService.SourceInfo Source { get; } = source;

    public string Label { get; } = source.StatCount > 0
        ? $"{source.DisplayName}（{source.StatCount} 条）"
        : $"{source.DisplayName}（无条目）";

    public string Tooltip { get; } = source.FileCount > 1
        ? $"{source.GamePath}\n{source.FileCount} 个文件 · {source.SizeBytes / 1024.0 / 1024.0:F1} MB"
        : $"{source.GamePath}\n{source.SizeBytes / 1024.0 / 1024.0:F1} MB";
}

/// <summary>左栏分组节点。组的排布顺序即数据源目录的定义顺序。</summary>
public sealed class AffixSourceGroupVm
{
    public AffixSourceGroupVm(string name, IReadOnlyList<AffixSourceVm> children)
    {
        Name = name;
        Children = children;
        Label = $"{name}（{children.Sum(c => c.Source.StatCount)} 条）";
    }

    public string Name { get; }
    public IReadOnlyList<AffixSourceVm> Children { get; }
    public string Label { get; }
}

/// <summary>分档预览的一行：等级标签 + 该档的实际显示效果（用对应颜色直接渲染出来）。</summary>
public sealed class TierPreviewVm(string rank, string colorId, string text, bool isLowestColored)
{
    public string Rank { get; } = rank;
    public string ColorId { get; } = colorId;
    public string Text { get; } = text;

    /// <summary>最低的染色档会额外标一句"更低档不上色"。</summary>
    public string Note { get; } = isLowestColored ? "（更低的档不上色）" : "";

    public Brush Swatch { get; } =
        WorkbenchPalette.TryGet(colorId, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
}
