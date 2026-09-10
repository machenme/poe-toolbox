using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PoEToolbox.Shared;

/// <summary>
/// POE detection: GGPK path discovery + foreground window check.
/// Supports PoE1 (Standalone/Steam/Epic) and PoE2.
/// </summary>
public sealed class PoeDetector : IPoeDetector
{
    public static readonly PoeDetector Default = new();

    private static readonly string[] PoeProcessNames =
        ["PathOfExile", "PathOfExileSteam", "PathOfExile_x64", "PathOfExileEGL"];

    /// <summary>
    /// Detect the game data path. Returns either:
    ///   - Content.ggpk path (official client or PoE1)
    ///   - Bundles2/_.index.bin path (Steam/Epic)
    ///   - null if not found
    /// </summary>
    public string? DetectGameDataPath()
    {
        // Registry: PoE1
        string[] regPathsPoE1 =
        [
            @"SOFTWARE\WOW6432Node\GrindingGearGames\Path of Exile",
            @"SOFTWARE\GrindingGearGames\Path of Exile",
        ];

        // Registry: PoE2
        string[] regPathsPoE2 =
        [
            @"SOFTWARE\WOW6432Node\GrindingGearGames\Path of Exile 2",
            @"SOFTWARE\GrindingGearGames\Path of Exile 2",
        ];

        foreach (var regPath in regPathsPoE1.Concat(regPathsPoE2))
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var key = hive.OpenSubKey(regPath);
                    var dir = key?.GetValue("InstallLocation") as string;
                    if (dir is not null)
                    {
                        var found = FindGameDataInDir(dir);
                        if (found is not null) return found;
                    }
                }
                catch { }
            }
        }

        // Common paths (Steam, standalone)
        foreach (var dir in new[]
        {
            @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile",
            @"C:\Program Files\Grinding Gear Games\Path of Exile",
            @"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile",
            @"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2",
            @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile 2",
            @"C:\Program Files\Grinding Gear Games\Path of Exile 2",
        })
        {
            var found = FindGameDataInDir(dir);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>
    /// Given a game install directory, find the data file:
    /// 1. Content.ggpk (official client)
    /// 2. Bundles2/_.index.bin (Steam/Epic — pre-extracted)
    /// </summary>
    private static string? FindGameDataInDir(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        var ggpk = Path.Combine(dir, "Content.ggpk");
        if (File.Exists(ggpk)) return ggpk;

        var idx = Path.Combine(dir, "Bundles2", "_.index.bin");
        if (File.Exists(idx)) return idx;

        return null;
    }

    /// <summary>[Deprecated] Use DetectGameDataPath() instead.</summary>
    public string? DetectGgpkPath() => DetectGameDataPath();

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
