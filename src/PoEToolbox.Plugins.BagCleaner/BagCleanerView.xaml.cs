using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PoEToolbox.Plugins.BagCleaner.Services;
using PoEToolbox.Plugins.BagCleaner.ViewModels;

namespace PoEToolbox.Plugins.BagCleaner;

/// <summary>
/// 主配置窗口 (SPEC §5 MainWindow.xaml + code-behind)。
///
/// code-behind 职责：
/// 1. 接收启动时 MainViewModel (App 在 ShowMainWindow 之前 RefreshFromConfig 调用)
/// 2. 实现热键捕获 (点击"触发键"或"停止键"按钮后进入捕获态，Window.KeyDown 接收按键)
/// 3. 取消按钮关闭
/// </summary>
public partial class BagCleanerView : UserControl
{
    private MainViewModel? _vm;
    private IHotkeyService? _hotkey;
    private enum CaptureMode { None, Trigger, Stop, Calibrate }
    private CaptureMode _capture = CaptureMode.None;

    public BagCleanerView()
    {
        InitializeComponent();

        // 注入 VM
        _vm = new MainViewModel(BagCleanerContext.Config!, BagCleanerContext.Hotkey!);
        _hotkey = BagCleanerContext.Hotkey;
        DataContext = _vm;

        // HotkeyService needs window HWND — defer init until view is loaded
        Loaded += (_, _) =>
        {
            if (_hotkey is HotkeyService hks && !hks.IsInitialized)
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(
                    System.Windows.Application.Current.MainWindow).Handle;
                hks.Initialize(hwnd);
            }
            BagCleanerContext.Plugin?.RegisterHotkeysIfReady();
        };

