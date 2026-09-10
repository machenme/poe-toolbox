using System.Runtime.InteropServices;
using static PoEToolbox.Plugins.BagCleaner.Input.NativeMethods;

namespace PoEToolbox.Plugins.BagCleaner.Input;

/// <summary>
/// SendInput 优先 + mouse_event/keybd_event 降级。
///
/// 设计要点：
/// 1. 优先 SendInput：微软推荐，行为更接近真实硬件输入，第三方 hook 难度更大。
/// 2. 失败降级：极少数安全软件会拦截 SendInput，此时降级到 keybd_event/mouse_event。
/// 3. 异常安全：所有释放操作 (ReleaseCtrl / ForceReleaseAllModifiers) 自身必须不抛异常。
/// </summary>
public sealed class InputSimulator : IInputSimulator
{
    private readonly bool _useSendInput = true;
    private readonly object _lock = new();

    public InputSimulator() : this(useSendInput: true) { }

    public InputSimulator(bool useSendInput)
    {
        _useSendInput = useSendInput;
    }

    public bool PressCtrl()
    {
        return SafeInvoke(() =>
        {
            if (_useSendInput)
            {
                var input = MakeKeyInput(VK_LCONTROL, KEYEVENTF_EXTENDEDKEY);
                if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1) return;
            }
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        });
    }

    public bool ReleaseCtrl()
    {
        return SafeInvoke(() =>
        {
            if (_useSendInput)
            {
                var input = MakeKeyInput(VK_LCONTROL, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP);
                if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1) return;
            }
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        });
    }

    public bool MoveMouse(int x, int y)
    {
        if (x < 0 || y < 0) return false;
        System.Diagnostics.Debug.WriteLine($"[Input.MoveMouse] -> ({x},{y})");

        return SafeInvoke(() =>
        {
            // SetCursorPos 直接接受屏幕物理像素坐标，多显示器/DPI 安全。
            // 替代方案 SendInput+MOUSEEVENTF_ABSOLUTE 需要把屏幕坐标归一化到 0~65535，
            // 多显示器下需要减去虚拟屏幕偏移再算，且不同 POE 客户端对归一化坐标解释不一致。
            if (_useSendInput)
            {
                if (SetCursorPos(x, y)) return;
            }

            // 降级：mouse_event 也支持 ABSOLUTE 但需要归一化 (POE 兼容性差)
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0)
            {
                vw = GetSystemMetrics(SM_CXSCREEN);
                vh = GetSystemMetrics(SM_CYSCREEN);
                vx = 0; vy = 0;
            }
            int absX = (int)(((long)(x - vx) * 65535L) / Math.Max(vw - 1, 1));
            int absY = (int)(((long)(y - vy) * 65535L) / Math.Max(vh - 1, 1));
            mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, (uint)absX, (uint)absY, 0, UIntPtr.Zero);
        });
    }

    public bool LeftClick()
    {
        return SafeInvoke(() =>
        {
            if (_useSendInput)
            {
                var down = new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN }
                };
                var up = new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP }
                };
                var batch = new[] { down, up };
                if (SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>()) == batch.Length) return;
            }
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        });
    }

    public bool RightClick()
    {
        return SafeInvoke(() =>
        {
            if (_useSendInput)
            {
                var down = new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTDOWN }
                };
                var up = new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTUP }
                };
                var batch = new[] { down, up };
                if (SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>()) == batch.Length) return;
            }
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        });
    }

    /// <summary>按下并释放一个按键（用于组合键场景，如 Ctrl+C 中的 C）。</summary>
    public bool KeyPress(byte virtualKey)
    {
        return SafeInvoke(() =>
        {
            if (_useSendInput)
            {
                var down = MakeKeyInput(virtualKey, 0);
                var up = MakeKeyInput(virtualKey, KEYEVENTF_KEYUP);
                var batch = new[] { down, up };
                if (SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>()) == batch.Length) return;
            }
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        });
    }

    public bool ForceReleaseAllModifiers()
    {
        // 必须分别对左右两个版本发送 KEYUP：操作系统可能认为任意一边还按着
        var keys = new byte[] { VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT, VK_MENU };
        var succeeded = true;
        foreach (var vk in keys)
        {
            succeeded &= SafeInvoke(() =>
            {
                if (_useSendInput)
                {
                    var input = MakeKeyInput(vk, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP);
                    SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
                }
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            });
        }
        return succeeded;
    }

    public bool IsKeyDown(byte virtualKey)
    {
        // GetAsyncKeyState 走 user32，无需 P/Invoke 重复声明
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    // -------- internal --------

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static INPUT MakeKeyInput(byte vk, uint flags)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            }
        };
    }

    private bool SafeInvoke(Action action)
    {
        // 模拟器自身抛异常会污染调用方 (尤其是 finally / UnhandledException 路径)，
        // 这里吞掉所有异常，保证 Ctrl 释放路径"尽力而为"。
        try
        {
            lock (_lock) { action(); }
            return true;
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("[InputSimulator] Win32 input call failed.");
            return false;
        }
    }
}
