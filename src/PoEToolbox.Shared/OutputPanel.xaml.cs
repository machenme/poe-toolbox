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
    /// <summary>Lines kept in the log area; older ones are dropped so a long session stays bounded.</summary>
    private const int MaxLogLines = 800;

    /// <summary>Extra lines tolerated before trimming, so the trim cost stays amortised.</summary>
    private const int TrimSlackLines = 200;

    private string _title = "";

    /// <summary>Lines written so far; see <see cref="TrimToCap"/>.</summary>
    private int _appendedLines;

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

    /// <summary>Appends one line to the log, drops the oldest lines, and scrolls to the end.</summary>
    public void AppendLog(string line)
    {
        void Append()
        {
            LogBox.AppendText(line + Environment.NewLine);
            var added = 1;
            foreach (var c in line)
                if (c == '\n')
                    added++;
            _appendedLines += added;
            TrimToCap();
            LogBox.ScrollToEnd();
        }
        if (Dispatcher.CheckAccess()) Append();
        else Dispatcher.Invoke(Append);
    }

    /// <summary>
    /// The engine output of a patch run is verbose and the panel is never cleared on its own, so the
    /// text would otherwise grow for the whole session. Lines are counted here instead of read from
    /// <c>TextBox.LineCount</c>: that one is layout based and reports 1 while the panel is not rendered.
    /// </summary>
    private void TrimToCap()
    {
        if (_appendedLines <= MaxLogLines + TrimSlackLines)
            return;

        var lines = LogBox.Text.Split('\n');
        // Split keeps one empty trailing entry: every append ends with a newline.
        LogBox.Text = string.Join('\n', lines[Math.Max(0, lines.Length - 1 - MaxLogLines)..]);
        _appendedLines = MaxLogLines;
    }

    /// <summary>Clears the log area.</summary>
    public void ClearLog()
    {
        void Clear()
        {
            LogBox.Clear();
            _appendedLines = 0;
        }
        if (Dispatcher.CheckAccess()) Clear();
        else Dispatcher.Invoke(Clear);
    }
}
