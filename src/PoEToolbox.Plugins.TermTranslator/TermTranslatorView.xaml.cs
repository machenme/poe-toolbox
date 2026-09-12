using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PoEToolbox.Core.Translation;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.TermTranslator;

public partial class TermTranslatorView : UserControl
{
    private const string ConfigKey = "TermTranslator";
    private const double MinTranslationFontSize = 9;
    private const double MaxTranslationFontSize = 24;
    private const double FontSizeStep = 1;
    private readonly TermTranslationService _translationService;
    private readonly TermTranslatorConfig _config;
    private CancellationTokenSource? _translationCts;
    private int _translationVersion;
    private string _translationText = string.Empty;

    private sealed record LanguageOption(TranslationLanguage Language, string Name);

    public TermTranslatorView()
    {
        InitializeComponent();
        _config = ConfigService.GetPluginConfig<TermTranslatorConfig>(ConfigKey)
            ?? new TermTranslatorConfig();
        _config.TranslationFontSize = Math.Clamp(
            _config.TranslationFontSize,
            MinTranslationFontSize,
            MaxTranslationFontSize);
        ApplyTranslationFontSize();
        _translationService = new TermTranslationService(
            BaseItemTermCatalog.LoadDefault(),
            new EdgeTranslationClient());

        var languages = new[]
        {
            new LanguageOption(TranslationLanguage.English, "English"),
            new LanguageOption(TranslationLanguage.SimplifiedChinese, "简体中文"),
            new LanguageOption(TranslationLanguage.TraditionalChinese, "繁體中文"),
        };
        SourceLanguageBox.ItemsSource = languages;
        TargetLanguageBox.ItemsSource = languages;
        SourceLanguageBox.SelectedIndex = 0;
        TargetLanguageBox.SelectedIndex = 1;
        UpdateLanguageStatus();
    }

    public void CancelTranslation()
    {
        _translationCts?.Cancel();
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var sourceText = SourceTextBox.Text;
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            SetStatus("请输入需要翻译的原文。", "WarningBrush");
            return;
        }

        if (!TryGetLanguages(out var source, out var target))
            return;

        CancelTranslation();
        var cts = new CancellationTokenSource();
        _translationCts = cts;
        var version = ++_translationVersion;
        SetBusy(true);
        SetStatus("正在保护 PoE 术语并翻译...", "TextSecondaryBrush");

