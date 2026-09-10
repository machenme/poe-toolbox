namespace PoEToolbox.Shared;

/// <summary>
/// POE process/window detection interface.
/// </summary>
public interface IPoeDetector
{
    bool IsPoeForeground();
    bool IsPoeRunning();
    IntPtr GetPoeForegroundWindow();
    string? DetectGgpkPath();
}
