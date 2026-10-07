using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PoEToolbox.Abstractions;
using PoEToolbox.Shared;
using PoEToolbox.Ui;

namespace PoEToolbox.App;

/// <summary>
/// Dashboard 首页。抄PowerToys 的两级信息层次：先一屏卡片让人知道每个模块会改什么，
/// 再由用户决定进哪一页。卡片本身不做任何业务动作，只发 <see cref="ModuleRequested"/>，
/// 由 <see cref="MainWindow"/> 去切导航 —— 这样「切页」这件事只有一处实现。
/// </summary>
public partial class DashboardView : UserControl
{
    private readonly Func<IReadOnlyList<ModuleCard>> _cardsProvider;
    private readonly Func<PoeGameKind> _gameProvider;
    private readonly Func<bool> _isRunningProvider;
    private readonly Func<string?> _leagueProvider;

    /// <summary>用户点了某张卡片。参数是该模块在导航里的名字。</summary>
    public event Action<string>? ModuleRequested;

    public DashboardView(
        Func<IReadOnlyList<ModuleCard>> cardsProvider,
        Func<PoeGameKind> gameProvider,
        Func<bool> isRunningProvider,
        Func<string?> leagueProvider)
    {
        _cardsProvider = cardsProvider;
        _gameProvider = gameProvider;
        _isRunningProvider = isRunningProvider;
        _leagueProvider = leagueProvider;

        InitializeComponent();
        Refresh();
    }

    /// <summary>客户端类型 / 运行状态 / 联赛变化时由 MainWindow 调一次。</summary>
    public void Refresh()
    {
        Header.HeaderText = UILabels.Get("DashboardTitle");
        Header.Description = UILabels.Get("DashboardIntro");

        var cards = _cardsProvider();
        Cards.ItemsSource = cards;
        EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Text = UILabels.Get("DashboardNoMatch");

        UpdateClientState();
        UpdateSelectButton();
    }

    /// <summary>换语言后刷新文案（卡片内容来自插件的 Name/Summary，不在此列）。</summary>
    public void ApplyLocalization()
    {
        Refresh();
        SelectGameDataButton.Content = UILabels.Get("SelectGameData");
    }

    private void UpdateClientState()
    {
        var game = _gameProvider();
        var hasClient = game != PoeGameKind.Unknown;

        GameStateText.Text = hasClient
            ? string.Format(UILabels.Get("DashboardClientReady"), GameLabel(game))
            : UILabels.Get("DashboardClientMissing");

        GameStateHint.Text = hasClient
            ? string.Format(
                UILabels.Get("DashboardClientReadyHint"),
                UILabels.Get(_isRunningProvider() ? "ShellRunning" : "ShellNotRunning"),
                _leagueProvider() ?? UILabels.Get("ShellLeagueUnknown"))
            : UILabels.Get("DashboardClientMissingHint");

        // 图标与底色跟着状态走：没选客户端时用中性色，避免看起来像已经就绪。
        GameBadgeIcon.SetResourceReference(TextBlock.ForegroundProperty,
            hasClient ? "AccentBrush" : "TextTertiaryBrush");
        GameBadgePlate.SetResourceReference(Border.BackgroundProperty,
            hasClient ? "AccentSubtleBrush" : "SurfaceBrush");
    }

    private void UpdateSelectButton()
    {
        // 未选客户端时主按钮高亮：这是全局唯一的前置条件，值得抢注意力。
        var hasClient = _gameProvider() != PoeGameKind.Unknown;
        SelectGameDataButton.Style = (Style)FindResource(
            hasClient ? "SecondaryButton" : "PrimaryButton");
        SelectGameDataButton.Content = UILabels.Get("SelectGameData");
    }

    private static string GameLabel(PoeGameKind game) => game switch
    {
        PoeGameKind.Poe1 => "PoE1",
        PoeGameKind.Poe2 => "PoE2",
        _ => UILabels.Get("ShellGameUnknown"),
    };

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModuleCard card })
            return;

        if (!card.IsEnabled)
            return;

        ModuleRequested?.Invoke(card.Name);
    }

    private void SelectGameDataButton_Click(object sender, RoutedEventArgs e)
        => SelectGameDataRequested?.Invoke();

    /// <summary>首页上的「选择游戏数据」按钮。真正选文件的对话框在 MainWindow（它管 GameDataAccess）。</summary>
    public event Action? SelectGameDataRequested;
}

/// <summary>
/// 一张模块卡片。<see cref="IsEnabled"/> 与左导航的禁用判定是同一个来源
/// （MainWindow.IsPluginAvailable），两处不会给出矛盾的状态。
/// </summary>
public sealed class ModuleCard : INotifyPropertyChanged
{
    private readonly Func<bool> _isEnabledProvider;

    public ModuleCard(
        string name,
        string iconGlyph,
        string summary,
        Func<bool> isEnabledProvider)
    {
        Name = name;
        IconGlyph = iconGlyph;
        Summary = summary;
        _isEnabledProvider = isEnabledProvider;
    }

    public string Name { get; }
    public string IconGlyph { get; }
    public string Summary { get; }

    public string UnavailableHint => UILabels.Get("DashboardCardUnavailable");

    public bool IsEnabled => _isEnabledProvider();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>客户端类型变化时刷新可用态（换客户端会让一批模块从不可用变可用）。</summary>
    public void RaiseAvailabilityChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));

    public override string ToString() => Name;
}
