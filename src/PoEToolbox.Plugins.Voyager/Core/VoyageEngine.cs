using System.Drawing;
using System.IO;
using System.Text;
using System.Windows;
using PoEToolbox.Plugins.BagCleaner.Core;
using PoEToolbox.Plugins.BagCleaner.Input;
using PoEToolbox.Plugins.BagCleaner.Models;
using PoEToolbox.Plugins.Voyager.Models;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.Voyager.Core;

public class VoyageEngine
{
    private readonly InputSimulator _input;
    private readonly VoyagerConfig _config;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts != null;

    public VoyageEngine(InputSimulator input, VoyagerConfig config)
    {
        _input = input;
        _config = config;
    }

    public async Task RunAsync(IntPtr poeHwnd, Action<string> onProgress)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var sb = new StringBuilder();
        string? previous = null;
        var count = 0;
        var stopped = false;

        try
        {
            var grid = new GridConfig
            {
                Columns = _config.Columns,
                Rows = _config.Rows,
                StartX = _config.StartX,
                StartY = _config.StartY,
                StepX = _config.StepX,
                StepY = _config.StepY,
            };

            var positions = GridCalculator.ToAbsoluteScreenPositions(grid, poeHwnd, out _);
            if (positions.Count == 0)
            {
                onProgress("无法获取坐标，请先校准");
                stopped = true;
                return;
            }

            // TEMP: only first row for testing
            int maxCells = Math.Min(positions.Count, _config.Columns);
            for (int i = 0; i < maxCells; i++)
            {
                if (PoeDetector.Default.GetPoeForegroundWindow() != poeHwnd)
                {
                    onProgress("POE 已失去前台，已停止。");
                    stopped = true;
                    break;
                }
                if (ct.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                var p = positions[i];

                // Move mouse to cell center
                if (!_input.MoveMouse(p.X, p.Y))
                {
                    onProgress("无法移动鼠标，已停止。");
                    stopped = true;
                    break;
                }
                await Task.Delay(80, ct); // wait for UI hover

                // Clear clipboard, then Ctrl+C to copy
                Clipboard.Clear();
                if (!_input.PressCtrl())
                {
                    onProgress("无法发送 Ctrl，已停止。");
                    stopped = true;
                    break;
                }
                await Task.Delay(30, ct);
                if (!_input.KeyPress(0x43)) // 'C' key
                {
                    onProgress("无法发送复制按键，已停止。");
                    stopped = true;
                    break;
                }
                await Task.Delay(50, ct);
                if (!_input.ReleaseCtrl())
                {
                    onProgress("无法释放 Ctrl，已停止。");
                    stopped = true;
                    break;
                }
                await Task.Delay(100, ct); // wait for clipboard

                // Read clipboard
                string? text = null;
                try { text = Clipboard.GetText(); } catch { }

                if (string.IsNullOrEmpty(text))
                {
                    // Empty cell — stop if we already found maps (rest of stash is empty)
                    if (count > 0)
                    {
                        onProgress($"空格，停止。已复制 {count} 个地图。");
                        stopped = true;
                        break;
                    }
                    continue;
                }

                // Stop if same as previous (no more maps)
                if (text == previous)
                {
                    onProgress($"检测到重复，停止。已复制 {count} 个地图。");
                    stopped = true;
                    break;
                }

                previous = text;
                sb.AppendLine($"=== 地图 #{count + 1} ===");
                sb.AppendLine(text);
                sb.AppendLine();
                count++;
                onProgress($"已复制 {count} 个地图...");

                await Task.Delay(150, ct); // interval between cells
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        finally
        {
            _input.ForceReleaseAllModifiers();
            _cts?.Dispose();
            _cts = null;
        }

        // Write output
        var dir = Path.GetDirectoryName(_config.OutputFile);
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(_config.OutputFile, sb.ToString(), Encoding.UTF8);

        onProgress(stopped
            ? $"已停止：已复制 {count} 个地图 → {_config.OutputFile}"
            : $"完成：{count} 个地图 → {_config.OutputFile}");
    }

    public void Stop()
    {
        _input.ReleaseCtrl();
        _cts?.Cancel();
    }
}