        // Auto-save on any setting change (300ms debounce)
        _vm!.PropertyChanged += (_, _) => DebounceAutoSave();
    }

    private System.Threading.Timer? _saveTimer;
    private void DebounceAutoSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new System.Threading.Timer(_ =>
        {
            Dispatcher.Invoke(() => _vm?.AutoSave());
        }, null, 300, System.Threading.Timeout.Infinite);
    }

    /// <summary>从配置重新加载 VM。</summary>
    public void RefreshFromConfig()
    {
        _vm?.LoadFromConfig();
    }

    // ============== 热键捕获 ==============

    private void OnTriggerKeyClick(object sender, RoutedEventArgs e)
    {
        EnterCapture(CaptureMode.Trigger);
    }

    private void OnStopKeyClick(object sender, RoutedEventArgs e)
    {
        EnterCapture(CaptureMode.Stop);
    }

    private void OnCalibrateKeyClick(object sender, RoutedEventArgs e)
    {
        EnterCapture(CaptureMode.Calibrate);
    }

    private void EnterCapture(CaptureMode mode)
    {
        _capture = mode;
        _vm?.SetCaptureActive(true);

        // 注销全局热键，让 WPF KeyDown 能接收到被注册的按键 (如 F3)
        _hotkey?.UnregisterAll();

        // 先重置所有按钮
        BtnTriggerKey.Content = _vm?.TriggerKeyDisplay ?? "";
        BtnTriggerKey.Background = Brushes.White;
        BtnTriggerKey.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD));
        BtnStopKey.Content = _vm?.StopKeyDisplay ?? "";
        BtnStopKey.Background = Brushes.White;
        BtnStopKey.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD));
        BtnCalibrateKey.Content = _vm?.CalibrateKeyDisplay ?? "";
        BtnCalibrateKey.Background = Brushes.White;
        BtnCalibrateKey.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD));

        // 高亮当前捕获模式的按钮
        var highlightBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xF0, 0xFE));
        var highlightBorder = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        switch (mode)
        {
            case CaptureMode.Trigger:
                BtnTriggerKey.Content = "按任意键...";
                BtnTriggerKey.Background = highlightBrush;
                BtnTriggerKey.BorderBrush = highlightBorder;
                break;
            case CaptureMode.Stop:
                BtnStopKey.Content = "按任意键...";
                BtnStopKey.Background = highlightBrush;
                BtnStopKey.BorderBrush = highlightBorder;
                break;
            case CaptureMode.Calibrate:
                BtnCalibrateKey.Content = "按任意键...";
                BtnCalibrateKey.Background = highlightBrush;
                BtnCalibrateKey.BorderBrush = highlightBorder;
                break;
        }
    }

    private void ExitCapture()
    {
        _capture = CaptureMode.None;
        _vm?.SetCaptureActive(false);
        if (_vm != null)
        {
            BtnTriggerKey.Content = _vm.TriggerKeyDisplay;
            BtnStopKey.Content = _vm.StopKeyDisplay;
            BtnCalibrateKey.Content = _vm.CalibrateKeyDisplay;
        }
        BtnTriggerKey.Background = Brushes.White;
        BtnStopKey.Background = Brushes.White;
        BtnCalibrateKey.Background = Brushes.White;
        BtnTriggerKey.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD));
        BtnStopKey.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD));
        BtnCalibrateKey.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xDF, 0xDD));

        // 重新注册全局热键 (使用 VM 中最新的值)
        if (_hotkey != null && _vm != null)
        {
            try
            {
                _hotkey.RegisterWithoutStopKeyAsync(
                    _vm.TriggerKey,
                    (uint)(_vm.UseCtrl ? 0x0002 : 0) | (uint)(_vm.UseAlt ? 0x0001 : 0)
                        | (uint)(_vm.UseShift ? 0x0004 : 0) | (uint)(_vm.UseWin ? 0x0008 : 0),
                    _vm.CalibrateKey).GetAwaiter().GetResult();
            }
            catch (HotkeyRegistrationException)
            {
                // 重新注册失败不阻塞 UI，保存时会再次尝试
            }
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_capture == CaptureMode.None) return;

        e.Handled = true;

        // Esc 取消捕获 (不修改)
        if (e.Key == Key.Escape)
        {
            ExitCapture();
            return;
        }

        // 单独的修饰键不算主键
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                 or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var key = e.Key;
        var vk = KeyToVirtualKey(key);
        var display = key.ToString();

        // F1-F12 显示 "Fn"
        if (key >= Key.F1 && key <= Key.F12)
            display = "F" + ((int)key - (int)Key.F1 + 1);

        if (_vm == null) return;

        switch (_capture)
        {
            case CaptureMode.Trigger:   _vm.SetTriggerKeyFromInput(vk, display); break;
            case CaptureMode.Stop:      _vm.SetStopKeyFromInput(vk, display); break;
            case CaptureMode.Calibrate: _vm.SetCalibrateKeyFromInput(vk, display); break;
        }

        ExitCapture();
    }

    private static int KeyToVirtualKey(Key key)
    {
        // WPF Key 枚举的 digit/letter 范围与 VK 一致 (A=0x41, 0=0x30, F1=0x70)
        if (key >= Key.A && key <= Key.Z) return 0x41 + (int)(key - Key.A);
        if (key >= Key.D0 && key <= Key.D9) return 0x30 + (int)(key - Key.D0);
        if (key >= Key.F1 && key <= Key.F12) return 0x70 + (int)(key - Key.F1);
        return key switch
        {
            Key.Escape => 0x1B,
            Key.Tab => 0x09,
            Key.Space => 0x20,
            Key.Back => 0x08,
            Key.Enter => 0x0D,
            Key.Delete => 0x2E,
            Key.Insert => 0x2D,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Up => 0x26,
            Key.Down => 0x28,
            Key.Left => 0x25,
            Key.Right => 0x27,
            _ => (int)key
        };
    }

    // ============== 取消 ==============

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_vm != null && _vm.Status == MainViewModel.SaveStatus.Dirty)
        {
            var ans = MessageBox.Show(
                "有未保存的修改，确定要丢弃吗？",
                "未保存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return;
        }
        // Reload from config (discard changes)
        _vm?.LoadFromConfig();
    }
}
