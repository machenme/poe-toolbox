using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Globalization;
using PoEToolbox.Core.Assets;

namespace PoEToolbox.Plugins.DataBrowser;

public partial class MapNumberSettingsView : UserControl
{
    private byte[]? _sourceDds;
    private readonly int _mapCount;
    private readonly int _previewNumber;
    private readonly DdsRgbColor[] _colors = CreateDefaultColors();

    public float SelectedFontSize => (float)FontSizeSlider.Value;
    public float SelectedOffsetX => (float)OffsetXSlider.Value;
    public float SelectedOffsetY => (float)OffsetYSlider.Value;
    public string SelectedFontFamily => (FontFamilyComboBox.SelectedItem as FontOption)?.RenderName ?? "Arial";
    public IReadOnlyList<DdsRgbColor> SelectedColors => _colors.ToArray();

    public MapNumberSettingsView(
        byte[] sourceDds,
        int mapCount,
        float defaultOffsetX,
        float defaultOffsetY)
    {
        if (mapCount is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(mapCount));

        _mapCount = mapCount;
        _previewNumber = mapCount;
        InitializeComponent();
        var fontFamilies = Fonts.SystemFontFamilies
            .Select(font => new FontOption(GetLocalizedFontName(font), font.Source))
            .Where(font => !string.IsNullOrWhiteSpace(font.DisplayName))
            .DistinctBy(font => font.RenderName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(font => font.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        FontFamilyComboBox.ItemsSource = fontFamilies;
        FontFamilyComboBox.SelectedItem = fontFamilies.FirstOrDefault(
            font => string.Equals(font.RenderName, "Arial", StringComparison.OrdinalIgnoreCase));
        PreviewNumberComboBox.ItemsSource = Enumerable.Range(1, _mapCount).ToList();
        PreviewNumberComboBox.SelectedItem = _previewNumber;
        TargetDescription.Text = $"调整参数后，{_mapCount} 个地图数字及其低 mip 将统一使用当前设置。";
        OffsetXSlider.Value = defaultOffsetX;
        OffsetYSlider.Value = defaultOffsetY;
        BackgroundImage.Source = LoadMapBackground();
        _sourceDds = sourceDds;
        UpdatePreview();
    }

    private static DdsRgbColor[] CreateDefaultColors()
    {
        var colors = new DdsRgbColor[16];
        for (var number = 1; number <= colors.Length; number++)
        {
            colors[number - 1] = number <= 5
                ? new DdsRgbColor(255, 255, 255)
                : number <= 10
                    ? new DdsRgbColor(255, 255, 0)
                    : new DdsRgbColor(255, 0, 0);
        }
        return colors;
    }

    private static string GetLocalizedFontName(FontFamily font)
    {
        var preferredCultures = new[]
        {
            CultureInfo.GetCultureInfo("zh-CN"),
            CultureInfo.GetCultureInfo("zh-TW"),
            CultureInfo.CurrentUICulture,
            CultureInfo.CurrentCulture,
            CultureInfo.GetCultureInfo("en-US"),
        };

        foreach (var preferredCulture in preferredCultures)
        {
            var localizedName = font.FamilyNames.FirstOrDefault(entry =>
            {
                var entryCulture = entry.Key.GetEquivalentCulture();
                return string.Equals(entryCulture.Name, preferredCulture.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        entryCulture.TwoLetterISOLanguageName,
                        preferredCulture.TwoLetterISOLanguageName,
                        StringComparison.OrdinalIgnoreCase);
            }).Value;
            if (!string.IsNullOrWhiteSpace(localizedName))
                return localizedName;
        }

        return font.Source;
    }

    private sealed record FontOption(string DisplayName, string RenderName);

    private static BitmapImage? LoadMapBackground()
    {
        var uri = new Uri(
            "pack://application:,,,/PoEToolbox.Plugins.DataBrowser;component/Assets/mapBackground.png",
            UriKind.Absolute);
        var resource = Application.GetResourceStream(uri);
        if (resource is null)
            return null;

        using var stream = resource.Stream;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeText is not null)
            FontSizeText.Text = $"{e.NewValue:0} px";
        if (IsInitialized && _sourceDds is not null)
            UpdatePreview();
    }

    private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender == OffsetXSlider && OffsetXText is not null)
            OffsetXText.Text = $"{e.NewValue:0} px";
        else if (sender == OffsetYSlider && OffsetYText is not null)
            OffsetYText.Text = $"{e.NewValue:0} px";
        if (IsInitialized && _sourceDds is not null)
            UpdatePreview();
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized && _sourceDds is not null && FontFamilyComboBox.SelectedItem is FontOption)
            UpdatePreview();
    }

    private void PreviewNumberComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewNumberComboBox.SelectedItem is not int)
            return;

        if (IsInitialized && _sourceDds is not null)
            UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_sourceDds is null)
            return;

        var rendered = DdsMapNumberRenderer.Render(
            _sourceDds,
            SelectedPreviewNumber,
            SelectedFontSize,
            SelectedOffsetX,
            SelectedOffsetY,
            SelectedFontFamily,
            _colors[SelectedPreviewNumber - 1]);
        var (width, height) = DdsMapNumberRenderer.GetDimensions(rendered);
        var pixels = DdsMapNumberRenderer.GetPreviewPixels(rendered);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            checked(width * 4));
        bitmap.Freeze();
        PreviewImage.Source = bitmap;
    }

    private int SelectedPreviewNumber
        => PreviewNumberComboBox.SelectedItem is int number ? number : _previewNumber;

}
