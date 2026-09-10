namespace PoEToolbox.Plugins.BagCleaner.Services;

/// <summary>
/// 全局热键服务接口 (符合 SPEC §4.2.1)。
/// WPF 专用实现：通过 <see cref="System.Windows.Interop.HwndSource.AddHook"/> 拦截 WM_HOTKEY 消息。
/// </summary>
public interface IHotkeyService
{
    /// <summary>触发清包热键 (默认 F2) 被按下时触发。</summary>
    event Action? Triggered;

    /// <summary>紧急停止热键 (默认 Esc) 被按下时触发。</summary>
    event Action? StopRequested;

    /// <summary>校准标记热键 (默认 F3) 被按下时触发 — 仅在校准态下有意义。</summary>
    event Action? CalibratePressed;

    /// <summary>
    /// 初始化：把全局消息钩子挂到指定 WPF 窗口的 HWND。
    /// 必须在调用 <see cref="RegisterAsync"/> 之前执行一次。
    /// </summary>
    /// <param name="hwnd">WPF 窗口句柄 (WindowInteropHelper.Handle)。</param>
    void Initialize(IntPtr hwnd);

    /// <summary>
    /// 注册触发热键 (主键 + 修饰键) + 停止热键 (主键) + 校准热键 (主键)。
    /// 失败时抛 <see cref="HotkeyRegistrationException"/>。
    /// </summary>
    Task RegisterAsync(int triggerKey, uint triggerModifier, int stopKey, int calibrateKey);

    /// <summary>
    /// 注册触发热键和校准热键，但不注册停止热键。
    /// 用于程序启动时，避免停止热键（如 Esc）阻止游戏正常使用。
    /// </summary>
    Task RegisterWithoutStopKeyAsync(int triggerKey, uint triggerModifier, int calibrateKey);

    /// <summary>单独注册停止热键。用于清包开始时动态注册。</summary>
    Task RegisterStopKeyAsync(int stopKey);

    /// <summary>单独注销停止热键。用于清包结束后释放。</summary>
    void UnregisterStopKey();

    /// <summary>注销所有已注册热键。程序退出时必须调用。</summary>
    void UnregisterAll();
}

public sealed class HotkeyRegistrationException : Exception
{
    public HotkeyRegistrationException(string message) : base(message) { }
    public HotkeyRegistrationException(string message, Exception inner) : base(message, inner) { }
}
