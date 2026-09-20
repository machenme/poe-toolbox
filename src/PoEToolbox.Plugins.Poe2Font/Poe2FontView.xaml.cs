using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PoEToolbox.Shared;
using PoEToolbox.Ui;
using PoEToolbox.Abstractions;

namespace PoEToolbox.Plugins.Poe2Font;

public partial class Poe2FontView : UserControl
{
    private const string SampleNote = "示例文字按写入后的字号渲染。";

    private readonly ObservableCollection<Poe2FontPreviewRow> _rows = [];
    private ListCollectionView? _rowsView;
    private bool _initialized;
    private readonly IEventBus _eventBus;
    private string? _gameDataPath;
    private PoeGameKind _gameKind;

    private sealed record FontOption(string DisplayName, string RenderName, FontFamily FontFamily);

    private sealed record ScaleOption(string Label, double Width);

    public Poe2FontView(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        InitializeComponent();
        _eventBus.Subscribe<GameContextChanged>(OnGameContextChanged);

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

        ResolutionBox.ItemsSource = new ScaleOption[]
        {
            new("2560 宽（官方基准）", 2560),
            new("1920 宽（1080p）", 1920),
            new("1600 宽", 1600),
            new("1280 宽", 1280),
            new("3840 宽（4K）", 3840),
        };
        ResolutionBox.SelectedIndex = 0;

        foreach (var entry in Poe2FontTemplate.Default.Entries)
            _rows.Add(new Poe2FontPreviewRow(entry, OnRowEdited));

        _rowsView = new ListCollectionView(_rows)
        {
            Filter = o => o is Poe2FontPreviewRow row
                && (ChangedOnlyBox?.IsChecked != true || row.IsChanged)
                && row.Matches(SearchBox?.Text ?? string.Empty),
        };
        EntryList.ItemsSource = _rowsView;

        _initialized = true;
        UpdatePreview();
    }

    public void Dispose() => _eventBus.Unsubscribe<GameContextChanged>(OnGameContextChanged);

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

