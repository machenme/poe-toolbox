using System.Windows;
using System.Windows.Controls;

namespace PoEToolbox.Shared;

/// <summary>
/// Semantic status-bar feedback. Makes the outcome of an operation unmistakable:
/// green bold for success, red bold for failure, orange for user-input problems,
/// secondary text color for neutral progress messages. Colors come from the active
/// theme dictionaries (SuccessBrush / ErrorBrush / WarningBrush), so both light
/// and dark themes work.
/// </summary>
public static class UiStatus
{
    public enum Kind { Neutral, Success, Warning, Error }

    public static void Set(TextBlock target, string text, Kind kind = Kind.Neutral)
    {
        var brushKey = kind switch
        {
            Kind.Success => "SuccessBrush",
            Kind.Warning => "WarningBrush",
            Kind.Error => "ErrorBrush",
            _ => "TextSecondaryBrush",
        };

        void Set()
        {
            target.Text = text;
            target.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            target.FontWeight = kind is Kind.Success or Kind.Error
                ? FontWeights.Bold
                : FontWeights.Normal;
        }

        if (target.Dispatcher.CheckAccess()) Set();
        else target.Dispatcher.Invoke(Set);
    }
}
