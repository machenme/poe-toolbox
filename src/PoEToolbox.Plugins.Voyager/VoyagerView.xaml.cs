using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PoEToolbox.Plugins.Voyager.Models;

namespace PoEToolbox.Plugins.Voyager;

public partial class VoyagerView : UserControl
{
    private readonly VoyagerConfig _config;
    public VoyagerPlugin? PluginRef { get; set; }

    private enum CaptureTarget { None, CopyTrigger, CopyCalibrate, SmartTrigger }
    private CaptureTarget _capture;

    public VoyagerView(VoyagerConfig config)
    {
        InitializeComponent();
        _config = config;
        UpdateCopyCalDisplay();
        UpdateSmartCalDisplay();
        LoadCopyHotkeyUI();
        LoadSmartHotkeyUI();
        TxtSmartColumns.Text = _config.SmartColumns.ToString();
        TxtSmartRows.Text = _config.SmartRows.ToString();
        TxtSmartOffsetX.Text = _config.SmartOffsetX.ToString();
        TxtSmartOffsetY.Text = _config.SmartOffsetY.ToString();

        Loaded += (_, _) =>
        {
            PluginRef?.InitHotkey();
        };
    }

    // ── Copy mode hotkey UI ───────────────────────────

    private void LoadCopyHotkeyUI()
    {
        BtnTriggerKey.Content = KeyDisplay(_config.HotkeyTrigger, _config.HotkeyTriggerModifier);
        BtnCalibrateKey.Content = KeyName(_config.HotkeyCalibrate);
        ChkCtrl.IsChecked  = (_config.HotkeyTriggerModifier & 0x0002) != 0;
        ChkAlt.IsChecked   = (_config.HotkeyTriggerModifier & 0x0001) != 0;
        ChkShift.IsChecked = (_config.HotkeyTriggerModifier & 0x0004) != 0;
        ChkWin.IsChecked   = (_config.HotkeyTriggerModifier & 0x0008) != 0;
    }

    private void OnTriggerKeyClick(object sender, RoutedEventArgs e) => EnterCapture(CaptureTarget.CopyTrigger);
    private void OnCalibrateKeyClick(object sender, RoutedEventArgs e) => EnterCapture(CaptureTarget.CopyCalibrate);

    private void OnModifierChanged(object sender, RoutedEventArgs e)
    {
        if (_capture != CaptureTarget.None) return;
        _config.HotkeyTriggerModifier =
            (ChkCtrl.IsChecked == true ? 0x0002u : 0) |
            (ChkAlt.IsChecked == true ? 0x0001u : 0) |
            (ChkShift.IsChecked == true ? 0x0004u : 0) |
            (ChkWin.IsChecked == true ? 0x0008u : 0);
        LoadCopyHotkeyUI();
        SaveAndReregister();
    }

    private void OnCopyCalibrateClick(object sender, RoutedEventArgs e)
        => PluginRef?.StartCalibration();

    public void UpdateCopyCalDisplay()
    {
        if (_config.StartX > 0 || _config.StartY > 0)
        {
            CopyCalStatus.Text = $"已校准 ({_config.Columns}×{_config.Rows})";
            CopyCalStatus.Foreground = (Brush)FindResource("Ok");
        }
    }

    // ── Smart click hotkey UI ─────────────────────────

    private void LoadSmartHotkeyUI()
    {
        BtnSmartTriggerKey.Content = KeyDisplay(_config.SmartTriggerKey, _config.SmartTriggerModifier);
        ChkSmartCtrl.IsChecked  = (_config.SmartTriggerModifier & 0x0002) != 0;
        ChkSmartAlt.IsChecked   = (_config.SmartTriggerModifier & 0x0001) != 0;
        ChkSmartShift.IsChecked = (_config.SmartTriggerModifier & 0x0004) != 0;
        ChkSmartWin.IsChecked   = (_config.SmartTriggerModifier & 0x0008) != 0;
    }

    private void OnSmartTriggerKeyClick(object sender, RoutedEventArgs e) => EnterCapture(CaptureTarget.SmartTrigger);

    private void OnSmartModifierChanged(object sender, RoutedEventArgs e)
    {
        if (_capture != CaptureTarget.None) return;
        _config.SmartTriggerModifier =
            (ChkSmartCtrl.IsChecked == true ? 0x0002u : 0) |
            (ChkSmartAlt.IsChecked == true ? 0x0001u : 0) |
            (ChkSmartShift.IsChecked == true ? 0x0004u : 0) |
            (ChkSmartWin.IsChecked == true ? 0x0008u : 0);
        LoadSmartHotkeyUI();
        SaveAndReregister();
    }