    private void OnGameContextChanged(GameContextChanged context)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGameContextChanged(context));
            return;
        }

        _gameDataPath = context.GameDataPath;
        _gameKind = context.Game;
        ValidatePath();
    }

    private void FontSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            UpdatePreview();
    }

    private void TypefaceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
            UpdatePreview();
    }

    private void IncreaseOffset_Click(object sender, RoutedEventArgs e) => ApplyOffsetFromBox(1);

    private void DecreaseOffset_Click(object sender, RoutedEventArgs e) => ApplyOffsetFromBox(-1);

    private void ApplyOffsetFromBox(int direction)
    {
        if (!TryReadInt(OffsetBox?.Text, out var magnitude))
            return;

        CommitBatch(Poe2FontBatch.Shift(_rows, direction * magnitude), "整体偏移");
    }

    private void ApplyPercent_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInt(PercentBox?.Text, out var percent) || percent is < 10 or > 500)
        {
            PreviewStatus.Text = "等比值需在 10 到 500 之间";
            return;
        }

        CommitBatch(Poe2FontBatch.ScalePercent(_rows, percent), "等比缩放");
    }

    private void ResetSizes_Click(object sender, RoutedEventArgs e)
        => CommitBatch(Poe2FontBatch.ResetToOfficial(_rows), "已还原官方字号");

    private void CommitBatch(int affected, string label)
    {
        _rowsView?.Refresh();
        UpdatePreview();
        StatusText.Text = $"{label}：影响 {affected} 项。";
    }

    private void OnRowEdited(Poe2FontPreviewRow row)
    {
        row.UpdatePreviewSize(SelectedWidth(), DeviceScale, Poe2FontTemplate.Default.BaseResolution);
        UpdatePreview();
    }

    private void OffsetBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
            UpdateOffsetHint();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _rowsView?.Refresh();

    private void ChangedOnly_Changed(object sender, RoutedEventArgs e) => _rowsView?.Refresh();

    private void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private double SelectedWidth()
        => ResolutionBox.SelectedItem is ScaleOption option ? option.Width : Poe2FontTemplate.Default.BaseResolution;

    private double DeviceScale
        => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

    private IReadOnlyDictionary<string, int> CurrentSizes() => Poe2FontBatch.ToSizeMap(_rows);

    private void UpdatePreview()
    {
        if (!_initialized)
            return;

        var typeface = GetSelectedTypeface();
        var fontFamily = ResolveFontFamily(typeface);
        EntryList.FontFamily = fontFamily ?? SystemFonts.MessageFontFamily;
        foreach (var row in _rows)
            row.UpdatePreviewSize(SelectedWidth(), DeviceScale, Poe2FontTemplate.Default.BaseResolution);

        var changed = CurrentSizes();
        PreviewText.Text = string.IsNullOrWhiteSpace(typeface)
            ? "请输入字体类型"
            : $"{typeface} · 相对官方改动 {changed.Count} 项";
        var brush = FindResource(string.IsNullOrWhiteSpace(typeface) || fontFamily is null
            ? "WarningBrush"
            : "TextPrimaryBrush") as Brush;
        PreviewText.Foreground = brush;
        PreviewStatus.Text = fontFamily is null
            ? $"本机未安装「{typeface}」，下方仍用系统字体显示"
            : $"{_rows.Count} 条 · {SampleNote}";
        PreviewStatus.Foreground = brush;
        UpdateOffsetHint();
        ValidatePath();
    }

    private void UpdateOffsetHint()
    {
        if (OffsetHint is null)
            return;

        var changed = CurrentSizes();
        OffsetHint.Text = changed.Count == 0
            ? "当前全部保持官方字号。"
            : $"已改 {changed.Count} 项，例如 {Summarize(changed)}。";
    }

    private string Summarize(IReadOnlyDictionary<string, int> changed)
    {
        var first = _rows.FirstOrDefault(row => row.IsChanged);
        return first is null ? "—" : $"{first.Id} {first.OfficialSize}→{first.TargetSize}";
    }

    private FontFamily? ResolveFontFamily(string typeface)
    {
        if (string.IsNullOrWhiteSpace(typeface))
            return null;

        if (TypefaceBox.SelectedItem is FontOption option
            && string.Equals(typeface, option.RenderName, StringComparison.OrdinalIgnoreCase))
            return option.FontFamily;

        try
        {
            var family = new FontFamily(typeface);
            return family.FamilyNames.Count > 0
                || Fonts.SystemFontFamilies.Any(available =>
                    string.Equals(available.Source, typeface, StringComparison.OrdinalIgnoreCase))
                ? family
                : null;
        }
        catch (ArgumentException)
        {
            // 字体名可能来自用户手输，非法时用系统字体回显并标黄，用户看得见，不必记日志。
            return null;
        }
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

    private bool TryReadInt(string? text, out int value)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value);

    private bool TryReadOptions(out Poe2FontOptions options)
    {
        var typeface = GetSelectedTypeface();
        options = new Poe2FontOptions(typeface, CurrentSizes());
        return !string.IsNullOrWhiteSpace(typeface);
    }

    private void ValidatePath()
    {
        if (ApplyButton is null)
            return;

        var path = _gameDataPath ?? GameDataPathPreference.Get() ?? string.Empty;
        var valid = _gameKind == PoeGameKind.Poe2
            && File.Exists(path)
            && (path.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("_.index.bin", StringComparison.OrdinalIgnoreCase));
        PathStatus.Text = valid
            ? "已选择数据文件。应用时会检查 POE2 标识和两个字体 XML。"
            : "请先在主窗口选择 POE2 游戏数据。";
        PathStatus.Foreground = FindResource(valid ? "SuccessBrush" : "WarningBrush") as Brush;
        ApplyButton.IsEnabled = valid && TryReadOptions(out _);
        RestoreButton.IsEnabled = valid && Poe2FontService.HasBaseline(path);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadOptions(out var options))
        {
            MessageBox.Show("请输入有效字体类型。", "配置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var path = GetGameDataPath();
        if (path is null)
        {
            UiStatus.Set(PathStatus, "请先在主窗口选择有效的游戏数据文件。", UiStatus.Kind.Warning);
            return;
        }

        var changed = options.Sizes;
        var preview = string.Join('\n', _rows.Where(row => row.IsChanged).Take(10)
            .Select(row => $"  {row.ScopeDisplay}/{row.Id}：{row.OfficialSize} → {row.TargetSize}"));
        var confirm = MessageBox.Show(
            $"将为当前客户端应用以下字体配置：\n\n字体：{options.Typeface}\n"
            + $"相对官方字号改动：{changed.Count} 项\n"
            + (preview.Length > 0 ? $"\n{preview}{(_rows.Count(row => row.IsChanged) > 10 ? "\n  ……" : "")}\n" : "\n")
            + "\n首次应用会保存两个目标文件的原始内容，并写入新的 Bundle/索引。是否继续？",
            "应用 POE2 字体配置", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
            return;

        SetBusy(true, "正在生成并写入字体配置...");
        try
        {
            var result = await Task.Run(() => Poe2FontService.Apply(path, options));
            ResultText.Text = $"✅ 已应用：{result.Typeface}，相对官方改动 {result.Changes.Count} 项。"
                + (result.UnknownEntryCount > 0
                    ? $"另有 {result.UnknownEntryCount} 项不在内置模板内，已保持原样。\n"
                    : "\n")
                + $"原始字体备份：{result.BaselinePath}";
            UiStatus.Set(PathStatus, "字体配置已写入。启动游戏前请释放其他工具对游戏数据文件的占用。", UiStatus.Kind.Success);
            FileLogger.App.Info(
                $"Poe2Font applied: typeface={result.Typeface}, changes={result.Changes.Count}, unknown={result.UnknownEntryCount}.");
            RestoreButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            UiStatus.Set(PathStatus, "❌ 字体配置失败，详见错误提示。", UiStatus.Kind.Error);
            FileLogger.App.Error("Poe2Font apply failed.", ex);
            ResultText.Text = ex.Message;
            MessageBox.Show($"字体配置失败：\n{ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var path = GetGameDataPath();
        if (path is null)
        {
            UiStatus.Set(PathStatus, "请先在主窗口选择有效的游戏数据文件。", UiStatus.Kind.Warning);
            return;
        }
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
            MessageBox.Show($"恢复失败：\n{ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        TypefaceBox.IsEnabled = !busy;
        OffsetBox.IsEnabled = !busy;
        PercentBox.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
        EntryList.IsEnabled = !busy;
        var path = _gameDataPath ?? GameDataPathPreference.Get();
        RestoreButton.IsEnabled = !busy && path is not null && Poe2FontService.HasBaseline(path);
    }

    private string? GetGameDataPath()
    {
        var path = _gameDataPath ?? GameDataPathPreference.Get();
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }
}
