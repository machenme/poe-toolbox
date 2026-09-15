using System.Windows;
using System.Windows.Controls;
using PoEToolbox.Plugins.BagCleaner.Input;
using PoEToolbox.Plugins.BagCleaner.Services;
using PoEToolbox.Plugins.Voyager.Core;
using PoEToolbox.Plugins.Voyager.Models;
using PoEToolbox.Plugins.Voyager.Views;
using PoEToolbox.Shared;
using PoEToolbox.Sdk;

namespace PoEToolbox.Plugins.Voyager;

public class VoyagerPlugin : IPlugin
{
    public string Name => "航海助手";
    public string IconGlyph => "\uE7E3"; // Segoe MDL2 Assets: Ferry（船，贴合「航海助手」）
    public int Order => 20;

    private VoyagerView? _view;
    private HotkeyService? _hotkey;       // copy mode: offset 0x10
    private HotkeyService? _smartHotkey;  // smart click: offset 0x20
    private InputSimulator? _input;
    private VoyageEngine? _copyEngine;
    private SmartClickEngine? _smartEngine;
    private VoyagerConfig _config;

    public VoyagerPlugin()
    {
        _config = PoEToolbox.Shared.ConfigService.GetPluginConfig<VoyagerConfig>("Voyager")
                  ?? new VoyagerConfig();
        // The smart-click map layout is fixed for the current 2K display profile.
        // Keep legacy configurable values in sync so old config files cannot show 20×12.
        _config.SmartColumns = FixedLootGrid.Columns;
        _config.SmartRows = FixedLootGrid.Rows;
        _input = new InputSimulator();
        _copyEngine = new VoyageEngine(_input, _config);
        _smartEngine = new SmartClickEngine(_input, _config);

        // Copy mode hotkeys (offset 0x10)
        _hotkey = new HotkeyService(baseIdOffset: 0x10);
        _hotkey.Triggered += () => _ = DoVoyage();
        _hotkey.CalibratePressed += DoCopyCalibration;

        // Smart click hotkeys (offset 0x20)
        _smartHotkey = new HotkeyService(baseIdOffset: 0x20);
        _smartHotkey.Triggered += () => _ = DoSmartClick();
    }

    public UserControl CreateView()
    {
        if (_view == null)
        {
            _view = new VoyagerView(_config) { PluginRef = this };
        }
        return _view;
    }

    public void OnActivated() => RegisterAllHotkeys();
    public void OnDeactivated() => UnregisterAllHotkeys();

    public void OnAppShutdown()
    {
        _copyEngine?.Stop();
        _smartEngine?.Stop();
        _calWindow?.Close();
        _calWindow = null;
        _hotkey?.Dispose();
        _smartHotkey?.Dispose();
        _input?.ForceReleaseAllModifiers();
    }

    // ── Hotkey management ─────────────────────────────

    public void InitHotkey()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(
            Application.Current.MainWindow).Handle;

        if (_smartHotkey?.IsInitialized != true)
        {
            _smartHotkey?.Initialize(hwnd);
        }
        RegisterAllHotkeys();
    }

    public void RegisterAllHotkeys()
    {
        // Voyage stash cleanup only. The legacy 6×10 copy hotkey remains in code
        // for later restoration but is intentionally not initialized or registered.
        if (_smartHotkey?.IsInitialized == true)
        {
            _smartHotkey.UnregisterAll();
            _ = _smartHotkey.RegisterWithoutStopKeyAsync(
                _config.SmartTriggerKey, _config.SmartTriggerModifier, _config.SmartTriggerKey);
        }
    }

    public void UnregisterAllHotkeys()
    {
        _hotkey?.UnregisterAll();
        _smartHotkey?.UnregisterAll();
    }

    // ── Copy calibration ──────────────────────────────

    private enum CalState { Idle, WaitingFirst, WaitingLast }
    private CalState _calState;
    private double _calX1, _calY1;
    private CalibrationWindow? _calWindow;

    public void StartCalibration()
    {
        if (_calWindow != null) return;

        _calState = CalState.WaitingFirst;
        _calWindow = new CalibrationWindow(_config.HotkeyCalibrate);
        _calWindow.Closed += (_, _) =>
        {
            _calState = CalState.Idle;
            _calWindow = null;
            _view?.AppendLog("复制校准已取消");
        };
        _calWindow.Show();
    }

    private void DoCopyCalibration()
    {
        if (_calState == CalState.Idle) return;

        var hwnd = PoeDetector.Default.GetPoeForegroundWindow();
        if (hwnd == IntPtr.Zero) { _view?.AppendLog("POE 不在前台"); return; }

        var rel = PoEToolbox.Plugins.BagCleaner.Core.GridCalculator.CaptureRelativePoint(hwnd);
        if (rel == null) return;
        var (rx, ry) = rel.Value;

        if (_calState == CalState.WaitingFirst)
        {
            _calX1 = rx; _calY1 = ry;
            _view?.AppendLog($"复制左上已标记: ({rx:P1},{ry:P1})");
            _calState = CalState.WaitingLast;
            _calWindow?.UpdateForStep(2, _config.HotkeyCalibrate);
        }
        else if (_calState == CalState.WaitingLast)
        {
            _config.StartX = _calX1;
            _config.StartY = _calY1;
            _config.StepX = (rx - _calX1) / Math.Max(_config.Columns - 1, 1);
            _config.StepY = (ry - _calY1) / Math.Max(_config.Rows - 1, 1);
            _calState = CalState.Idle;
            PoEToolbox.Shared.ConfigService.SavePluginConfig("Voyager", _config);
            _calWindow?.Close();
            _calWindow = null;
            _view?.UpdateCopyCalDisplay();
            _view?.AppendLog($"复制校准完成: {_config.Columns}×{_config.Rows}, 步长({_config.StepX:P1},{_config.StepY:P1})");
        }
    }

    // ── Voyage (copy) ─────────────────────────────────

    private async Task DoVoyage()
    {
        if (_copyEngine?.IsRunning == true || _smartEngine?.IsRunning == true) return;
        var hwnd = PoeDetector.Default.GetPoeForegroundWindow();
        if (hwnd == IntPtr.Zero) { _view?.AppendLog("POE 不在前台"); return; }

        _view?.AppendLog("开始扫描...");
        await _copyEngine!.RunAsync(hwnd, msg =>
            Application.Current.Dispatcher.Invoke(() => _view?.AppendLog(msg)));
    }

    // ── Smart click ───────────────────────────────────

    private async Task DoSmartClick()
    {
        if (_copyEngine?.IsRunning == true || _smartEngine?.IsRunning == true) return;

        var hwnd = PoeDetector.Default.GetPoeForegroundWindow();
        if (hwnd == IntPtr.Zero) { _view?.AppendLog("POE 不在前台"); return; }

        _view?.AppendLog("开始智能点击...");
        await _smartEngine!.ExecuteAsync(hwnd, msg =>
            Application.Current.Dispatcher.Invoke(() => _view?.AppendLog(msg)));
    }
}
