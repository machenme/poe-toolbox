using System.Windows;
using Microsoft.Win32;

namespace PoEToolbox.Shared;

/// <summary>
/// Theme management. Loads dark/light XAML ResourceDictionaries
/// and applies them to the Application.Resources.
/// Must be initialized by the App on startup.
/// </summary>
public static class ThemeManager
{
    public enum Theme { FollowSystem, Light, Dark }

    public static Theme Current { get; private set; } = Theme.FollowSystem;

    /// <summary>
    /// The Application.Resources MergedDictionaries collection.
    /// Set by App.xaml.cs on startup before any theme operations.
    /// </summary>
    public static ResourceDictionary? AppResources { get; set; }

    /// <summary>Check Windows system theme from registry.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            return val is 0;
        }
        catch { return false; }
    }

    /// <summary>Apply theme by loading the correct ResourceDictionary.</summary>
    public static void Apply(Theme theme)
    {
        Current = theme;
        var isDark = theme switch
        {
            Theme.Dark => true,
            Theme.Light => false,
            _ => IsSystemDark(),
        };

        var resources = AppResources ?? Application.Current.Resources;

        var toRemove = resources.MergedDictionaries
            .Where(d => d.Source?.OriginalString.Contains("Themes/") == true)
            .ToList();

        foreach (var d in toRemove)
            resources.MergedDictionaries.Remove(d);

        var themeFile = isDark ? "dark.xaml" : "light.xaml";
        var uri = new Uri($"pack://application:,,,/themes/{themeFile}", UriKind.Absolute);
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }
}
