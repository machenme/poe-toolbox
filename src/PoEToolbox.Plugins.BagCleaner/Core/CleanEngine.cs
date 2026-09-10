using System.Drawing;
using PoEToolbox.Plugins.BagCleaner.Models;
using PoEToolbox.Plugins.BagCleaner.Input;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.BagCleaner.Core;

/// <summary>
/// 默认清包引擎：Ctrl + 网格遍历 + 反检测随机化。
///
/// 坐标来源 (V2 方案)：
/// - 绝对坐标已由 <see cref="GridCalculator.ToAbsoluteScreenPositions"/> 在执行前算好
/// - 传入 IReadOnlyList&lt;Point&gt; 屏幕绝对坐标
/// - 引擎只负责按列表遍历点击 + Ctrl 保持 + 随机抖动
///
/// 安全约束：
/// 1. Ctrl 释放走 finally 块，保证任何退出路径都释放。
/// 2. Random.Shared 是 .NET 6+ 内置线程安全均匀随机实例。
/// 3. 间隔与偏移每次现取，不预生成序列，避免多轮清包序列重复。
/// </summary>
public sealed class CleanEngine : ICleanEngine
{
    private readonly IInputSimulator _input;
    private readonly FileLogger _log;

    private CleanState _state = CleanState.Idle;
    public CleanState State => _state;

    public CleanEngine(IInputSimulator input, FileLogger log)
    {
        _input = input;
        _log = log;
    }

    public Task<CleanResult> ExecuteAsync(
        IReadOnlyList<Point> positions,
        AntiDetectionConfig antiDet,
        int moveSettleDelayMs,
        CancellationToken ct)
        => ExecuteAsync(positions, antiDet, moveSettleDelayMs, ct, null);

    public async Task<CleanResult> ExecuteAsync(
        IReadOnlyList<Point> positions,
        AntiDetectionConfig antiDet,
        int moveSettleDelayMs,
        CancellationToken ct,
        Func<bool>? isTargetForeground = null)
    {
        if (_state != CleanState.Idle)
        {
            _log.Warn($"[CleanEngine] 拒绝执行：当前状态 {_state} 非 Idle");
            return new CleanResult
            {
                TotalCells = positions.Count,
                Aborted = true
            };
        }

        if (positions == null || positions.Count == 0)
        {
            _log.Warn("[CleanEngine] 坐标列表为空，拒绝执行 (可能未校准)");
            return new CleanResult { TotalCells = 0, Aborted = true };
        }

        _state = CleanState.Executing;
        var result = new CleanResult { TotalCells = positions.Count };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (isTargetForeground is not null && !isTargetForeground())
            {
                result.Aborted = true;
                _log.Warn("[CleanEngine] 目标窗口不在前台，拒绝发送输入");
                return result;
            }
            if (!_input.PressCtrl())
            {
                result.Aborted = true;
                _log.Error("[CleanEngine] 无法发送 Ctrl 按键，已停止");
                return result;
            }
            _log.Info($"[CleanEngine] 开始清包：{positions.Count} 格, " +
                      $"间隔=[{antiDet.ClickIntervalMinMs},{antiDet.ClickIntervalMaxMs}]ms, " +
                      $"偏移=[{antiDet.PositionOffsetMinPx},{antiDet.PositionOffsetMaxPx}]px");

            for (int i = 0; i < positions.Count; i++)
            {
                if (isTargetForeground is not null && !isTargetForeground())
                {
                    result.Aborted = true;
                    _log.Warn($"[CleanEngine] 目标窗口失去前台，已停止 @ idx={i}");
                    return result;
                }
                if (ct.IsCancellationRequested)
                {
                    result.Aborted = true;
                    _log.Warn($"[CleanEngine] 用户中止 @ idx={i} clicked={result.ClickedCells}");
                    return result;
                }

                var p = positions[i];

                int offsetX = Random.Shared.Next(
                    antiDet.PositionOffsetMinPx,
                    antiDet.PositionOffsetMaxPx + 1);
                int offsetY = Random.Shared.Next(
                    antiDet.PositionOffsetMinPx,
                    antiDet.PositionOffsetMaxPx + 1);

                int targetX = p.X + offsetX;
                int targetY = p.Y + offsetY;

                if (!_input.MoveMouse(targetX, targetY))
                {
                    result.Aborted = true;
                    _log.Error($"[CleanEngine] 无法移动鼠标，已停止 @ idx={i}");
                    return result;
                }
                if (moveSettleDelayMs > 0) await Task.Delay(moveSettleDelayMs, ct);
                if (!_input.LeftClick())
                {
                    result.Aborted = true;
                    _log.Error($"[CleanEngine] 无法发送点击，已停止 @ idx={i}");
                    return result;
                }

                result.Positions.Add(new Point(targetX, targetY));
                result.ClickedCells++;

                int interval = Random.Shared.Next(
                    antiDet.ClickIntervalMinMs,
                    antiDet.ClickIntervalMaxMs + 1);
                result.IntervalsMs.Add(interval);
                await Task.Delay(interval, ct);
            }

            _log.Info($"[CleanEngine] 清包完成：点击 {result.ClickedCells}/{result.TotalCells} 格, " +
                      $"平均间隔 {(result.IntervalsMs.Count > 0 ? result.IntervalsMs.Average() : 0):F1}ms");
        }
        catch (OperationCanceledException)
        {
            result.Aborted = true;
            _log.Warn("[CleanEngine] OperationCanceledException，已中止遍历");
        }
        catch (Exception ex)
        {
            result.Aborted = true;
            _log.Error("[CleanEngine] 清包过程异常", ex);
        }
        finally
        {
            _input.ForceReleaseAllModifiers();
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            _state = CleanState.Idle;
        }

        return result;
    }
}