    private void OnSmartGridChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TxtSmartColumns.Text, out int cols) && cols >= 1 && cols <= 50)
            _config.SmartColumns = cols;
        if (int.TryParse(TxtSmartRows.Text, out int rows) && rows >= 1 && rows <= 50)
            _config.SmartRows = rows;
        SaveAndReregister();
    }

    private void OnSmartOffsetChanged(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TxtSmartOffsetX.Text, out int ox) && ox >= -100 && ox <= 100)
            _config.SmartOffsetX = ox;
        if (int.TryParse(TxtSmartOffsetY.Text, out int oy) && oy >= -100 && oy <= 100)
            _config.SmartOffsetY = oy;
        SaveAndReregister();
    }

    public void UpdateSmartCalDisplay()
    {
        SmartCalStatus.Text = "固定 2K (2560×1440) 网格 (12×20)，无需校准";
        SmartCalStatus.Foreground = (Brush)FindResource("Ok");
    }

    // ── Key capture ────────────────────────────────────

    private void EnterCapture(CaptureTarget mode)
    {
        _capture = mode;
        PluginRef?.UnregisterAllHotkeys();

        ResetButtonStyle(BtnTriggerKey);
        ResetButtonStyle(BtnCalibrateKey);
        ResetButtonStyle(BtnSmartTriggerKey);

        var highlightBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xF0, 0xFE));
        var highlightBorder = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));

        (Button btn, _) = GetCaptureButton(mode);
        btn.Content = "按任意键...";
        btn.Background = highlightBrush;
        btn.BorderBrush = highlightBorder;
    }

    private void ExitCapture()
    {
        _capture = CaptureTarget.None;
        LoadCopyHotkeyUI();
        LoadSmartHotkeyUI();
        ResetButtonStyle(BtnTriggerKey);
        ResetButtonStyle(BtnCalibrateKey);
        ResetButtonStyle(BtnSmartTriggerKey);

        PluginRef?.RegisterAllHotkeys();
    }

    private (Button btn, bool isCalibrate) GetCaptureButton(CaptureTarget mode) => mode switch
    {
        CaptureTarget.CopyTrigger     => (BtnTriggerKey, false),
        CaptureTarget.CopyCalibrate   => (BtnCalibrateKey, true),
        CaptureTarget.SmartTrigger    => (BtnSmartTriggerKey, false),
        _ => (BtnTriggerKey, false)
    };

    private static void ResetButtonStyle(Button btn)
    {
        btn.Background = Brushes.White;
        btn.BorderBrush = Application.Current.TryFindResource("Border") as Brush
                          ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_capture == CaptureTarget.None) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            ExitCapture();
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                 or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        int vk = KeyToVirtualKey(e.Key);
        var (_, isCalibrate) = GetCaptureButton(_capture);

        int? otherTrigger = null, otherCalibrate = null;

        switch (_capture)
        {
            case CaptureTarget.CopyTrigger:
                otherCalibrate = _config.HotkeyCalibrate;
                if (vk == otherCalibrate) { AppendLog("触发键不能和校准键相同"); ExitCapture(); return; }
                _config.HotkeyTrigger = vk;
                break;
            case CaptureTarget.CopyCalibrate:
                otherTrigger = _config.HotkeyTrigger;
                if (vk == otherTrigger) { AppendLog("校准键不能和触发键相同"); ExitCapture(); return; }
                _config.HotkeyCalibrate = vk;
                break;
            case CaptureTarget.SmartTrigger:
                _config.SmartTriggerKey = vk;
                break;
        }

        SaveAndReregister();
        ExitCapture();
    }

    // ── Persist ────────────────────────────────────────

    private void SaveAndReregister()
    {
        PoEToolbox.Shared.ConfigService.SavePluginConfig("Voyager", _config);
        PluginRef?.RegisterAllHotkeys();
    }

    // ── Log ────────────────────────────────────────────

    // The log area is a TextBlock: `Text += ...` rebuilds the whole string on every line, and it never
    // shrank. Keep the last MaxLogLines lines in a builder and hand the block one string.
    private const int MaxLogLines = 300;
    private const int TrimSlackLines = 100;
    private readonly StringBuilder _log = new();
    private int _logLines;

    public void AppendLog(string msg)
    {
        _log.Append(DateTime.Now.ToString("HH:mm:ss")).Append(' ').Append(msg).Append('\n');
        if (++_logLines > MaxLogLines + TrimSlackLines)
            TrimLog();
        LogText.Text = _log.ToString();
    }

    /// <summary>Drops whole lines from the front until only <see cref="MaxLogLines"/> remain.</summary>
    private void TrimLog()
    {
        var drop = _logLines - MaxLogLines;
        var cut = 0;
        for (var i = 0; i < _log.Length && drop > 0; i++)
            if (_log[i] == '\n')
            {
                drop--;
                cut = i + 1;
            }

        if (cut == 0)
            return;

        _log.Remove(0, cut);
        _logLines = MaxLogLines;
    }

    // ── Helpers ────────────────────────────────────────

    private static string KeyDisplay(int vk, uint modifiers)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

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

    private static int KeyToVirtualKey(Key key)
    {
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
}
