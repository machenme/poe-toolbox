using System.Windows;
using System.Windows.Controls;

namespace PoEToolbox.Ui;

/// <summary>
/// 模块页统一页头（图标 + 标题 + 一句说明）。见 <c>PageHeader.xaml</c> 顶部说明。
/// </summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty HeaderTextProperty = DependencyProperty.Register(
        nameof(HeaderText), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnHeaderChanged));

    public PageHeader()
    {
        InitializeComponent();

        // 构造函数里手动跑一次：绑定用的 ElementName 绑定要等加载完才求值，
        // 那时若三个属性都是空串，这块就该是收起的。
        ApplyHeaderState();
    }

    /// <summary>Segoe MDL2 Assets 码位。空值时整块图标底板收起。</summary>
    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public string HeaderText
    {
        get => (string)GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    /// <summary>一句话说明这模块会动什么。空串时不占位。</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>图标底板在 4 个值之间切换：空图标 / 空标题 / 只有图标 / 图标+标题+说明。</summary>
    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as PageHeader)?.ApplyHeaderState();

    private void ApplyHeaderState()
    {
        var hasIcon = !string.IsNullOrWhiteSpace(IconGlyph);
        var hasText = !string.IsNullOrWhiteSpace(HeaderText);
        var hasDescription = !string.IsNullOrWhiteSpace(Description);

        IconPlate.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
        TitleLabel.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        DescriptionLabel.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;

        // 三者全空就整块收起，避免留一条 40px 的空白。
        Panel.Visibility = hasIcon || hasText || hasDescription
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
