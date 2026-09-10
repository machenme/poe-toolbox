using System.Runtime.InteropServices;
using System.Windows.Interop;
using PoEToolbox.Plugins.BagCleaner.Input;
using static PoEToolbox.Plugins.BagCleaner.Input.NativeMethods;

namespace PoEToolbox.Plugins.BagCleaner.Services;

/// <summary>
/// 全局热键服务 (WPF HwndSource 钩子实现 — SPEC §4.2.1)：
/// 1. Initialize(hwnd) 时把钩子挂到 HwndSource.FromHwnd(hwnd) 同时保存 hwnd 句柄
/// 2. RegisterAsync 调 Win32 RegisterHotKey(_hwnd, id, ...) — 消息发到指定窗口 (MainWindow)
/// 3. HwndSource.AddHook 拦截 WM_HOTKEY，提取 wParam 热键 ID，转发为 .NET event
///
/// 关键点：RegisterHotKey 的第一个参数必须传具体窗口 HWND（不能是 IntPtr.Zero），
/// 否则 WM_HOTKEY 是 thread-bound message，HwndSource.AddHook 收不到。
/// </summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly int _hotkeyIdTrigger;
    private readonly int _hotkeyIdStop;
    private readonly int _hotkeyIdCalibrate;

    private IntPtr _hwnd = IntPtr.Zero;
    public bool IsInitialized => _hwnd != IntPtr.Zero;
    private HwndSource? _hwndSource;
    private bool _triggerRegistered;
    private bool _stopRegistered;
    private bool _calibrateRegistered;
    private bool _disposed;

    public event Action? Triggered;
    public event Action? StopRequested;
    public event Action? CalibratePressed;

    /// <summary>
    /// 创建热键服务。baseIdOffset 用于多插件共存时避免 ID 冲突。
    /// BagCleaner 默认 0，Voyager 等插件应使用不同偏移量（如 0x10）。
    /// </summary>
    public HotkeyService(int baseIdOffset = 0)
    {
        _hotkeyIdTrigger   = 0xB7A1 + baseIdOffset;
        _hotkeyIdStop      = 0xB7A2 + baseIdOffset;
        _hotkeyIdCalibrate = 0xB7A3 + baseIdOffset;
    }

    public void Initialize(IntPtr hwnd)
    {
        if (_hwndSource != null)
            throw new InvalidOperationException("HotkeyService 已初始化");
        if (hwnd == IntPtr.Zero)
            throw new ArgumentException("hwnd 无效", nameof(hwnd));

        _hwnd = hwnd;
        _hwndSource = HwndSource.FromHwnd(hwnd)
            ?? throw new InvalidOperationException("无法获取 HwndSource，HWND 无效");
        _hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            try
            {
                if (id == _hotkeyIdTrigger) Triggered?.Invoke();
                else if (id == _hotkeyIdStop) StopRequested?.Invoke();
                else if (id == _hotkeyIdCalibrate) CalibratePressed?.Invoke();
            }
            catch
            {
                // event handler 抛异常不能影响消息泵
            }
        }
        return IntPtr.Zero;
    }

    public Task RegisterAsync(int triggerKey, uint triggerModifier, int stopKey, int calibrateKey)
    {
        if (_hwndSource == null || _hwnd == IntPtr.Zero)
            throw new InvalidOperationException("请先调用 Initialize(hwnd)");

        if (_triggerRegistered || _stopRegistered || _calibrateRegistered)
            throw new HotkeyRegistrationException("热键已注册，请先 UnregisterAll");

        // MOD_NOREPEAT 防止长按重复触发
        var triggerFlags = triggerModifier | MOD_NOREPEAT;
        var stopFlags = MOD_NOREPEAT;
        var calibrateFlags = MOD_NOREPEAT;

        if (!RegisterHotKey(_hwnd, _hotkeyIdTrigger, triggerFlags, (uint)triggerKey))
        {
            var err = Marshal.GetLastWin32Error();
            throw new HotkeyRegistrationException(
                $"注册触发热键 (VK=0x{triggerKey:X}) 失败，Win32 错误码: {err}。可能被其他程序占用。");
        }
        _triggerRegistered = true;

        if (!RegisterHotKey(_hwnd, _hotkeyIdStop, stopFlags, (uint)stopKey))
        {
            var err = Marshal.GetLastWin32Error();
            UnregisterHotKey(_hwnd, _hotkeyIdTrigger);
            _triggerRegistered = false;
            throw new HotkeyRegistrationException(
                $"注册停止热键 (VK=0x{stopKey:X}) 失败，Win32 错误码: {err}。可能被其他程序占用。");
        }
        _stopRegistered = true;

        // 校准键必须与触发/停止不同 (否则语义混乱，WPF 也不会同时触发)
        if (calibrateKey != triggerKey && calibrateKey != stopKey)
        {
            if (!RegisterHotKey(_hwnd, _hotkeyIdCalibrate, calibrateFlags, (uint)calibrateKey))
            {
                var err = Marshal.GetLastWin32Error();
                UnregisterHotKey(_hwnd, _hotkeyIdTrigger);
                UnregisterHotKey(_hwnd, _hotkeyIdStop);
                _triggerRegistered = false;
                _stopRegistered = false;
                throw new HotkeyRegistrationException(
                    $"注册校准热键 (VK=0x{calibrateKey:X}) 失败，Win32 错误码: {err}。可能被其他程序占用。");
            }
            _calibrateRegistered = true;
        }

        return Task.CompletedTask;
    }

    public Task RegisterWithoutStopKeyAsync(int triggerKey, uint triggerModifier, int calibrateKey)
    {
        if (_hwndSource == null || _hwnd == IntPtr.Zero)
            throw new InvalidOperationException("请先调用 Initialize(hwnd)");

        if (_triggerRegistered || _calibrateRegistered)
            throw new HotkeyRegistrationException("热键已注册，请先 UnregisterAll");

        // MOD_NOREPEAT 防止长按重复触发
        var triggerFlags = triggerModifier | MOD_NOREPEAT;
        var calibrateFlags = MOD_NOREPEAT;

        if (!RegisterHotKey(_hwnd, _hotkeyIdTrigger, triggerFlags, (uint)triggerKey))
        {
            var err = Marshal.GetLastWin32Error();
            throw new HotkeyRegistrationException(
                $"注册触发热键 (VK=0x{triggerKey:X}) 失败，Win32 错误码: {err}。可能被其他程序占用。");
        }
        _triggerRegistered = true;

        // 校准键必须与触发不同
        if (calibrateKey != triggerKey)
        {
            if (!RegisterHotKey(_hwnd, _hotkeyIdCalibrate, calibrateFlags, (uint)calibrateKey))
            {
                var err = Marshal.GetLastWin32Error();
                UnregisterHotKey(_hwnd, _hotkeyIdTrigger);
                _triggerRegistered = false;
                throw new HotkeyRegistrationException(
                    $"注册校准热键 (VK=0x{calibrateKey:X}) 失败，Win32 错误码: {err}。可能被其他程序占用。");
            }
            _calibrateRegistered = true;
        }

        return Task.CompletedTask;
    }

    public Task RegisterStopKeyAsync(int stopKey)
    {
        if (_hwndSource == null || _hwnd == IntPtr.Zero)
            throw new InvalidOperationException("请先调用 Initialize(hwnd)");

        if (_stopRegistered)
            return Task.CompletedTask; // 已注册，跳过

        var stopFlags = MOD_NOREPEAT;
        if (!RegisterHotKey(_hwnd, _hotkeyIdStop, stopFlags, (uint)stopKey))
        {
            var err = Marshal.GetLastWin32Error();
            throw new HotkeyRegistrationException(
                $"注册停止热键 (VK=0x{stopKey:X}) 失败，Win32 错误码: {err}。可能被其他程序占用。");
        }
        _stopRegistered = true;
        return Task.CompletedTask;
    }

    public void UnregisterStopKey()
    {
        if (_hwnd == IntPtr.Zero || !_stopRegistered) return;
        UnregisterHotKey(_hwnd, _hotkeyIdStop);
        _stopRegistered = false;
    }

    public void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_triggerRegistered)
        {
            UnregisterHotKey(_hwnd, _hotkeyIdTrigger);
            _triggerRegistered = false;
        }
        if (_stopRegistered)
        {
            UnregisterHotKey(_hwnd, _hotkeyIdStop);
            _stopRegistered = false;
        }
        if (_calibrateRegistered)
        {
            UnregisterHotKey(_hwnd, _hotkeyIdCalibrate);
            _calibrateRegistered = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }
}