        try
        {
            var result = await _translationService.TranslateAsync(sourceText, source, target, cts.Token);
            if (version != _translationVersion || cts.IsCancellationRequested)
                return;

            RenderTranslation(result);
            SetStatus($"✅ 完成：已保护 {result.ProtectedTermCount} 个 PoE 术语，使用 {result.ChunkCount} 个翻译片段。", "SuccessBrush");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (version == _translationVersion)
                SetStatus("翻译已取消。", "WarningBrush");
        }
        catch (Exception ex)
        {
            if (version == _translationVersion)
                SetStatus($"❌ 翻译失败：{ex.Message}", "ErrorBrush");
        }
        finally
        {
            if (version == _translationVersion)
            {
                SetBusy(false);
            }

            if (ReferenceEquals(_translationCts, cts))
                _translationCts = null;
            cts.Dispose();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _translationVersion++;
        CancelTranslation();
        SetBusy(false);
        SetStatus("翻译已取消。", "WarningBrush");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Cancel_Click(sender, e);
        SourceTextBox.Clear();
        ClearTranslation();
        SetStatus("就绪。原文将发送至 Microsoft Edge 在线翻译服务。", "TextSecondaryBrush");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_translationText))
            return;

        try
        {
            Clipboard.SetText(_translationText);
            SetStatus("译文已复制。", "SuccessBrush");
        }
        catch (Exception ex)
        {
            SetStatus($"无法复制译文：{ex.Message}", "ErrorBrush");
        }
    }

    private void DecreaseFontSize_Click(object sender, RoutedEventArgs e)
        => ChangeTranslationFontSize(-FontSizeStep);

    private void IncreaseFontSize_Click(object sender, RoutedEventArgs e)
        => ChangeTranslationFontSize(FontSizeStep);

    private void ChangeTranslationFontSize(double delta)
    {
        var nextSize = Math.Clamp(
            _config.TranslationFontSize + delta,
            MinTranslationFontSize,
            MaxTranslationFontSize);
        if (nextSize == _config.TranslationFontSize)
            return;

        _config.TranslationFontSize = nextSize;
        ApplyTranslationFontSize();
        ConfigService.SavePluginConfig(ConfigKey, _config);
    }

    private void ApplyTranslationFontSize()
    {
        SourceTextBox.FontSize = _config.TranslationFontSize;
        TranslationTextBox.FontSize = _config.TranslationFontSize;
        FontSizeText.Text = $"{_config.TranslationFontSize:0}";
        DecreaseFontSizeButton.IsEnabled = _config.TranslationFontSize > MinTranslationFontSize;
        IncreaseFontSizeButton.IsEnabled = _config.TranslationFontSize < MaxTranslationFontSize;
    }

    private void SwapLanguages_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceLanguageBox.SelectedItem;
        SourceLanguageBox.SelectedItem = TargetLanguageBox.SelectedItem;
        TargetLanguageBox.SelectedItem = source;
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLanguageStatus();

    private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_translationCts is not null)
            Cancel_Click(sender, e);
    }

    private bool TryGetLanguages(out TranslationLanguage source, out TranslationLanguage target)
    {
        source = default;
        target = default;
        if (SourceLanguageBox.SelectedItem is not LanguageOption sourceOption
            || TargetLanguageBox.SelectedItem is not LanguageOption targetOption)
        {
            SetStatus("请选择源语言和目标语言。", "WarningBrush");
            return false;
        }

        source = sourceOption.Language;
        target = targetOption.Language;
        if (source == target)
        {
            SetStatus("源语言和目标语言不能相同。", "WarningBrush");
            return false;
        }
        return true;
    }

    private void UpdateLanguageStatus()
    {
        if (SourceLanguageBox is null || TargetLanguageBox is null)
            return;

        LanguageStatus.Text = SourceLanguageBox.SelectedItem is LanguageOption source
            && TargetLanguageBox.SelectedItem is LanguageOption target
            ? $"{source.Name} -> {target.Name}"
            : string.Empty;
    }

    private void SetBusy(bool isBusy)
    {
        TranslateButton.IsEnabled = !isBusy;
        CancelButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        SourceLanguageBox.IsEnabled = !isBusy;
        TargetLanguageBox.IsEnabled = !isBusy;
    }

    private void SetStatus(string text, string brushKey)
    {
        var kind = brushKey switch
        {
            "SuccessBrush" => UiStatus.Kind.Success,
            "WarningBrush" => UiStatus.Kind.Warning,
            "ErrorBrush" => UiStatus.Kind.Error,
            _ => UiStatus.Kind.Neutral,
        };
        UiStatus.Set(StatusText, text, kind);
    }

    private void ClearTranslation()
    {
        _translationText = string.Empty;
        TranslationTextBox.Document.Blocks.Clear();
        TranslationTextBox.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
    }

    private void RenderTranslation(TermTranslationResult result)
    {
        _translationText = result.Translation;
        TranslationTextBox.Document.Blocks.Clear();
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var cursor = 0;

        foreach (var span in result.TermSpans)
        {
            paragraph.Inlines.Add(new Run(result.Translation[cursor..span.Start]));
            var term = new Run(result.Translation.Substring(span.Start, span.Length))
            {
                Foreground = FindResource("AccentBrush") as Brush,
                TextDecorations = TextDecorations.Underline,
                ToolTip = "内置词典术语",
            };
            paragraph.Inlines.Add(term);
            cursor = span.Start + span.Length;
        }

        paragraph.Inlines.Add(new Run(result.Translation[cursor..]));
        TranslationTextBox.Document.Blocks.Add(paragraph);
    }
}
