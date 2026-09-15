using PoEToolbox.Sdk;
using System.Windows;
using System.Windows.Controls;
using PoEToolbox.Plugins.BagCleaner.Core;
using PoEToolbox.Plugins.BagCleaner.Input;
using PoEToolbox.Shared;
using PoEToolbox.Plugins.BagCleaner.Models;
using PoEToolbox.Plugins.BagCleaner.Views;
using BC = PoEToolbox.Plugins.BagCleaner.Services;

namespace PoEToolbox.Plugins.BagCleaner;

public class BagCleanerPlugin : IPlugin
{
    public string Name => "背包清理";
    public string IconGlyph => "\uE894"; // Segoe MDL2 Assets: Clear（清除，贴合「背包清理」）
    public int Order => 10;

    private BagCleanerView? _view;
    private BC.HotkeyService? _hotkey;
    private CleanEngine? _engine;
    private InputSimulator? _input;
    private FileLogger? _logger;
    private CancellationTokenSource? _cleanCts;

    public BagCleanerPlugin()
    {
        BagCleanerContext.Plugin = this;

        var config = new BC.ConfigService();
        config.LoadConfig();
        var poeDetector = PoEToolbox.Shared.PoeDetector.Default;
        _hotkey = new BC.HotkeyService();
        _input = new InputSimulator();
        _logger = new FileLogger();
        _engine = new CleanEngine(_input, _logger);

        BagCleanerContext.Config = config;
        BagCleanerContext.Hotkey = _hotkey;
        BagCleanerContext.PoeDetector = poeDetector;

        // Wire hotkey events
        _hotkey.Triggered += OnHotkeyTriggered;
        _hotkey.StopRequested += OnHotkeyStopRequested;
        _hotkey.CalibratePressed += OnHotkeyCalibrate;

        // Wire calibration events
        BagCleanerContext.Calibrator.StateChanged += OnCalibrationStateChanged;
        BagCleanerContext.Calibrator.Completed += OnCalibrationCompleted;
    }

    public UserControl CreateView() => _view ??= new BagCleanerView();

    public void OnActivated()
    {
        RegisterHotkeysIfReady();
    }

    public void InitializeHotkeys(IntPtr hwnd)
    {
        if (_hotkey?.IsInitialized != true)
            _hotkey?.Initialize(hwnd);

        RegisterHotkeysIfReady();
    }

    internal void RegisterHotkeysIfReady()
    {
        if (_hotkey?.IsInitialized != true) return;
        _hotkey.UnregisterAll();
        _ = _hotkey.RegisterWithoutStopKeyAsync(
            BagCleanerContext.Config!.Current.Hotkey.TriggerKey,
            BagCleanerContext.Config.Current.Hotkey.TriggerModifier,
            BagCleanerContext.Config.Current.Hotkey.CalibrateKey);
    }

    public void OnDeactivated()
    {
        // The hotkey is global and should remain available when another plugin is selected.
    }

    public void OnAppShutdown()
    {
        _hotkey?.UnregisterAll();
        _hotkey?.Dispose();
        _cleanCts?.Cancel();
        _input?.ForceReleaseAllModifiers();
    }

    // ═══ Hotkey handlers ════════════════════════════════

    private void OnHotkeyTriggered() => _ = RunCleanAsync();

    private async Task RunCleanAsync()
    {
        if (!BagCleanerContext.PoeDetector!.IsPoeForeground()) return;
        if (_engine == null) return;
        if (_cleanCts is { IsCancellationRequested: false }) return;

        var hwnd = BagCleanerContext.PoeDetector.GetPoeForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        var cfg = BagCleanerContext.Config!.Current;
        var allPositions = GridCalculator.ToAbsoluteScreenPositions(cfg.Grid, hwnd, out _);

        // Filter by skip mask: SkipMask[i]=true → skip (不清理)
        int cols = cfg.Grid.Columns;
        var positions = new List<System.Drawing.Point>();
        for (int i = 0; i < allPositions.Count; i++)
        {
            bool skipped = cfg.Grid.SkipMask != null
                        && i < cfg.Grid.SkipMask.Length
                        && cfg.Grid.SkipMask[i];
            if (!skipped)
                positions.Add(allPositions[i]);
        }

        if (positions.Count == 0) return;
        _cleanCts = new CancellationTokenSource();
        try
        {
            await _hotkey!.RegisterStopKeyAsync(cfg.Hotkey.StopKey);
            await _engine.ExecuteAsync(positions, cfg.AntiDetection,
                cfg.Timing.MoveSettleDelayMs, _cleanCts.Token,
                BagCleanerContext.PoeDetector.IsPoeForeground);
        }
        catch (BC.HotkeyRegistrationException ex)
        {
            _logger?.Error("[BagCleaner] 无法注册紧急停止热键", ex);
        }
        finally
        {
            _hotkey?.UnregisterStopKey();
            _cleanCts?.Dispose();
            _cleanCts = null;
            _input?.ForceReleaseAllModifiers();
        }
    }

    private void OnHotkeyStopRequested()
    {
        _input?.ReleaseCtrl(); // release before processing Esc to avoid Ctrl+Esc
        _cleanCts?.Cancel();
    }

    private void OnHotkeyCalibrate()
    {
        if (BagCleanerContext.PoeDetector!.IsPoeForeground())
            BagCleanerContext.Calibrator.TryCaptureCurrentPoint();
    }

    // ═══ Calibration UI ════════════════════════════════

    private CalibrationWindow? _calWindow;

    private void OnCalibrationStateChanged(CalibrationState state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (state == CalibrationState.Idle)
            {
                _calWindow?.Close();
                _calWindow = null;
                return;
            }

            if (_calWindow == null)
            {
                // _ = _hotkey!.RegisterStopKeyAsync(...); // pending development
                _calWindow = new CalibrationWindow(
                    BagCleanerContext.Config.Current.Hotkey.CalibrateKey);
                _calWindow.Closed += (_, _) =>
                {
                    BagCleanerContext.Calibrator.Cancel();
                    _calWindow = null;
                };
                _calWindow.Show();
            }
            _calWindow.UpdateForState(state);
        });
    }

    private void OnCalibrationCompleted(CalibrationCompletedInfo info)
    {
        BagCleanerContext.Config!.SaveConfig(BagCleanerContext.Config.Current);
        _view?.RefreshFromConfig();
    }
}
