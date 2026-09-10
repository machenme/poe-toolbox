using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoEToolbox.Plugins.BagCleaner.Models;
using PoEToolbox.Plugins.BagCleaner.Services;

namespace PoEToolbox.Plugins.BagCleaner.ViewModels;

/// <summary>
/// 主配置窗口 ViewModel (CommunityToolkit.Mvvm 源生成器)。
///
/// 设计要点：
/// 1. 字段用 [ObservableProperty] 自动生成 INPC 通知
/// 2. 命令用 [RelayCommand] 自动生成 ICommand
/// 3. 所有 VM 修改都在内存中的 _config 上，Save 时一次性写盘
/// 4. 文本框类型用 string 包装 int 避免绑定类型不匹配 (简化 XAML)
/// 5. 左侧导航通过 CurrentNavIndex (0-4) 控制面板可见性
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IConfigService _config;
    private readonly IHotkeyService _hotkey;

    // ---- 左侧导航 (0=按键设置 1=仓库筛选 2=校准坐标 3=延迟设置 4=进程名称) ----
    [ObservableProperty] private int _currentNavIndex = 0;

    // ---- 热键 ----
    [ObservableProperty] private int _triggerKey;
    [ObservableProperty] private string _triggerKeyDisplay = "";
    [ObservableProperty] private int _stopKey;
    [ObservableProperty] private string _stopKeyDisplay = "";
    [ObservableProperty] private int _calibrateKey;
    [ObservableProperty] private string _calibrateKeyDisplay = "";
    [ObservableProperty] private bool _useCtrl;
    [ObservableProperty] private bool _useAlt;
    [ObservableProperty] private bool _useShift;
    [ObservableProperty] private bool _useWin;

    // ---- 网格 (校准后只读显示) ----
    [ObservableProperty] private string _startX = "";
    [ObservableProperty] private string _startY = "";
    [ObservableProperty] private string _stepX = "";
    [ObservableProperty] private string _stepY = "";
    [ObservableProperty] private string _columns = "12";
    [ObservableProperty] private string _rows = "5";

    // ---- 仓库筛选 (60 格跳过矩阵) ----
    [ObservableProperty] private ObservableCollection<SkipCell> _skipCells = new();

    // ---- 反检测 ----
    [ObservableProperty] private string _intervalMin = "40";
    [ObservableProperty] private string _intervalMax = "60";
    [ObservableProperty] private string _offsetMin = "-3";
    [ObservableProperty] private string _offsetMax = "3";

    // ---- POE 进程名 ----
    [ObservableProperty] private string _poeProcesses = "";

    // ---- 状态栏 ----
    public enum SaveStatus { Clean, Dirty, Saved, Error }
    [ObservableProperty] private SaveStatus _status = SaveStatus.Clean;
    [ObservableProperty] private string _statusMessage = "未改动";
    [ObservableProperty] private string _statusColor = "#A0A0A0"; // 灰

    public MainViewModel(IConfigService config, IHotkeyService hotkey)
    {
        _config = config;
        _hotkey = hotkey;
        LoadFromConfig();
    }

    public void LoadFromConfig()
    {
        var c = _config.Current;
        TriggerKey = c.Hotkey.TriggerKey;
        TriggerKeyDisplay = BuildKeyDisplay(c.Hotkey.TriggerKey, c.Hotkey.TriggerModifier);
        CalibrateKey = c.Hotkey.CalibrateKey;
        CalibrateKeyDisplay = KeyName(c.Hotkey.CalibrateKey);
        StopKey = c.Hotkey.StopKey;
        StopKeyDisplay = KeyName(c.Hotkey.StopKey);
        UseCtrl = (c.Hotkey.TriggerModifier & 0x0002) != 0;
        UseAlt = (c.Hotkey.TriggerModifier & 0x0001) != 0;
        UseShift = (c.Hotkey.TriggerModifier & 0x0004) != 0;
        UseWin = (c.Hotkey.TriggerModifier & 0x0008) != 0;

        // V2 方案：StartX/Y/StepX/StepY 改为 double 比例 (0.0~1.0)，UI 显示为百分比
        StartX = FormatPercent(c.Grid.StartX);
        StartY = FormatPercent(c.Grid.StartY);
        StepX = FormatPercent(c.Grid.StepX);
        StepY = FormatPercent(c.Grid.StepY);
        Columns = c.Grid.Columns.ToString();
        Rows = c.Grid.Rows.ToString();

        // 仓库筛选：加载跳过矩阵
        LoadSkipCells(c.Grid);

        IntervalMin = c.AntiDetection.ClickIntervalMinMs.ToString();
        IntervalMax = c.AntiDetection.ClickIntervalMaxMs.ToString();
        OffsetMin = c.AntiDetection.PositionOffsetMinPx.ToString();
        OffsetMax = c.AntiDetection.PositionOffsetMaxPx.ToString();

        PoeProcesses = string.Join(", ", c.PoeProcessNames);

        SetStatus(SaveStatus.Clean, "已加载最新配置");
    }

    private void LoadSkipCells(GridConfig grid)
    {
        var total = grid.Columns * grid.Rows;
        var mask = grid.SkipMask;
        SkipCells.Clear();
        for (int i = 0; i < total; i++)
        {
            var skipped = mask != null && i < mask.Length && mask[i];
            var cell = new SkipCell(i, skipped);
            cell.PropertyChanged += (_, _) => MarkDirty();
            SkipCells.Add(cell);
        }
    }

    [RelayCommand]
    private void Save(Window? owner)
    {
        // 1. 解析并验证
        // 热键互斥检查：热键必须各不相同
        var triggerName = KeyName(TriggerKey);
        var calibrateName = KeyName(CalibrateKey);
        var stopName = KeyName(StopKey);
        if (TriggerKey == CalibrateKey)
        {
            SetError($"触发清包 ({triggerName}) 和校准标记 ({calibrateName}) 不能是同一个键");
            return;
        }
        if (TriggerKey == StopKey)
        {
            SetError($"触发清包 ({triggerName}) 和紧急停止 ({stopName}) 不能是同一个键");
            return;
        }
        if (CalibrateKey == StopKey)
        {
            SetError($"校准标记 ({calibrateName}) 和紧急停止 ({stopName}) 不能是同一个键");
            return;
        }
        if (!TryParseUInt(IntervalMin, out int intervalMin) || intervalMin < 1)
        {
            SetError("点击间隔最小值无效 (必须 ≥ 1)");
            return;
        }
        if (!TryParseUInt(IntervalMax, out int intervalMax) || intervalMax < intervalMin)
        {
            SetError("点击间隔最大值无效 (必须 ≥ 最小值)");
            return;
        }
        if (!TryParseInt(OffsetMin, out int offsetMin) || offsetMin < -50 || offsetMin > 50)
        {
            SetError("像素偏移最小值无效 (范围 [-50, 50])");
            return;
        }
        if (!TryParseInt(OffsetMax, out int offsetMax) || offsetMax < offsetMin || offsetMax > 50)
        {
            SetError("像素偏移最大值无效 (范围 [min, 50])");
            return;
        }
        if (!TryParseUInt(Columns, out int cols) || cols < 1 || cols > 30)
        {
            SetError("列数无效 (范围 [1, 30])");
            return;
        }
        if (!TryParseUInt(Rows, out int rows) || rows < 1 || rows > 20)
        {
            SetError("行数无效 (范围 [1, 20])");
            return;
        }

        // 2. 写入配置对象
        var c = _config.Current;
        c.Hotkey.TriggerKey = TriggerKey;
        c.Hotkey.TriggerModifier = (UseCtrl ? 0x0002u : 0) | (UseAlt ? 0x0001u : 0) | (UseShift ? 0x0004u : 0) | (UseWin ? 0x0008u : 0);
        c.Hotkey.CalibrateKey = CalibrateKey;
        c.Hotkey.StopKey = StopKey;

        c.Grid.Columns = cols;
        c.Grid.Rows = rows;
        // StartX/Y/StepX/StepY 由校准写入，此处不覆盖 (避免误清零)

        // 写入仓库筛选跳过矩阵
        c.Grid.SkipMask = new bool[SkipCells.Count];
        for (int i = 0; i < SkipCells.Count; i++)
            c.Grid.SkipMask[i] = SkipCells[i].IsSkipped;

        c.AntiDetection.ClickIntervalMinMs = intervalMin;
        c.AntiDetection.ClickIntervalMaxMs = intervalMax;
        c.AntiDetection.PositionOffsetMinPx = offsetMin;
        c.AntiDetection.PositionOffsetMaxPx = offsetMax;

        // 进程名解析
        var processes = PoeProcesses
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (processes.Count > 0) c.PoeProcessNames = processes;

        // 3. 持久化
        try
        {
            _config.SaveConfig(c);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainViewModel] 保存失败: {ex.Message}");
            SetError("保存失败：" + ex.Message);
            return;
        }

        // 4. 重新注册热键 (如果热键有变化)
        try
        {
            _hotkey.UnregisterAll();
            _hotkey.RegisterWithoutStopKeyAsync(c.Hotkey.TriggerKey, c.Hotkey.TriggerModifier, c.Hotkey.CalibrateKey)
                .GetAwaiter().GetResult();
        }
        catch (HotkeyRegistrationException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainViewModel] 热键重新注册失败: {ex.Message}");
            SetError("热键注册失败：" + ex.Message);
            return;
        }

        SetStatus(SaveStatus.Saved, "已保存");
    }

    /// <summary>Auto-save without UI feedback (no message boxes, no error status).</summary>
    public void AutoSave()
    {
        if (_captureActive) return; // don't save during hotkey capture

        // Skip validation errors silently — only save if all values are valid
        if (TriggerKey == CalibrateKey || TriggerKey == StopKey || CalibrateKey == StopKey) return;
        if (!TryParseUInt(IntervalMin, out int intervalMin) || intervalMin < 1) return;
        if (!TryParseUInt(IntervalMax, out int intervalMax) || intervalMax < intervalMin) return;
        if (!TryParseInt(OffsetMin, out int offsetMin) || offsetMin < -50 || offsetMin > 50) return;
        if (!TryParseInt(OffsetMax, out int offsetMax) || offsetMax < offsetMin || offsetMax > 50) return;
        if (!TryParseUInt(Columns, out int cols) || cols < 1 || cols > 30) return;
        if (!TryParseUInt(Rows, out int rows) || rows < 1 || rows > 20) return;

        var c = _config.Current;
        c.Hotkey.TriggerKey = TriggerKey;
        c.Hotkey.TriggerModifier = (UseCtrl ? 0x0002u : 0) | (UseAlt ? 0x0001u : 0) | (UseShift ? 0x0004u : 0) | (UseWin ? 0x0008u : 0);
        c.Hotkey.CalibrateKey = CalibrateKey;
        c.Hotkey.StopKey = StopKey;
        c.Grid.Columns = cols;
        c.Grid.Rows = rows;
        c.Grid.SkipMask = new bool[SkipCells.Count];
        for (int i = 0; i < SkipCells.Count; i++)
            c.Grid.SkipMask[i] = SkipCells[i].IsSkipped;
        c.AntiDetection.ClickIntervalMinMs = intervalMin;
        c.AntiDetection.ClickIntervalMaxMs = intervalMax;
        c.AntiDetection.PositionOffsetMinPx = offsetMin;
        c.AntiDetection.PositionOffsetMaxPx = offsetMax;
        var processes = PoeProcesses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (processes.Count > 0) c.PoeProcessNames = processes;

        try { _config.SaveConfig(c); } catch { return; }
        try
        {
            _hotkey.UnregisterAll();
            _hotkey.RegisterWithoutStopKeyAsync(c.Hotkey.TriggerKey, c.Hotkey.TriggerModifier, c.Hotkey.CalibrateKey)
                .GetAwaiter().GetResult();
        }
        catch { return; }

        SetStatus(SaveStatus.Saved, "已自动保存");
    }

    private bool _captureActive;
    public void SetCaptureActive(bool active) => _captureActive = active;

    [RelayCommand]
    private void Reset()
    {
        TriggerKey = 0x71; // F2
        UseCtrl = false; UseAlt = false; UseShift = false; UseWin = false;
        TriggerKeyDisplay = "F2";
        StopKey = 0x1B;    // Esc
        StopKeyDisplay = "Esc";
        CalibrateKey = 0x72; // F3
        CalibrateKeyDisplay = "F3";
        IntervalMin = "40"; IntervalMax = "60";
        OffsetMin = "-3"; OffsetMax = "3";
        MarkDirty();
    }

    [RelayCommand]
    private void SelectAllSkip()
    {
        foreach (var c in SkipCells) c.IsSkipped = true;
    }

    [RelayCommand]
    private void ClearAllSkip()
    {
        foreach (var c in SkipCells) c.IsSkipped = false;
    }

    [RelayCommand]
    private void StartCalibration()
    {
        // 主窗口打开时调"校准"：主窗口要隐藏 (校准期间 F3 是标记键)
        BagCleanerContext.Calibrator.Begin();
        SetStatus(SaveStatus.Dirty, "校准进行中：请把鼠标移到第 1 格中心，按 F3 标记");
    }

    // 任何字段变化都触发 MarkDirty
    partial void OnTriggerKeyChanged(int value) { TriggerKeyDisplay = BuildKeyDisplay(value, BuildModifier()); MarkDirty(); }
    partial void OnCalibrateKeyChanged(int value) => MarkDirty();
    partial void OnStopKeyChanged(int value) => MarkDirty();
    partial void OnUseCtrlChanged(bool value)  { TriggerKeyDisplay = BuildKeyDisplay(TriggerKey, BuildModifier()); MarkDirty(); }
    partial void OnUseAltChanged(bool value)   { TriggerKeyDisplay = BuildKeyDisplay(TriggerKey, BuildModifier()); MarkDirty(); }
    partial void OnUseShiftChanged(bool value) { TriggerKeyDisplay = BuildKeyDisplay(TriggerKey, BuildModifier()); MarkDirty(); }
    partial void OnUseWinChanged(bool value)   { TriggerKeyDisplay = BuildKeyDisplay(TriggerKey, BuildModifier()); MarkDirty(); }
    partial void OnIntervalMinChanged(string value) => MarkDirty();
    partial void OnIntervalMaxChanged(string value) => MarkDirty();
    partial void OnOffsetMinChanged(string value) => MarkDirty();
    partial void OnOffsetMaxChanged(string value) => MarkDirty();
    partial void OnColumnsChanged(string value) => MarkDirty();
    partial void OnRowsChanged(string value) => MarkDirty();
    partial void OnPoeProcessesChanged(string value) => MarkDirty();

    private void MarkDirty()
    {
        SetStatus(SaveStatus.Dirty, "有未保存的修改");
    }

    private void SetStatus(SaveStatus s, string msg)
    {
        Status = s;
        StatusMessage = msg;
        StatusColor = s switch
        {
            SaveStatus.Clean => "#A0A0A0",
            SaveStatus.Dirty => "#CA8A04",
            SaveStatus.Saved => "#107C10",
            SaveStatus.Error => "#C4314B",
            _ => "#A0A0A0"
        };
    }

    private void SetError(string msg) => SetStatus(SaveStatus.Error, msg);

    // ============== 工具方法 ==============

    public void SetTriggerKeyFromInput(int vk, string display)
    {
        TriggerKey = vk;
        TriggerKeyDisplay = BuildKeyDisplay(vk, BuildModifier());
    }

    public void SetStopKeyFromInput(int vk, string display)
    {
        StopKey = vk;
        StopKeyDisplay = display;
    }

    public void SetCalibrateKeyFromInput(int vk, string display)
    {
        CalibrateKey = vk;
        CalibrateKeyDisplay = display;
    }

    private string BuildKeyDisplay(int vk, uint modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private uint BuildModifier() =>
        (UseCtrl ? 0x0002u : 0) | (UseAlt ? 0x0001u : 0) | (UseShift ? 0x0004u : 0) | (UseWin ? 0x0008u : 0);

    private static string KeyName(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)('A' + vk - 0x41)).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)('0' + vk - 0x30)).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);
        return vk switch
        {
            0x1B => "Esc", 0x09 => "Tab", 0x20 => "Space", 0x08 => "Backspace",
            0x0D => "Enter", 0x2E => "Delete", 0x2D => "Insert",
            0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
            0x26 => "↑", 0x28 => "↓", 0x25 => "←", 0x27 => "→",
            _ => $"VK 0x{vk:X2}"
        };
    }

    /// <summary>double (0.0~1.0) → 百分比字符串 (如 "50.5%")，UI 显示用。</summary>
    private static string FormatPercent(double v) =>
        (v * 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static bool TryParseInt(string s, out int v) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out v);

    private static bool TryParseUInt(string s, out int v) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out v) && v >= 0;
}
