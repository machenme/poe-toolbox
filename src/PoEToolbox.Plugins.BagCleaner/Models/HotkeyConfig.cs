namespace PoEToolbox.Plugins.BagCleaner.Models;

/// <summary>
/// 热键配置：
///   TriggerKey  - 触发清包（默认 F2）
///   StopKey     - 紧急停止（默认 Esc）
///   CalibrateKey - 校准标记（默认 F3）— 独立于清包键，规避"按 F2 时不知是校准还是清包"的歧义
/// 虚拟键码参考：https://learn.microsoft.com/windows/win32/inputdev/virtual-key-codes
/// </summary>
public sealed class HotkeyConfig
{
    /// <summary>触发清包热键 — 虚拟键码，默认 VK_F2 (0x71 = 113)</summary>
    public int TriggerKey { get; set; } = 0x71;

    /// <summary>触发是否需要 Modifier (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8)，默认 0 无</summary>
    public uint TriggerModifier { get; set; } = 0;

    /// <summary>紧急停止热键 — 虚拟键码，默认 VK_ESCAPE (0x1B = 27)</summary>
    public int StopKey { get; set; } = 0x1B;

    /// <summary>
    /// 校准标记热键 — 虚拟键码，默认 VK_F3 (0x72 = 114)。
    /// 与 TriggerKey 拆分：F2 始终是清包，F3 在校准态下用于标记第 1/2 格。
    /// </summary>
    public int CalibrateKey { get; set; } = 0x72;
}
