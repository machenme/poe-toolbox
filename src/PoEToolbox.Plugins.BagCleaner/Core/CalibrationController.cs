using PoEToolbox.Plugins.BagCleaner.Models;
using PoEToolbox.Shared;
using PoEToolbox.Plugins.BagCleaner.Services;
using static PoEToolbox.Plugins.BagCleaner.Input.NativeMethods;

namespace PoEToolbox.Plugins.BagCleaner.Core;

public enum CalibrationState
{
    /// <summary>未在校准流程中，F2 走清包触发。</summary>
    Idle,

    /// <summary>等待用户在第 1 格中心按 F3 标记。</summary>
    WaitingFirst,

    /// <summary>等待用户在最后 1 格中心按 F3 标记 (用于算 stepX/Y)。</summary>
    WaitingSecond
}

/// <summary>
/// 交互式坐标校准 (V2 方案 — POE ClientArea 相对坐标)。
///
/// 关键设计 — 状态与 POE 窗口解耦：
/// - Begin() 不要求 POE 前台，直接进入 WaitingFirst (避免"托盘点校准时 POE 不在前台"导致静默失败)
/// - TryCaptureCurrentPoint() 在 F3 触发时实时获取 POE 窗口句柄 (此时用户已在 POE 中按 F3)
/// - 句柄为空 → 气泡提示用户切到 POE，状态保持不变
///
/// 流程：
/// 1. 用户托盘菜单"校准坐标" → Begin() → 状态变 WaitingFirst
/// 2. 用户切到 POE + 打开仓库 + 把鼠标移到第 1 格中心 → 按 F3
/// 3. 第 1 次 F3 → GridCalculator.CaptureRelativePoint 捕获 (relX, relY)
/// 4. 第 2 次 F3 → 同样捕获 (relX2, relY2)，计算 StepX/Y + 写配置
///
/// 算法：
///   StartX = p1.relX, StartY = p1.relY
///   StepX  = (p2.relX - p1.relX) / (Columns - 1)
///   StepY  = (p2.relY - p1.relY) / (Rows - 1)
/// </summary>
public sealed class CalibrationController
{
    private readonly IConfigService _config;
    private readonly IPoeDetector _poe;
    private readonly FileLogger _log;

    private CalibrationState _state = CalibrationState.Idle;
    private (double relX, double relY)? _firstPoint;

    public CalibrationState State => _state;

    /// <summary>状态变化事件 (App.xaml.cs 订阅 → 显示气泡通知 + 托盘 tooltip)。</summary>
    public event Action<CalibrationState>? StateChanged;

    /// <summary>校准完成事件 (App.xaml.cs 订阅 → 显示"已保存"气泡 + 详细参数)。</summary>
    public event Action<CalibrationCompletedInfo>? Completed;

    /// <summary>校准态 F3 但未取到点 (POE 不在前台 / 鼠标在 POE 外) — App 显示"请切到 POE"气泡。</summary>
    public event Action? CaptureFailedNeedPoeForeground;

    public CalibrationController(IConfigService config, IPoeDetector poe, FileLogger log)
    {
        _config = config;
        _poe = poe;
        _log = log;
    }

    public void Begin()
    {
        if (_state != CalibrationState.Idle)
        {
            _log.Warn($"[Calibration] 已在校准流程中 ({_state})，忽略重复开始");
            return;
        }

        _firstPoint = null;
        Transition(CalibrationState.WaitingFirst);
        _log.Info("[Calibration] 进入校准：请把鼠标移到背包【左上第 1 格】中心，按 F3 标记 (POE 窗口需在前台)");
    }

    public void Cancel()
    {
        if (_state == CalibrationState.Idle) return;
        _log.Info($"[Calibration] 用户取消校准 (was {_state})");
        _firstPoint = null;
        Transition(CalibrationState.Idle);
    }

