using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PoEToolbox.Sdk;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.FxPatch;

public partial class FxPatchView : UserControl
{
    private readonly IEventBus _eventBus;

    private sealed class BuiltInRow
    {
        public required FxPatchEngine.BuiltInPatchDef Def;
        public required CheckBox Check;
        public required TextBlock StateText;
        public FxPatchEngine.PatchState? State;
    }

    private sealed class ThirdPartyRow
    {
        public required FxPatchStateStore.AppliedPatch Entry;
        public required CheckBox Check;
    }

    private readonly List<BuiltInRow> _builtInRows = [];
    private readonly List<ThirdPartyRow> _thirdPartyRows = [];
    /// <summary>内置区里的「内置词缀修改补丁」固定行（执行走词缀补丁 json 通道，不占内置补丁 id）。</summary>
    private CheckBox? _affixCheck;
    private TextBlock? _affixStateText;
    private Button? _affixExportButton;
    private FxPatchEngine.PatchState? _affixState;
    /// <summary>程序化设置勾选时抑制「用户改过勾选」标记。</summary>
    private bool _suppressCheckEvents;
    /// <summary>用户手动改过勾选后，自动刷新不再覆盖他的选择（除非显式点刷新）。</summary>
    private bool _userEditedSelection;
    private bool _statusStale = true;
    private string? _statusLoadedPath;
    private bool _refreshing;

    private string? _gameDataPath;
    private PoeGameKind _gameKind;

    public FxPatchView(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        InitializeComponent();
        BuildBuiltInChecks();
        _eventBus.Subscribe<GameContextChanged>(OnGameContextChanged);
        Loaded += (_, _) => RefreshStatusIfStale();
    }

    public void Dispose() => _eventBus.Unsubscribe<GameContextChanged>(OnGameContextChanged);

    /// <summary>按引擎内置补丁注册表生成勾选列表，新增内置补丁自动出现在这里。
    /// 每行 = 勾选框 + 右侧启用状态（读了补丁记录后填入）。</summary>
    private void BuildBuiltInChecks()
    {
        foreach (var def in FxPatchEngine.BuiltIns)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new CheckBox
            {
                Content = $"{def.DisplayName}（{def.Id}）",
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            check.Checked += (_, _) => OnUserToggle();
            check.Unchecked += (_, _) => OnUserToggle();

            var stateText = new TextBlock
            {
                Text = "待检测",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Style = FindResource("SmallText") as Style,
            };
            stateText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            grid.Children.Add(check);
            grid.Children.Add(stateText);
            Grid.SetColumn(stateText, 1);
            var export = MakeExportButton(FxPatchEngine.TryResolveBuiltInPatchPath(def.Id));
            if (export is not null)
            {
                grid.Children.Add(export);
                Grid.SetColumn(export, 2);
            }

            BuiltInChecksPanel.Children.Add(grid);
            _builtInRows.Add(new BuiltInRow { Def = def, Check = check, StateText = stateText });
        }

        AppendAffixRow();
    }

    /// <summary>内置区末尾追加「内置词缀修改补丁」固定行：由「词缀上色」页生成与维护，
    /// 本页负责启用 / 还原 / 卸载；执行仍走词缀补丁 json 通道（fx-patch），不占内置补丁 id，
    /// 所以不进 <see cref="_builtInRows"/>（那里按 id 走 fx-oilmod 通道）。</summary>
    private void AppendAffixRow()
    {
        // 与引擎内置特效补丁隔开：这个词缀补丁由「词缀上色」页生成，不是引擎 BuiltIns
        var sep = new Separator { Margin = new Thickness(0, 8, 0, 4) };
        sep.SetResourceReference(Separator.BackgroundProperty, "BorderBrush");
        BuiltInChecksPanel.Children.Add(sep);

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            Content = "内置词缀修改补丁（词缀上色）",
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "由「词缀上色」页生成并维护：调整颜色 / 规则去那个页，本页只负责启用、还原与卸载。",
        };
        check.Checked += (_, _) => OnUserToggle();
        check.Unchecked += (_, _) => OnUserToggle();

        var stateText = new TextBlock
        {
            Text = "待检测",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Style = FindResource("SmallText") as Style,
        };
        stateText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        grid.Children.Add(check);
        grid.Children.Add(stateText);
        Grid.SetColumn(stateText, 1);

        // 词缀补丁 json 在「词缀上色」首次应用后才存在：按钮常驻、按需显隐
        if (MakeExportButton(AffixPatchJsonPath, requireExisting: false) is { } export)
        {
            export.Visibility = File.Exists(AffixPatchJsonPath) ? Visibility.Visible : Visibility.Collapsed;
            _affixExportButton = export;
            grid.Children.Add(export);
            Grid.SetColumn(export, 2);
        }

