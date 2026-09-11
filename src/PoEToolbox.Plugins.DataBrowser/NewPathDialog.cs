using System.Windows;
using System.Windows.Controls;

namespace PoEToolbox.Plugins.DataBrowser;

/// <summary>Minimal modal text-input dialog used to ask for a new virtual file path.</summary>
internal static class NewPathDialog
{
    /// <summary>Shows a modal input dialog and returns the trimmed input, or null when cancelled.</summary>
    public static string? Prompt(Window? owner, string title, string message, string initialValue)
    {
        var window = new Window
        {
            Title = title,
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        if (owner is not null)
            window.Owner = owner;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var textBox = new TextBox
        {
            Text = initialValue,
            Height = 32,
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        textBox.SelectAll();
        panel.Children.Add(textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button { Content = "确定", Width = 88, Height = 30, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 88, Height = 30, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;

        string? result = null;
        ok.Click += (_, _) =>
        {
            result = textBox.Text.Trim();
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => textBox.Focus();

        return window.ShowDialog() == true ? result : null;
    }
}
