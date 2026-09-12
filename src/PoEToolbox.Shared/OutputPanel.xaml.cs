using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PoEToolbox.Shared;

/// <summary>
/// Unified output panel for plugins: a log area plus a semantic status bar.
/// Use <see cref="SetStatus"/> for the outcome ("✅ 成功" / "❌ 失败" in bold
/// green/red) and <see cref="AppendLog"/> for detailed engine output.
/// Thread-safe: all methods marshal to the UI thread.
/// </summary>
public partial class OutputPanel : UserControl
{
    private string _title = "";

    public OutputPanel()
    {
        InitializeComponent();
    }

    /// <summary>Section title shown above the log. Empty hides the row.</summary>
    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? "";
            TitleText.Text = _title;
            TitleText.Visibility = _title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Sets the status line with semantic color/weight.</summary>
    public void SetStatus(string text, UiStatus.Kind kind = UiStatus.Kind.Neutral)
        => UiStatus.Set(StatusText, text, kind);

    /// <summary>Appends one line to the log and scrolls to the end.</summary>
    public void AppendLog(string line)
    {
        void Append()
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
        if (Dispatcher.CheckAccess()) Append();
        else Dispatcher.Invoke(Append);
    }

    /// <summary>Clears the log area.</summary>
    public void ClearLog()
    {
        void Clear() => LogBox.Clear();
        if (Dispatcher.CheckAccess()) Clear();
        else Dispatcher.Invoke(Clear);
    }
}
