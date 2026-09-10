using System.Drawing;

namespace PoEToolbox.Plugins.BagCleaner.Input;

/// <summary>
/// 输入模拟器接口：所有 Win32 输入收口在此。
/// </summary>
public interface IInputSimulator
{
    /// <summary>按下左 Ctrl (使用 VK_LCONTROL，更接近真实硬件)。</summary>
    bool PressCtrl();

    /// <summary>释放左 Ctrl。</summary>
    bool ReleaseCtrl();

    /// <summary>将鼠标移动到屏幕绝对坐标 (x, y)。</summary>
    bool MoveMouse(int x, int y);

    /// <summary>在当前位置执行一次左键点击 (down + up) — POE 转移物品到仓库用此 + Ctrl。</summary>
    bool LeftClick();

    /// <summary>在当前位置执行一次右键点击 (down + up)。</summary>
    bool RightClick();

    /// <summary>异常恢复：强制释放所有可能被按下的修饰键 (Ctrl/Alt/Shift/Win)。</summary>
    bool ForceReleaseAllModifiers();

    /// <summary>查询当前逻辑按键状态。用于诊断。</summary>
    bool IsKeyDown(byte virtualKey);
}