    /// <summary>
    /// 在校准态下被 F3 调用：实时拿 POE 窗口 → 捕获当前光标位置 → POE 客户区比例。
    /// </summary>
    /// <returns>true 表示本次调用完成了整个校准流程并已写入配置。</returns>
    public bool TryCaptureCurrentPoint()
    {
        if (_state == CalibrationState.Idle) return false;

        // 实时拿 POE 窗口 (用户在 POE 中按 F3 时，前台就是 POE)
        var poeHwnd = _poe.GetPoeForegroundWindow();
        if (poeHwnd == IntPtr.Zero)
        {
            _log.Warn("[Calibration] F3 触发但 POE 窗口不在前台，请切到 POE 再按 F3");
            CaptureFailedNeedPoeForeground?.Invoke();
            return false;
        }

        var captured = GridCalculator.CaptureRelativePoint(poeHwnd);
        if (captured is not { } p)
        {
            _log.Error("[Calibration] 捕获光标相对位置失败");
            return false;
        }

        if (_state == CalibrationState.WaitingFirst)
        {
            if (!GridCalculator.TryCaptureWindowSize(poeHwnd, out var w, out var h))
            {
                _log.Warn("[Calibration] 获取 POE 客户区尺寸失败");
                return false;
            }
            _log.Info($"[Calibration] 校准基于 POE 客户区尺寸 = ({w} x {h})");

            _firstPoint = p;
            _log.Info($"[Calibration] 标记第 1 格 = ({p.relX:F4}, {p.relY:F4})");
            Transition(CalibrationState.WaitingSecond);
            return false;
        }

        if (_state == CalibrationState.WaitingSecond)
        {
            if (_firstPoint is not { } p1)
            {
                Cancel();
                return false;
            }
            FinishCalibration(poeHwnd, p1, p);
            return true;
        }

        return false;
    }

    private void FinishCalibration(IntPtr poeHwnd, (double relX, double relY) p1, (double relX, double relY) p2)
    {
        var cfg = _config.Current;
        var cols = Math.Max(1, cfg.Grid.Columns);
        var rows = Math.Max(1, cfg.Grid.Rows);

        // 防呆：第二点必须严格在第一点的右下方 (按 PRD 默认网格拓扑)
        if (p2.relX <= p1.relX || p2.relY <= p1.relY)
        {
            _log.Warn($"[Calibration] 第二点 ({p2.relX:F4}, {p2.relY:F4}) 不在第一点 ({p1.relX:F4}, {p1.relY:F4}) 的右下方");
            _firstPoint = null;
            Transition(CalibrationState.WaitingFirst);
            return;
        }

        double stepX = (p2.relX - p1.relX) / Math.Max(1, cols - 1);
        double stepY = (p2.relY - p1.relY) / Math.Max(1, rows - 1);

        // step < 0.005 (即客户区宽度的 0.5%) 视为异常 — POE 格子间距通常 2-4%
        if (stepX < 0.005 || stepY < 0.005)
        {
            _log.Warn($"[Calibration] 计算出的 step 异常 (stepX={stepX:F4}, stepY={stepY:F4})");
            _firstPoint = null;
            Transition(CalibrationState.WaitingFirst);
            return;
        }

        if (!GridCalculator.TryCaptureWindowSize(poeHwnd, out var w, out var h))
        {
            _log.Warn("[Calibration] 写入配置时获取窗口尺寸失败");
            return;
        }

        cfg.Grid.StartX = p1.relX;
        cfg.Grid.StartY = p1.relY;
        cfg.Grid.StepX = stepX;
        cfg.Grid.StepY = stepY;
        cfg.Grid.PoeWindowWidth = w;
        cfg.Grid.PoeWindowHeight = h;
        _config.SaveConfig(cfg);

        _log.Info($"[Calibration] 校准完成：Start=({p1.relX:F4},{p1.relY:F4}) Step=({stepX:F4},{stepY:F4}) 窗口=({w}x{h})");
        _firstPoint = null;
        var info = new CalibrationCompletedInfo(p1, (stepX, stepY), (w, h));
        Transition(CalibrationState.Idle);
        Completed?.Invoke(info);
    }

    private void Transition(CalibrationState next)
    {
        _state = next;
        StateChanged?.Invoke(next);
    }
}

/// <summary>校准完成事件 payload。</summary>
public readonly record struct CalibrationCompletedInfo(
    (double relX, double relY) Start,
    (double stepX, double stepY) Step,
    (int width, int height) PoeWindow);
