using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PoEToolbox.Shared;

/// <summary>
/// POE process/window detection: is the client running, and is its window in the foreground.
/// <para>
/// 这里<b>不做</b>游戏数据文件（Content.ggpk / _.index.bin）的位置探测。工具不允许替用户猜一份客户端：
/// 见 <see cref="GameDataPathPreference"/>。
/// </para>
/// </summary>
public sealed class PoeDetector : IPoeDetector
{
    public static readonly PoeDetector Default = new();

    private static readonly string[] PoeProcessNames =
        [
            "PathOfExile",
            "PathOfExileSteam",
            "PathOfExile_x64",
            "PathOfExile_x64Steam",
            "PathOfExile_KG",
            "PathOfExile_x64_KG",
            "PathOfExileEGL",
        ];

    public bool IsPoeForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return IsPoeProcess(proc.ProcessName);
        }
        catch { return false; }
    }

    public IntPtr GetPoeForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            if (IsPoeProcess(proc.ProcessName)) return hwnd;
        }
        // 取 pid 与取进程之间进程可能刚好退出，属正常竞态，按「不是 PoE 窗口」处理。
        catch { }
        return IntPtr.Zero;
    }

    public bool IsPoeRunning()
    {
        foreach (var name in PoeProcessNames)
            if (Process.GetProcessesByName(name).Length > 0) return true;
        return false;
    }

    private static bool IsPoeProcess(string name)
        => Array.Exists(PoeProcessNames, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
