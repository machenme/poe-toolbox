using System.Windows;
using PoEToolbox.Plugins.BagCleaner.Input;
using PoEToolbox.Plugins.Voyager.Models;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.Voyager.Core;

/// <summary>
/// 智能点击引擎：截屏判空 → 只对有东西的格子执行 Ctrl+LeftClick。
/// </summary>
public class SmartClickEngine
{
    private readonly InputSimulator _input;
    private readonly VoyagerConfig _config;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts != null;

    public SmartClickEngine(InputSimulator input, VoyagerConfig config)
    {
        _input = input;
        _config = config;
    }

    public async Task ExecuteAsync(IntPtr poeHwnd, Action<string> onProgress)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            if (PoeDetector.Default.GetPoeForegroundWindow() != poeHwnd)
            {
                onProgress("POE 不在前台，已停止。");
                return;
            }
            // Step 1: screenshot + detect
            onProgress("截屏检测海图仓库占用格...");
            string detectionError = string.Empty;
            bool[] occupied = await Task.Run(() =>
            {
                return CellDetector.DetectFixed(poeHwnd, out detectionError);
            }, ct);

            if (!string.IsNullOrEmpty(detectionError))
            {
                onProgress(detectionError);
                return;
            }

            int total = occupied.Count(o => o);
            var occupiedCells = Enumerable.Range(0, occupied.Length)
                .Where(i => occupied[i])
                .Select(i => $"({i % FixedLootGrid.Columns + 1},{i / FixedLootGrid.Columns + 1})");
            onProgress($"检测完成: {total}/{occupied.Length} 个占用格");
            onProgress(total == 0 ? "占用格: 无" : $"占用格: {string.Join(" ", occupiedCells)}");

            if (total == 0)
            {
                onProgress("没有检测到占用格，跳过点击");
                return;
            }

            // Step 2: use centers computed from the fixed outer vertices.
            if (!FixedLootGrid.TryGetScreenRect(poeHwnd, out var gridRect, out var gridError))
            {
                onProgress(gridError);
                return;
            }

            var positions = FixedLootGrid.GetCellCenters(gridRect);

            // Step 3: click only occupied cells
            int clicked = 0;
            for (int i = 0; i < occupied.Length && i < positions.Count; i++)
            {
                if (PoeDetector.Default.GetPoeForegroundWindow() != poeHwnd)
                {
                    onProgress("POE 已失去前台，已停止。");
                    break;
                }
                if (ct.IsCancellationRequested) break;
                if (!occupied[i]) continue;

                var p = positions[i];
                int cx = p.X + _config.SmartOffsetX;
                int cy = p.Y + _config.SmartOffsetY;

                if (!_input.MoveMouse(cx, cy))
                {
                    onProgress("无法移动鼠标，已停止。");
                    break;
                }
                await Task.Delay(60, ct);

                if (!_input.PressCtrl())
                {
                    onProgress("无法发送 Ctrl，已停止。");
                    break;
                }
                await Task.Delay(30, ct);
                if (!_input.LeftClick())
                {
                    onProgress("无法发送点击，已停止。");
                    break;
                }
                await Task.Delay(40, ct);
                if (!_input.ReleaseCtrl())
                {
                    onProgress("无法释放 Ctrl，已停止。");
                    break;
                }

                clicked++;
                onProgress($"点击 {clicked}/{total}...");

                await Task.Delay(80, ct);
            }

            onProgress($"完成: 点击 {clicked} 个格子");
        }
        catch (OperationCanceledException)
        {
            onProgress("已停止");
        }
        finally
        {
            _input.ForceReleaseAllModifiers();
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Stop()
    {
        _input.ReleaseCtrl();
        _cts?.Cancel();
    }
}
