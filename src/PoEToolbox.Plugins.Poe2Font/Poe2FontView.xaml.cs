using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.Poe2Font;

public partial class Poe2FontView : UserControl
{
    private bool _updatingSize;
    private bool _initialized;

    private sealed record FontOption(string DisplayName, string RenderName, FontFamily FontFamily);

    public Poe2FontView()
    {
        InitializeComponent();
        var fonts = Fonts.SystemFontFamilies
            .Select(font => new FontOption(GetLocalizedFontName(font), font.Source, font))
            .Where(font => !string.IsNullOrWhiteSpace(font.DisplayName))
            .DistinctBy(font => font.RenderName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(font => font.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        TypefaceBox.ItemsSource = fonts;
        var defaultFont = fonts.FirstOrDefault(font =>
            string.Equals(font.RenderName, Poe2FontService.TypefacePresets[0], StringComparison.OrdinalIgnoreCase))
            ?? fonts.FirstOrDefault(font =>
                string.Equals(font.RenderName, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase));
        if (defaultFont is not null)
            TypefaceBox.SelectedItem = defaultFont;
        else
            TypefaceBox.Text = Poe2FontService.TypefacePresets[0];

        TypefaceBox.AddHandler(TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(TypefaceBox_TextChanged));
        _initialized = true;
        UpdatePreview();
    }

    private static string GetLocalizedFontName(FontFamily font)
    {
        var preferredCultures = new[]
        {
            CultureInfo.CurrentUICulture,
            CultureInfo.CurrentCulture,
            CultureInfo.GetCultureInfo("zh-CN"),
            CultureInfo.GetCultureInfo("zh-TW"),
            CultureInfo.GetCultureInfo("en-US"),
        };

        foreach (var culture in preferredCultures)
        {
            var name = font.FamilyNames.FirstOrDefault(entry =>
                string.Equals(entry.Key.GetEquivalentCulture().Name, culture.Name,
                    StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return font.FamilyNames.Values.FirstOrDefault() ?? font.Source;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "POE2 数据|Content.ggpk;_.index.bin|GGPK|Content.ggpk|Bundles2 索引|_.index.bin",
            Title = "选择 POE2 Content.ggpk 或 _.index.bin",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            GameDataPathBox.Text = dialog.FileName;
            ValidatePath();
        }
    }

    private void GameDataPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
            return;
        ValidatePath();
    }

    private void FontSettingChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingSize)
            return;

        if (ReferenceEquals(sender, SizeScaleBox)
            && TryReadScale(out var scale)
            && scale >= SizeScaleSlider.Minimum
            && scale <= SizeScaleSlider.Maximum)
        {
            _updatingSize = true;
            SizeScaleSlider.Value = scale;
            _updatingSize = false;
        }

        UpdatePreview();
    }

    private void TypefaceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_updatingSize)
            UpdatePreview();
    }

    private void SizeScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || SizeScaleBox is null || _updatingSize)
            return;

        _updatingSize = true;
        SizeScaleBox.Text = e.NewValue.ToString("0", CultureInfo.InvariantCulture);
        _updatingSize = false;
        UpdatePreview();
    }

    private void ValidatePath()
    {
        var path = GameDataPathBox?.Text.Trim() ?? string.Empty;
        var valid = File.Exists(path)
            && (path.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("_.index.bin", StringComparison.OrdinalIgnoreCase));
        PathStatus.Text = valid
            ? "已选择数据文件。应用时会检查 POE2 标识和两个字体 XML。"
            : "请选择 Content.ggpk 或 Bundles2\\_.index.bin。";
        PathStatus.Foreground = FindResource(valid ? "SuccessBrush" : "WarningBrush") as System.Windows.Media.Brush;
        ApplyButton.IsEnabled = valid && TryReadOptions(out _);
        RestoreButton.IsEnabled = valid && Poe2FontService.HasBaseline(path);
    }

    private void UpdatePreview()
    {
        var typeface = GetSelectedTypeface();
        var validScale = TryReadScale(out var value) && value is >= 25 and <= 300;
        var scale = validScale ? value : 100;
        PreviewText.Text = string.IsNullOrWhiteSpace(typeface)
            ? "请输入字体类型"
            : $"{typeface} · {scale:0.##}%";
        PreviewText.Foreground = FindResource(string.IsNullOrWhiteSpace(typeface)
            ? "ErrorBrush"
            : "TextPrimaryBrush") as System.Windows.Media.Brush;
        ApplyPreviewFont(typeface, scale, validScale);
        if (GameDataPathBox is not null)
            ValidatePath();
    }

    private string GetSelectedTypeface()
    {
        if (TypefaceBox?.SelectedItem is FontOption font
            && string.Equals(TypefaceBox.Text.Trim(), font.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return font.RenderName;
        }

        return TypefaceBox?.Text.Trim() ?? string.Empty;
    }

    private void ApplyPreviewFont(string typeface, double scale, bool validScale)
    {
        var fontOption = TypefaceBox.SelectedItem as FontOption;
        FontFamily? fontFamily = fontOption is not null
            && string.Equals(typeface, fontOption.RenderName, StringComparison.OrdinalIgnoreCase)
            ? fontOption.FontFamily
            : null;
        if (fontFamily is null && !string.IsNullOrWhiteSpace(typeface))
        {
            try
            {
                fontFamily = new FontFamily(typeface);
            }
            catch (ArgumentException)
            {
            }
        }

        var previewBrush = FindResource(fontFamily is null ? "WarningBrush" : "TextPrimaryBrush") as Brush;
        var fallbackFont = SystemFonts.MessageFontFamily;
        foreach (var preview in new[] { PreviewLarge, PreviewNormal, PreviewSmall, PreviewTiny })
        {
            preview.FontFamily = fontFamily ?? fallbackFont;
            preview.Foreground = previewBrush;
        }

        PreviewLarge.FontSize = ScalePreviewSize(38, scale, validScale);
        PreviewNormal.FontSize = ScalePreviewSize(28, scale, validScale);
        PreviewSmall.FontSize = ScalePreviewSize(22, scale, validScale);
        PreviewTiny.FontSize = ScalePreviewSize(17, scale, validScale);
        PreviewStatus.Text = fontFamily is null
            ? "字体不可用"
            : validScale ? "正在使用系统字体预览" : "字号倍率无效";
        PreviewStatus.Foreground = previewBrush;
    }

    private static double ScalePreviewSize(double baseSize, double scale, bool validScale)
        => baseSize * (validScale ? scale : 100) / 100.0;

    private bool TryReadOptions(out Poe2FontOptions options)
    {
        options = new Poe2FontOptions(GetSelectedTypeface(), 100);
        if (string.IsNullOrWhiteSpace(options.Typeface) || !TryReadScale(out var scale))
            return false;

        options = options with { SizeScalePercent = scale };
        return scale is >= 25 and <= 300;
    }

    private bool TryReadScale(out double scale)
    {
        var text = SizeScaleBox?.Text.Trim() ?? string.Empty;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out scale);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadOptions(out var options))
        {
            MessageBox.Show("请输入有效字体类型，并将字号倍率设置为 25 到 300。", "配置无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = GameDataPathBox.Text.Trim();
        var confirm = MessageBox.Show(
            $"将为 POE2 应用以下字体配置：\n\n字体：{options.Typeface}\n字号倍率：{options.SizeScalePercent:0.##}%\n\n"
            + "首次应用会保存两个目标文件的原始内容，并写入新的 Bundle/索引。是否继续？",
            "应用 POE2 字体配置", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        SetBusy(true, "正在生成并写入字体配置...");
        try
        {
            var result = await Task.Run(() => Poe2FontService.Apply(path, options));
            ResultText.Text = $"✅ 已应用：{result.Typeface}，字号倍率 {result.SizeScalePercent:0.##}%。\n"
                + $"原始字体备份：{result.BaselinePath}";
            UiStatus.Set(PathStatus, "字体配置已写入。启动游戏前请释放其他工具对游戏数据文件的占用。", UiStatus.Kind.Success);
            FileLogger.App.Info($"Poe2Font applied: typeface={result.Typeface}, scale={result.SizeScalePercent:0.##}%.");
            RestoreButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            UiStatus.Set(PathStatus, "❌ 字体配置失败，详见错误提示。", UiStatus.Kind.Error);
            FileLogger.App.Error("Poe2Font apply failed.", ex);
            ResultText.Text = ex.Message;
            MessageBox.Show($"字体配置失败：\n{ex.Message}", "操作失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var path = GameDataPathBox.Text.Trim();
        var confirm = MessageBox.Show(
            "将恢复首次使用字体功能时保存的两个原始 XML。当前字体配置会被移除，是否继续？",
            "恢复原始字体", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        SetBusy(true, "正在恢复原始字体...");
        try
        {
            var result = await Task.Run(() => Poe2FontService.Restore(path));
            ResultText.Text = $"✅ 已恢复原始字体文件。备份位置：{result.BaselinePath}";
            FileLogger.App.Info("Poe2Font restored original fonts.");
            RestoreButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            UiStatus.Set(PathStatus, "❌ 恢复失败，详见错误提示。", UiStatus.Kind.Error);
            FileLogger.App.Error("Poe2Font restore failed.", ex);
            ResultText.Text = ex.Message;
            MessageBox.Show($"恢复失败：\n{ex.Message}", "操作失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        GameDataPathBox.IsEnabled = !busy;
        TypefaceBox.IsEnabled = !busy;
        SizeScaleBox.IsEnabled = !busy;
        SizeScaleSlider.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy && Poe2FontService.HasBaseline(GameDataPathBox.Text.Trim());
    }
}
