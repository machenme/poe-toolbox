namespace PoEToolbox.Shared;

/// <summary>
/// POE process/window detection interface. 不含游戏数据文件的位置探测——工具不替用户猜客户端，
/// 路径只来自 <see cref="GameDataPathPreference"/> 里用户自己选过的那一份。
/// </summary>
public interface IPoeDetector
{
    bool IsPoeForeground();
    bool IsPoeRunning();
    IntPtr GetPoeForegroundWindow();
}