        _affixCheck = check;
        _affixStateText = stateText;
        BuiltInChecksPanel.Children.Add(grid);
    }

    /// <summary>词缀固定行的状态与勾选：账本里有 affix-workbench 条目（引擎在应用时写入）= 已启用。
    /// 状态字 / Tooltip 与内置行同一套；补丁内容变更请到「词缀上色」页重新应用，本页不校验内容。</summary>
    private void UpdateAffixRow(List<FxPatchStateStore.AppliedPatch> entries, bool overwriteUserSelection)
    {
        if (_affixCheck is null || _affixStateText is null)
            return;

        _affixState = entries.Any(IsAffixPatchEntry)
            ? FxPatchEngine.PatchState.Applied
            : FxPatchEngine.PatchState.NotApplied;
        _affixStateText.Text = FriendlyState(_affixState.Value);
        _affixStateText.SetResourceReference(TextBlock.ForegroundProperty, BrushKeyOf(_affixState.Value));
        _affixStateText.ToolTip = !File.Exists(AffixPatchJsonPath) && _affixState == FxPatchEngine.PatchState.Applied
            // 账本说打过、但补丁产物不在这台机器上（换过电脑 / 清理过补丁目录）：
            // 这种情况下本页没有可执行的还原入口，得说清楚让用户回「词缀上色」补一次。
            ? "游戏里已经应用了词缀修改，但本机找不到对应的补丁文件（记录来自其他机器或补丁目录被清理过）。"
              + "本页无法单独还原它：到「词缀上色」页重新应用一次即可补上；要彻底清掉请用「彻底还原游戏客户端」。"
            : TooltipOf(_affixState.Value, "");
        _affixExportButton?.Visibility = File.Exists(AffixPatchJsonPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _suppressCheckEvents = true;
        try
        {
            // json 还不存在 = 从没用过词缀上色，没有可启用的东西：禁用勾选并指路
            var hasPatch = File.Exists(AffixPatchJsonPath);
            _affixCheck.IsEnabled = hasPatch;
            _affixCheck.ToolTip = hasPatch
                ? "由「词缀上色」页生成并维护：调整颜色 / 规则去那个页，本页只负责启用、还原与卸载。"
                : "还没有生成过词缀补丁：先到「词缀上色」页连接游戏并点「应用词缀修改」，之后就能在这里启停。";
            if (overwriteUserSelection)
                _affixCheck.IsChecked = _affixState == FxPatchEngine.PatchState.Applied;
        }
        finally
        {
            _suppressCheckEvents = false;
        }
    }

    private void OnUserToggle()
    {
        if (_suppressCheckEvents)
            return;
        _userEditedSelection = true;
    }

    /// <summary>当前勾选的内置补丁 ID（按注册表顺序）。</summary>
    private List<string> CheckedBuiltInIds =>
        _builtInRows.Where(r => r.Check.IsChecked == true).Select(r => r.Def.Id).ToList();

    private void OnGameContextChanged(GameContextChanged context)
    {
        if (string.IsNullOrWhiteSpace(context.GameDataPath))
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGameContextChanged(context));
            return;
        }

        // 只记路径：读补丁记录放在切到本模块时（OnActivated / Loaded）做。
        if (_gameDataPath != context.GameDataPath)
            _statusStale = true;
        _gameDataPath = context.GameDataPath;
        _gameKind = context.Game;
    }

    // ═══ 补丁状态：读记录（快）═══════════════════════════════
    /// <summary>由插件在切到本模块时调用；只在状态陈旧（换过游戏目录或刚执行过操作）时重读。</summary>
    public void RefreshStatusIfStale(bool force = false)
    {
        if (_gameKind != PoeGameKind.Poe2)
            return;
        var path = (_gameDataPath ?? GameDataPathPreference.Get())?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!force && !_statusStale && _statusLoadedPath == path)
            return;

        // 还没有补丁记录（例如升级前就打过补丁）：先扫一次索引把账本建起来，之后都只读这个小文件。
        if (!File.Exists(FxPatchStateStore.FilePathOf(path)))
        {
            _ = RescanAsync(quiet: true);
            return;
        }

        LoadRecordedPatches(path, overwriteUserSelection: force || !_userEditedSelection);
        _statusStale = false;
        _statusLoadedPath = path;
    }

    /// <summary>读引擎写下的补丁记录（一个小 json，不开索引），把已启用的补丁默认勾上。</summary>
    private void LoadRecordedPatches(string path, bool overwriteUserSelection)
    {
        var entries = FxPatchStateStore.Read(path);
        var appliedIds = new HashSet<string>(
            entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

        var states = new Dictionary<string, (FxPatchEngine.PatchState State, string Tip)>();
        foreach (var row in _builtInRows)
            states[row.Def.Id] = appliedIds.Contains(row.Def.Id)
                ? (FxPatchEngine.PatchState.Applied, TooltipOf(FxPatchEngine.PatchState.Applied, ""))
                : (FxPatchEngine.PatchState.NotApplied, TooltipOf(FxPatchEngine.PatchState.NotApplied, ""));

        ApplyStates(states, overwriteUserSelection);
        UpdateAffixRow(entries, overwriteUserSelection);

        // 第三方补丁（自定义 / 整包替换型）以与内置一致的勾选行展示在左侧下半区。
        RenderThirdPartyList(entries);
        SetStateHint(BuiltInSummaryHint());
    }

    private string BuiltInSummaryHint()
    {
        var applied = _builtInRows.Count(r => r.State == FxPatchEngine.PatchState.Applied)
                      + (_affixState == FxPatchEngine.PatchState.Applied ? 1 : 0);
        return applied == 0
            ? "当前没有已启用的内置补丁，勾选后点「启用特效补丁」即可。"
            : $"已启用 {applied} 个内置补丁，已自动勾选；需要还原直接点「彻底还原游戏客户端」。";
    }

    /// <summary>底部输入框当前对应的待应用补丁文件完整路径；null 表示没有选择。
    /// 输入框只显示文件名，完整路径只存在这个字段里。</summary>
    private string? _pendingPatchFile;

    /// <summary>当前输入框里的补丁文件路径（待应用）；空视为无。</summary>
    private string? PendingPatchPath => _pendingPatchFile;

    /// <summary>输入框文件本轮是否参与执行：列表里没有代表它的行（刚选完文件，保持「选完直接启用」），
    /// 或代表它的行被勾选。行存在但未勾选 = 用户明确不要它参与。</summary>
    private bool PendingFileParticipates()
    {
        var custom = PendingPatchPath;
        if (string.IsNullOrWhiteSpace(custom) || !File.Exists(custom))
            return false;
        var row = FindThirdPartyRowBySource(custom);
        return row is null || row.Check.IsChecked == true;
    }

    /// <summary>第三方列表里 SourceFile 与指定路径相同的行；没有返回 null。</summary>
    private ThirdPartyRow? FindThirdPartyRowBySource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var full = Path.GetFullPath(path);
        return _thirdPartyRows.FirstOrDefault(r =>
            r.Entry.SourceFile is { } src
            && Path.GetFullPath(src).Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    private static string PendingDisplayName(string path)
        => Path.GetFileName(path)
               .Replace(".patch.json", "", StringComparison.OrdinalIgnoreCase)
               .Replace(".json", "", StringComparison.OrdinalIgnoreCase)
               .Replace(".zip", "", StringComparison.OrdinalIgnoreCase) is { Length: > 0 } name
            ? name
            : path;

    /// <summary>把第三方补丁渲染成与内置一致的勾选行：
    /// 底部输入框选中的补丁文件作为「待应用」条目随后、默认勾选、右侧显示未启用；
    /// 账本里已应用的其他条目（含整包替换型）跟随其后，显示已启用。
    /// 「内置词缀修改补丁」固定在内置区末尾（见 AppendAffixRow），不在这里渲染。
    /// 勾选 = 选中它参与「启用 / 恢复 / 卸载」操作；已应用条目的操作靠账本记录的原补丁文件路径。</summary>
    private void RenderThirdPartyList(List<FxPatchStateStore.AppliedPatch> entries)
    {
        if (ThirdPartyPanel is null || ThirdPartyEmpty is null)
            return;

        ThirdPartyPanel.Children.Clear();
        _thirdPartyRows.Clear();

        var pending = PendingPatchPath;
        var hasPending = pending is not null && entries.All(e =>
            string.IsNullOrWhiteSpace(e.SourceFile)
            || !string.Equals(Path.GetFullPath(e.SourceFile), Path.GetFullPath(pending), StringComparison.OrdinalIgnoreCase));

        var extras = entries
            .Where(e => !string.Equals(e.Kind, FxPatchStateStore.KindBuiltIn, StringComparison.OrdinalIgnoreCase))
            .Where(e => !IsAffixPatchEntry(e)) // 词缀补丁固定在内置区，账本条目不进第三方列表
            .ToList();
        ThirdPartyEmpty.Visibility = extras.Count == 0 && !hasPending ? Visibility.Visible : Visibility.Collapsed;

        if (hasPending)
        {
            AddThirdPartyRow(
                new FxPatchStateStore.AppliedPatch(
                    PendingDisplayName(pending!), pending!, FxPatchStateStore.KindCustom, null,
                    DateTimeOffset.UtcNow, pending),
                applied: false);
        }

        foreach (var e in extras)
            AddThirdPartyRow(e, applied: true);

        // 整包替换型只允许勾一个：待应用行默认勾选，若它已勾选，把已应用的整包行让位取消。
        var pendingRow = _thirdPartyRows.FirstOrDefault(r => r.Check.IsChecked == true && IsRawPackEntry(r.Entry));
        if (pendingRow is not null)
            EnforceSingleRawPack(pendingRow.Check);
    }

    /// <summary>词缀上色模块生成的补丁（固定单例 id，不随版本变）。</summary>
    private static bool IsAffixPatchEntry(FxPatchStateStore.AppliedPatch e)
        => string.Equals(e.Id, AffixPatchBuilder.PatchId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(e.Name, AffixPatchBuilder.PatchId, StringComparison.OrdinalIgnoreCase);

    /// <summary>词缀补丁描述文件的固定路径（词缀上色每次应用都会重写它，还原后仍在）。</summary>
    private static string AffixPatchJsonPath
        => Path.Combine(ConfigService.PatchesDirectory, AffixPatchBuilder.PatchId,
            AffixPatchBuilder.PatchId + ".patch.json");

    /// <summary>该第三方补丁是否属于整包替换型（会整体替换 _.index.bin）。</summary>
    private bool IsRawPackEntry(FxPatchStateStore.AppliedPatch entry)
        => string.Equals(entry.Kind, FxPatchStateStore.KindRawPack, StringComparison.OrdinalIgnoreCase)
           || (entry.SourceFile is not null && File.Exists(entry.SourceFile) && IsRawPackSource(entry.SourceFile));

    /// <summary>整包替换型补丁互斥：多个都会整体替换 _.index.bin，同时打必然互相冲掉。
    /// 勾选一个时自动取消其他整包行的勾选，并提示被让位的补丁。</summary>
    private void EnforceSingleRawPack(CheckBox source)
    {
        if (source.IsChecked != true)
            return;
        var row = _thirdPartyRows.FirstOrDefault(r => ReferenceEquals(r.Check, source));
        if (row is null || !IsRawPackEntry(row.Entry))
            return;

        var yielded = _thirdPartyRows
            .Where(r => !ReferenceEquals(r.Check, source)
                        && r.Check.IsChecked == true
                        && IsRawPackEntry(r.Entry))
            .ToList();
        if (yielded.Count == 0)
            return;
        foreach (var r in yielded)
            r.Check.IsChecked = false;
        SetStatus($"整包替换型补丁同一时间只能启用一个（都会整体替换游戏索引）：已让位 {string.Join("、", yielded.Select(r => r.Entry.Name))}。", UiStatus.Kind.Warning);
    }

    private void AddThirdPartyRow(FxPatchStateStore.AppliedPatch entry, bool applied, bool? defaultChecked = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hasSource = !string.IsNullOrWhiteSpace(entry.SourceFile) && File.Exists(entry.SourceFile);
        // 词缀补丁固定在内置区（AppendAffixRow），这里只渲染待应用与账本第三方条目
        var displayName = entry.Name.Contains('/') || entry.Name.Contains('\\')
            ? Path.GetFileNameWithoutExtension(entry.Name)
            : entry.Name;
        var check = new CheckBox
        {
            Content = $"{displayName}（{KindLabel(entry.Kind)}）",
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = defaultChecked ?? !applied,
            IsEnabled = hasSource,
            ToolTip = applied
                ? (hasSource
                    ? (IsRawPackEntry(entry)
                        ? "整包替换型补丁：它修改了游戏索引（_.index.bin），卸载它会把索引整体换回官方原版，其他所有已启用的补丁都会被连带卸载。"
                        : "勾选后点「卸载已勾选补丁」即可删除这个补丁新增的文件（按应用时记录的原补丁文件执行）。")
                    : "这条记录里没有可用的原补丁文件（旧记录或文件已移动/删除），无法勾选操作；重新应用一次即可补上记录。")
                : "刚选择、还没有写入游戏的补丁文件；保持勾选并点「启用特效补丁」即可应用。",
        };
        if (IsRawPackEntry(entry))
            check.Checked += (_, _) => EnforceSingleRawPack(check);

        var stateText = new TextBlock
        {
            Text = applied ? "已启用" : "未启用",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Style = FindResource("SmallText") as Style,
        };
        stateText.SetResourceReference(TextBlock.ForegroundProperty,
            applied ? "SuccessBrush" : "TextSecondaryBrush");

        grid.Children.Add(check);
        grid.Children.Add(stateText);
        Grid.SetColumn(stateText, 1);
        var export = MakeExportButton(entry.SourceFile);
        if (export is not null)
        {
            grid.Children.Add(export);
            Grid.SetColumn(export, 2);
        }

        ThirdPartyPanel.Children.Add(grid);
        _thirdPartyRows.Add(new ThirdPartyRow { Entry = entry, Check = check });
    }

    /// <summary>行尾的「导出」小按钮：把该补丁打包为可分发的 zip；来源缺失则不出按钮。</summary>
    private Button? MakeExportButton(string? sourcePath, bool requireExisting = true)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || (requireExisting && !File.Exists(sourcePath)))
            return null;
        var btn = new Button
        {
            Content = "导出",
            Style = FindResource("SecondaryButton") as Style,
            Padding = new Thickness(8, 2, 8, 2),
            Height = 24,
            MinWidth = 52,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "把这个补丁打包为可分发的 zip（描述文件 + assets），发给别人在「启用特效补丁」处直接选用。",
            Tag = sourcePath,
        };
        btn.Click += ExportPatch_Click;
        return btn;
    }

    private void ExportPatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string source } || !File.Exists(source))
            return;
        var dlg = new SaveFileDialog
        {
            Title = "导出补丁",
            FileName = Path.GetFileNameWithoutExtension(source) + ".zip",
            Filter = "补丁包 (*.zip)|*.zip",
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            PatchExportService.ExportTo(source, dlg.FileName);
            SetStatus($"✅ 已导出补丁：{dlg.FileName}", UiStatus.Kind.Success);
        }
        catch (Exception ex)
        {
            FileLogger.App.Error("导出补丁失败。", ex);
            SetStatus("❌ 导出补丁失败：" + ex.Message, UiStatus.Kind.Error);
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        FxPatchStateStore.KindCustom => "自定义补丁",
        FxPatchStateStore.KindRawPack => "整包替换补丁",
        _ => "内置补丁",
    };

    private void ApplyStates(
        IReadOnlyDictionary<string, (FxPatchEngine.PatchState State, string Tip)> states,
        bool overwriteUserSelection)
    {
        _suppressCheckEvents = true;
        try
        {
            foreach (var row in _builtInRows)
            {
                if (!states.TryGetValue(row.Def.Id, out var status))
                {
                    row.State = null;
                    row.StateText.Text = "未知";
                    row.StateText.ToolTip = null;
                    continue;
                }
                row.State = status.State;
                row.StateText.Text = FriendlyState(status.State);
                row.StateText.SetResourceReference(TextBlock.ForegroundProperty, BrushKeyOf(status.State));
                row.StateText.ToolTip = status.Tip;
                if (overwriteUserSelection)
                    row.Check.IsChecked = status.State == FxPatchEngine.PatchState.Applied;
            }
        }
        finally
        {
            _suppressCheckEvents = false;
        }

        if (overwriteUserSelection)
            _userEditedSelection = false;
    }

    private static string FriendlyState(FxPatchEngine.PatchState state) => state switch
    {
        FxPatchEngine.PatchState.Applied => "已启用",
        FxPatchEngine.PatchState.NotApplied => "未启用",
        FxPatchEngine.PatchState.Conflict => "状态异常",
        _ => "不兼容",
    };

    private static string BrushKeyOf(FxPatchEngine.PatchState state) => state switch
    {
        FxPatchEngine.PatchState.Applied => "SuccessBrush",
        FxPatchEngine.PatchState.NotApplied => "TextSecondaryBrush",
        FxPatchEngine.PatchState.Conflict => "WarningBrush",
        _ => "ErrorBrush",
    };

    private static string TooltipOf(FxPatchEngine.PatchState state, string detail) => state switch
    {
        FxPatchEngine.PatchState.Applied => "补丁已写入游戏；勾选后可执行还原或卸载。",
        FxPatchEngine.PatchState.NotApplied => "还没启用；勾选后点「启用特效补丁」即可。",
        FxPatchEngine.PatchState.Conflict => "改动只生效了一部分，或内容与补丁不一致，建议先「彻底还原游戏客户端」再重新启用。"
                                             + (detail.Length == 0 ? "" : $"\n引擎提示：{detail}"),
        _ => "当前游戏数据与这个补丁不匹配。"
             + (detail.Length == 0 ? "" : $"\n引擎提示：{detail}"),
    };

    private void SetStateHint(string text)
    {
        if (BuiltInStateHint is null)
            return;
        void Set() => BuiltInStateHint.Text = text;
        if (Dispatcher.CheckAccess())
            Set();
        else
            Dispatcher.BeginInvoke(Set);
    }

    private void BrowsePatch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "选择补丁描述文件", Filter = "补丁描述 (*.patch.json;*.json;*.zip)|*.patch.json;*.json;*.zip|全部文件 (*.*)|*.*" };
        if (dlg.ShowDialog() == true)
        {
            _pendingPatchFile = dlg.FileName;
            PatchJsonBox.Text = PendingDisplayName(dlg.FileName);
            PatchJsonBox.ToolTip = dlg.FileName;
            RenderThirdPartyList(CurrentLedgerEntries());
        }
    }

    /// <summary>当前游戏数据对应的补丁账本；没有路径时按空账本处理。</summary>
    private List<FxPatchStateStore.AppliedPatch> CurrentLedgerEntries()
    {
        var path = (_gameDataPath ?? GameDataPathPreference.Get())?.Trim();
        return string.IsNullOrWhiteSpace(path) ? [] : FxPatchStateStore.Read(path);
    }

    /// <summary>勾选的第三方补丁条目。</summary>
    private List<FxPatchStateStore.AppliedPatch> CheckedThirdParty =>
        _thirdPartyRows.Where(r => r.Check.IsChecked == true).Select(r => r.Entry).ToList();

    // ═══ 引擎调用 ═══════════════════════════════════════════════
    private bool ValidateInputs(out string gameData, bool requireSelection = true)
    {
        gameData = (_gameDataPath ?? GameDataPathPreference.Get())?.Trim() ?? string.Empty;
        if (_gameKind != PoeGameKind.Poe2)
        {
            SetStatus("特效补丁仅支持 POE2 客户端。", UiStatus.Kind.Warning);
            return false;
        }
        if (gameData.Length == 0)
        {
            SetStatus("请先选择游戏数据。", UiStatus.Kind.Warning);
            return false;
        }

        var custom = PendingFileParticipates() ? PendingPatchPath! : string.Empty;
        var builtInIds = CheckedBuiltInIds;
        var affixChecked = _affixCheck?.IsChecked == true && File.Exists(AffixPatchJsonPath);
        if (requireSelection && builtInIds.Count == 0 && custom.Length == 0 && CheckedThirdParty.Count == 0 && !affixChecked)
        {
            SetStatus("请先勾选至少一个补丁，或选择一个自定义补丁文件。", UiStatus.Kind.Warning);
            return false;
        }
        if (custom.Length > 0 && !File.Exists(custom))
        {
            SetStatus("自定义补丁文件不存在，请重新选择。", UiStatus.Kind.Warning);
            return false;
        }
        return true;
    }

    /// <summary>按当前来源组装引擎调用（可任意组合）：
    /// 输入框里的自定义补丁文件 + 勾选的内置补丁 + 勾选的第三方补丁（按账本记录的原补丁文件）。
    /// 待应用的输入框文件如果已经在第三方列表里以勾选行存在，不重复拼装。
    /// uninstallAll=true 时（卸载整包替换型补丁的连带卸载）不看勾选，改用账本里全部已启用的补丁。</summary>
    private List<(string? BuiltInId, string[] Args)> BuildInvocations(string action, bool uninstallAll = false)
    {
        var gameData = (_gameDataPath ?? GameDataPathPreference.Get())!.Trim();
        var rawPack = new List<(string? BuiltInId, string[] Args)>();
        var modifying = new List<(string? BuiltInId, string[] Args)>();

        void AddSource(string? builtInId, string sourceFile, bool knownRawPack)
        {
            // 卸载 = 完整移除一个补丁：先 revert 撤销修改，再 purge 删除新增文件。
            // 只跑 purge 会漏掉「只修改不新增」的补丁（引擎无事可做、账本也不清）；
            // 整包替换型没有 purge 语义，revert 本身就是完整卸载。
            if (action == "purge")
            {
                if (builtInId is not null)
                {
                    modifying.Add((builtInId, [gameData, builtInId, "revert"]));
                    modifying.Add((builtInId, [gameData, builtInId, "purge"]));
                }
                else if (knownRawPack)
                {
                    rawPack.Add((null, [gameData, sourceFile, "revert"]));
                }
                else
                {
                    var isRaw = IsRawPackSource(sourceFile);
                    var bucket = isRaw ? rawPack : modifying;
                    if (isRaw)
                        bucket.Add((null, [gameData, sourceFile, "revert"]));
                    else
                    {
                        bucket.Add((null, [gameData, sourceFile, "revert"]));
                        bucket.Add((null, [gameData, sourceFile, "purge"]));
                    }
                }
                return;
            }

            if (builtInId is not null)
            {
                modifying.Add((builtInId, [gameData, builtInId, action]));
                return;
            }
            var raw = knownRawPack || IsRawPackSource(sourceFile);
            (raw ? rawPack : modifying).Add((null, [gameData, sourceFile, action]));
        }

        // 连带卸载全部补丁：来源从「勾选的」换成「账本里全部已启用的」（内置 + 第三方）。
        var ledger = uninstallAll ? CurrentLedgerEntries() : null;
        var checkedThirdParty = (ledger is not null
                ? ledger.Where(e => !string.Equals(e.Kind, FxPatchStateStore.KindBuiltIn, StringComparison.OrdinalIgnoreCase))
                : CheckedThirdParty)
            .Where(e => !string.IsNullOrWhiteSpace(e.SourceFile) && File.Exists(e.SourceFile))
            .ToList();
        // 输入框文件只有「列表里没有代表它的行」（刚选完文件，保持选完直接启用）
        // 或「代表它的行被勾选」（此时上面的勾选第三方已带上，无需重复拼）时才参与执行。
        // 行存在但未勾选 = 用户明确不要它参与——此前这里无视勾选无条件拼入，
        // 取消勾选形同虚设，刚卸载的补丁也会被悄悄打回去（2026-09-16 用户实测踩坑）。
        var custom = PendingPatchPath ?? string.Empty;
        if (custom.Length > 0 && FindThirdPartyRowBySource(custom) is null)
            AddSource(null, custom, knownRawPack: false);

        var builtInIds = ledger is not null
            ? ledger.Where(e => string.Equals(e.Kind, FxPatchStateStore.KindBuiltIn, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : CheckedBuiltInIds;
        foreach (var id in builtInIds)
            AddSource(id, id, knownRawPack: false);

        foreach (var entry in checkedThirdParty)
            AddSource(null, entry.SourceFile!,
                knownRawPack: string.Equals(entry.Kind, FxPatchStateStore.KindRawPack, StringComparison.OrdinalIgnoreCase));

        // 内置区的「内置词缀修改补丁」固定行：执行走词缀补丁 json 通道，固定排在修改类最后
        //（顺序引导的最后一步：先打完特效 / 整包补丁，最后词缀修改）。
        // uninstallAll 的账本分支已含词缀条目（上面循环拼过），这里只在按勾选拼装时补，避免重复执行。
        if (!uninstallAll && _affixCheck?.IsChecked == true && File.Exists(AffixPatchJsonPath))
            AddSource(null, AffixPatchJsonPath, knownRawPack: false);

        // 防御性兜底：整包替换型一次只能执行一个，多出的跳过（正常情况勾选层已互斥，走不到这里）。
        if (rawPack.Count > 1)
        {
            SetStatus("整包替换型补丁一次只能执行一个，本次只执行第一个，其余已跳过；请逐个操作。", UiStatus.Kind.Warning);
            rawPack.RemoveRange(1, rawPack.Count - 1);
        }

        // 顺序有讲究：整包替换型会整体换掉 _.index.bin，必须先于修改类执行，否则先改的文件被冲掉。
        // 卸载同样是整包先行：它备份的索引快照里带着其他补丁 PATCHED bundle 的引用，
        // 若先卸载其他补丁（bundle 被删）再还原快照，索引就会引用已删除的 bundle；
        // 先还原快照（此刻 bundle 还在，快照自洽），再逐个 revert+purge，最终索引才干净。
        return rawPack.Concat(modifying).ToList();
    }

    /// <summary>判断一个补丁文件是不是整包替换型（zip 里带 _.index.bin）。
    /// 账本里已有 Kind 的直接用 Kind；待应用的 zip 打开看一眼。打不开就当普通补丁，让引擎去报错。</summary>
    private static bool IsRawPackSource(string sourceFile)
    {
        if (!sourceFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false; // .patch.json 一定是走索引 op 的普通补丁
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(sourceFile);
            return zip.Entries.Any(e =>
                e.FullName.Equals("_.index.bin", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("/_.index.bin", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>勾选的第三方补丁里有多少条缺原补丁文件（旧记录或文件已不在）；有则提示，这类无法被操作。
    /// uninstallAll=true 时改为检查账本里全部第三方条目（连带卸载的来源不是勾选）。</summary>
    private void WarnMissingThirdPartySource(string action, bool uninstallAll = false)
    {
        var source = uninstallAll
            ? CurrentLedgerEntries().Where(e =>
                !string.Equals(e.Kind, FxPatchStateStore.KindBuiltIn, StringComparison.OrdinalIgnoreCase))
            : CheckedThirdParty;
        var missing = source
            .Where(e => string.IsNullOrWhiteSpace(e.SourceFile) || !File.Exists(e.SourceFile))
            .ToList();
        if (missing.Count > 0)
            SetStatus($"有 {missing.Count} 个第三方补丁记录里没有可用的原补丁文件（旧记录或文件已移动），无法被{action}，将跳过：" +
                      string.Join("、", missing.Select(e => e.Name)), UiStatus.Kind.Warning);
    }

    private async void Status_Click(object sender, RoutedEventArgs e)
    {
        var custom = PendingPatchPath ?? string.Empty;
        if (custom.Length > 0)
        {
            // 自定义补丁：仍然走引擎明细输出（记录里只有 id，没有逐项状态）。
            if (!ValidateInputs(out _, requireSelection: false))
                return;
            await RunEngineAsync("刷新补丁状态", BuildInvocations("status"));
            return;
        }

        if (!ValidateInputs(out _, requireSelection: false))
            return;
        await RescanAsync(quiet: false);
    }

    /// <summary>真的打开索引核对一遍（要几秒），按结果校正补丁记录，再刷新界面与勾选。
    /// quiet=true 时只更新界面，不往输出面板写东西（模块首次打开时建账本用）。</summary>
    private async Task RescanAsync(bool quiet)
    {
        if (_refreshing)
        {
            if (!quiet)
                SetStateHint("上一次核对还在进行中（打开游戏数据需要一些时间），请稍候再点刷新。");
            return;
        }

        var path = (_gameDataPath ?? GameDataPathPreference.Get())?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;

        // 别的模块正在动游戏数据：这次不抢，先按账本显示，下次再核对。
        if (FxEngineRunner.IsBusy)
        {
            LoadRecordedPatches(path, overwriteUserSelection: !_userEditedSelection);
            return;
        }

        _refreshing = true;
        try
        {
            if (!quiet)
                SetStateHint("正在核对补丁状态（要打开游戏数据，通常需要十几秒到一分钟）……");
            var statuses = await Task.Run(() => FxPatchEngine.QueryBuiltInStatus(path));
            if ((_gameDataPath ?? GameDataPathPreference.Get())?.Trim() != path)
                return;

            // 账本以扫描结果为准：内置补丁按实际状态重写，自定义补丁的记录原样保留。
            var ledger = FxPatchStateStore.Read(path)
                .Where(e => !string.Equals(e.Kind, FxPatchStateStore.KindBuiltIn, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var s in statuses)
            {
                if (s.State != FxPatchEngine.PatchState.Applied)
                    continue;
                ledger.Add(new FxPatchStateStore.AppliedPatch(
                    s.Id, s.DisplayName, FxPatchStateStore.KindBuiltIn, null, DateTimeOffset.UtcNow));
            }
            FxPatchStateStore.SaveAll(path, ledger);

            var entries = FxPatchStateStore.Read(path);
            var states = new Dictionary<string, (FxPatchEngine.PatchState State, string Tip)>();
            foreach (var s in statuses)
                states[s.Id] = (s.State, TooltipOf(s.State, s.Detail));
            ApplyStates(states, overwriteUserSelection: true);
            _statusStale = false;
            _statusLoadedPath = path;
            // 词缀行不在 _builtInRows 里（它走 fx-patch 通道），ApplyStates 覆盖不到：
            // 必须单独按账本刷新，否则「刷新补丁状态」之后这一行会停在旧状态、勾也不回正。
            // 点按钮是显式刷新，所以这里覆盖用户的手动勾选。
            UpdateAffixRow(entries, overwriteUserSelection: true);
            RenderThirdPartyList(entries);
            SetStateHint(BuiltInSummaryHint());

            if (quiet)
            {
                _statusStale = false;
                _statusLoadedPath = path;
                LoadRecordedPatches(path, overwriteUserSelection: !_userEditedSelection);
                return;
            }

            Output.ClearLog();
            AppendLog("内置补丁状态（已打开游戏数据核对）：");
            var applied = 0;
            foreach (var row in _builtInRows)
            {
                var mark = row.State == FxPatchEngine.PatchState.Applied ? "☑" : row.State is null ? "?" : "☐";
                if (row.State == FxPatchEngine.PatchState.Applied)
                    applied++;
                AppendLog($"  {mark} {row.Def.DisplayName}（{row.Def.Id}）—— {row.StateText.Text}");
            }
            // 「内置词缀修改补丁」固定在内置区、但不进 _builtInRows，单独补一行：
            // 否则核对结果里缺它，用户明明启用过却在输出里找不到。
            var affixMark = _affixState == FxPatchEngine.PatchState.Applied ? "☑"
                : _affixState is null ? "?" : "☐";
            if (_affixState == FxPatchEngine.PatchState.Applied)
                applied++;
            AppendLog($"  {affixMark} 内置词缀修改补丁（{AffixPatchBuilder.PatchId}）—— {_affixStateText?.Text ?? "待检测"}");
            AppendLog("");
            AppendLog(applied == 0 ? "没有已启用的内置补丁。" : $"已启用 {applied} 个，已自动勾选。");
            SetStatus("✅ 已核对补丁状态。", UiStatus.Kind.Success);
        }
        catch (Exception ex)
        {
            FileLogger.App.Error("UI 核对补丁状态失败。", ex);
            SetStateHint("核对补丁状态失败：" + ex.Message);
            if (!quiet)
                SetStatus("❌ 核对补丁状态失败：" + ex.Message, UiStatus.Kind.Error);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out _))
            return;

        // 引导：整包替换型补丁会整体换掉 _.index.bin，先于此应用的其他补丁（词缀上色、
        // 技能特效等）的内容会被冲掉。这是有实际代价的操作，用弹窗确认而不是状态栏一闪而过。
        var appliesRawPack = CheckedThirdParty.Any(e =>
            string.Equals(e.Kind, FxPatchStateStore.KindRawPack, StringComparison.OrdinalIgnoreCase))
            || (PendingFileParticipates() && IsRawPackSource(PendingPatchPath!));
        if (appliesRawPack)
        {
            var affixNote = _affixState == FxPatchEngine.PatchState.Applied
                ? "\n\n当前已启用词缀修改补丁：应用本补丁后它会失效，请在完成后到「词缀上色」重新点「应用词缀修改」。"
                : "\n\n建议顺序：先应用普通补丁和整包替换型补丁，最后再到「词缀上色」应用词缀修改。";
            var confirm = MessageBox.Show(
                "勾选的补丁里包含整包替换型补丁——它会整体替换游戏索引（_.index.bin）。\n\n" +
                "这可能覆盖已启用的其他补丁写入的内容（包括但不限于词缀上色、技能特效等），" +
                "被覆盖的补丁会显示失效，需要重新应用。" + affixNote + "\n\n确定继续？",
                "启用整包替换型补丁", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
                return;
        }

        await RunEngineAsync("启用特效补丁", BuildInvocations("apply"));
        RefreshAfterOperation();
    }

    /// <summary>彻底还原游戏客户端 = 整体还原官方原版：先按备份清单还原整包替换型补丁覆盖的原生文件，
    /// 再基线索引替换 + 删除全部补丁新增文件（含第三方补丁）。
    /// 与逐补丁的「卸载已勾选补丁」相对；不可逆（想再用要重新启用补丁），所以用红色警示 + 二次确认。</summary>
    private async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out _, requireSelection: false))
            return;
        var confirm = MessageBox.Show(
            "将把游戏彻底还原成官方原版：整包替换型补丁（如汉化包）覆盖的原生文件会按备份清单放回，再用原版索引整体替换当前索引，并删除所有补丁新增的文件——包括第三方补丁，全部补丁记录一并清空。\n\n之后想再用任何补丁，都需要重新启用。确定继续？\n（只想移除个别补丁的话，请用「卸载已勾选补丁」。）",
            "彻底还原游戏客户端", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        var gameData = (_gameDataPath ?? GameDataPathPreference.Get())!.Trim();
        // restore 是内置通道的动作（BuiltInId 不能为 null，否则会被当成自定义补丁路径解析）。
        await RunEngineAsync("彻底还原游戏客户端",
            [(FxPatchEngine.BuiltInAll, new[] { gameData, "restore" })]);
        RefreshAfterOperation();
    }

    private async void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out _))
            return;

        // 整包替换型补丁（zip 带 _.index.bin）修改了游戏索引本体：卸载它 = 索引整体换回原版，
        // 其他所有补丁都会因此失效，所以要连带卸载全部补丁，并在弹窗里明说。
        // 不含索引的普通补丁没有这个问题，仍可单独卸载。
        var involvesRawPack = CheckedThirdParty.Any(e =>
            string.Equals(e.Kind, FxPatchStateStore.KindRawPack, StringComparison.OrdinalIgnoreCase));
        var uninstallAll = false;
        if (involvesRawPack)
        {
            var confirm = MessageBox.Show(
                "勾选的补丁里包含整包替换型补丁——它修改了游戏索引（_.index.bin）。\n\n" +
                "卸载它会把索引整体换回官方原版，其他所有已启用的补丁都会因此失效，" +
                "所以本次将连带卸载全部补丁，而不是只卸载勾选的那一个。\n\n" +
                "不含索引的普通补丁不受此限制，随时可以单独卸载。\n\n确定继续？",
                "卸载整包替换型补丁", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
                return;
            uninstallAll = true;
        }
        else
        {
            var confirm = MessageBox.Show(
                "将完整卸载勾选的补丁：撤销它们对游戏文件的修改，并删除补丁新增的文件；其他未勾选的补丁不受影响。\n\n卸载之后想再用，需要重新应用补丁。确定继续？\n（想把游戏整体还原成官方原版、移除所有补丁的话，请用「彻底还原游戏客户端」。）",
                "卸载已勾选补丁", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
                return;
        }
        WarnMissingThirdPartySource("卸载", uninstallAll);
        // 记下本轮真正卸载的第三方来源（uninstallAll 的账本快照必须在引擎清空账本之前取）。
        // 卸载完成后若输入框还指向其中之一，清空它：否则「待应用」条目会以勾选状态复活，
        // 下一次启用会把刚卸载的补丁悄悄打回去（2026-09-16 用户实测踩坑）。
        var purgedSources = (uninstallAll
                ? CurrentLedgerEntries().Where(e => !string.Equals(e.Kind, FxPatchStateStore.KindBuiltIn, StringComparison.OrdinalIgnoreCase))
                : CheckedThirdParty)
            .Where(e => !string.IsNullOrWhiteSpace(e.SourceFile) && File.Exists(e.SourceFile))
            .Select(e => Path.GetFullPath(e.SourceFile!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invocations = BuildInvocations("purge", uninstallAll);
        if (invocations.Count == 0)
        {
            SetStatus("勾选的补丁都没有可用的执行方式（第三方补丁缺原补丁文件），请先重新应用一次补齐记录。", UiStatus.Kind.Warning);
            return;
        }
        await RunEngineAsync(uninstallAll ? "卸载全部补丁（连带整包替换型）" : "卸载已勾选补丁", invocations);
        if (_pendingPatchFile is { } pending && File.Exists(pending) && purgedSources.Contains(Path.GetFullPath(pending)))
        {
            _pendingPatchFile = null;
            PatchJsonBox.Text = string.Empty;
            PatchJsonBox.ToolTip = null;
        }
        RefreshAfterOperation();
    }

    /// <summary>写操作（启用 / 还原 / 卸载）完成后刷新：引擎已顺手更新补丁记录，
    /// 直接重读这个小 json 即可，不再打开游戏数据核对（那要几秒到分钟级，还会卡住界面提示）。</summary>
    private void RefreshAfterOperation()
    {
        _statusStale = false;
        var path = (_gameDataPath ?? GameDataPathPreference.Get())?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;
        _statusLoadedPath = path;
        LoadRecordedPatches(path, overwriteUserSelection: true);
    }

    /// <summary>顺序执行一组引擎调用（内置补丁多选时逐个跑），汇总成败写入状态栏。</summary>
    private Task<bool> RunEngineAsync(string action, List<(string? BuiltInId, string[] Args)> invocations)
        => FxEngineRunner.RunAsync(
            action,
            invocations.Select(i => new FxEngineRunner.Invocation(i.BuiltInId, i.Args)).ToList(),
            gameDataPath: _gameDataPath,
            skipGameData: false,
            clearLog: Output.ClearLog,
            appendLog: AppendLog,
            setStatus: SetStatus,
            setBusy: SetButtonsEnabled);

    private void AppendLog(string line) => Output.AppendLog(line);

    private void SetStatus(string text, UiStatus.Kind kind = UiStatus.Kind.Neutral)
        => Output.SetStatus(text, kind);

    private void SetButtonsEnabled(bool enabled)
    {
        void Set()
        {
            StatusButton.IsEnabled = enabled;
            ApplyButton.IsEnabled = enabled;
            RevertButton.IsEnabled = enabled;
            PurgeButton.IsEnabled = enabled;
        }
        if (Dispatcher.CheckAccess())
            Set();
        else
            Dispatcher.Invoke(Set);
    }
}
