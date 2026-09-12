using PoEToolbox.Sdk;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.ComponentModel;
using System.Windows.Data;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using PoEToolbox.Shared;

namespace PoEToolbox.App;

public partial class MainWindow : Window
{
    private const string ReleasesUrl = "https://github.com/machenme/poe-toolbox/releases/";
    private readonly PluginManager _pluginManager;
    private readonly IAppState _sessionState;
    private IPlugin? _activePlugin;

    public MainWindow()
    {
        InitializeComponent();

        _pluginManager = new PluginManager(new EventBus());
        _sessionState = _pluginManager.SessionState;
        _pluginManager.EventBus.Subscribe<GameContextChanged>(OnGameContextChanged);
        _pluginManager.EventBus.Subscribe<LeagueChanged>(OnLeagueChanged);
        GameDataAccess.LocksChanged += OnGameDataLocksChanged;
        _pluginManager.RegisterAll();
        NavList.ItemsSource = CreateNavigationView();
        ApplyLocalization();
        _pluginManager.EventBus.Publish(new GameContextChanged(
            PoeGameKind.Unknown, null, PoeDetector.Default.IsPoeRunning()));
        SourceInitialized += (_, _) => InitializeGlobalPluginHotkeys();

        // Show disclaimer on first run
        if (!DisclaimerPage.IsAccepted())
        {
            var page = new DisclaimerPage();
            page.Accepted += () =>
            {
                DisclaimerOverlay.Visibility = Visibility.Collapsed;
                if (NavList.Items.Count > 0) NavList.SelectedIndex = 0;
            };
            page.Declined += () => Application.Current.Shutdown();
            DisclaimerOverlay.Child = page;
            DisclaimerOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            if (NavList.Items.Count > 0) NavList.SelectedIndex = 0;
        }

        ApplyVersionState(UpdateChecker.GetCachedResult());
        _ = LoadUpdateStateAsync();

        // Mirror the file log (all modules' operations) into the global log dock.
        FileLogger.EntryLogged += OnFileLogEntry;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavigationEntry { Plugin: { } plugin }) return;

        _activePlugin?.OnDeactivated();
        _activePlugin = plugin;
        try
        {
            FileLogger.App.Info($"Plugin activated: {plugin.Name}");
            PluginContent.Content = plugin.CreateView();
            _activePlugin.OnActivated();
        }
        catch (Exception ex)
        {
            var msg = $"{plugin.Name}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            FileLogger.WriteCritical(msg, ex);
            MessageBox.Show(msg, "Plugin Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InitializeGlobalPluginHotkeys()
    {
        var bagCleaner = _pluginManager.Plugins
            .OfType<PoEToolbox.Plugins.BagCleaner.BagCleanerPlugin>()
            .FirstOrDefault();
        if (bagCleaner == null) return;

        bagCleaner.InitializeHotkeys(new WindowInteropHelper(this).Handle);
    }

    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current switch
        {
            ThemeManager.Theme.Light => ThemeManager.Theme.Dark,
            ThemeManager.Theme.Dark => ThemeManager.Theme.FollowSystem,
            _ => ThemeManager.Theme.Light,
        };
        ThemeManager.Apply(next);
        UpdateThemeIcon();
    }

    private void LangBtn_Click(object sender, RoutedEventArgs e)
    {
        var next = UILabels.Current switch
        {
            UILabels.Lang.English => UILabels.Lang.SimplifiedChinese,
            UILabels.Lang.SimplifiedChinese => UILabels.Lang.TraditionalChinese,
            _ => UILabels.Lang.English,
        };
        UILabels.SetLanguage(next);
        ApplyLocalization();
    }

    private void ReleaseGgpkButton_Click(object sender, RoutedEventArgs e)
    {
        _pluginManager.EventBus.Publish(new ReleaseGameDataLocksRequested());
        OnGameDataLocksChanged(GameDataAccess.HasOpenLocks);
        FileLogger.App.Info("User requested release of game data locks.");
        StatusLabel.Text = UILabels.Get("GgpkLocksReleased");
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.DataDirectory);
            Process.Start(new ProcessStartInfo(ConfigService.DataDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical("Failed to open the application data directory.", ex);
        }
    }

    private void UpdateThemeIcon()
    {
        ThemeBtn.Content = ThemeManager.Current switch
        {
            ThemeManager.Theme.Dark => "",
            ThemeManager.Theme.Light => "",
            _ => "",
        };
    }

    private async Task LoadUpdateStateAsync()
    {
        var result = await UpdateChecker.CheckAsync();
        await Dispatcher.InvokeAsync(() => ApplyVersionState(result));
    }

    private void ApplyVersionState(UpdateCheckResult result)
    {
        var current = result.CurrentVersion ?? typeof(App).Assembly.GetName().Version;
        var currentText = current is null
            ? "v?"
            : $"v{FormatVersion(current)}";
        LblVersion.Text = currentText;

        UpdateDot.Visibility = result.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
        if (!result.HasUpdate)
            UpdateDialogOverlay.Visibility = Visibility.Collapsed;
        VersionBtn.ToolTip = result.HasUpdate
            ? UILabels.Get("UpdateAvailableTooltip")
            : UILabels.Get("VersionTooltip");
    }

    private void VersionBtn_Click(object sender, RoutedEventArgs e)
    {
        var cached = UpdateChecker.GetCachedResult();
        if (!cached.HasUpdate || cached.LatestVersion is null)
            return;

        UpdateDialogTitle.Text = UILabels.Get("UpdateDialogTitle");
        UpdateDialogVersion.Text = $"v{FormatVersion(cached.LatestVersion)}";
        UpdateDialogNotes.Text = string.IsNullOrWhiteSpace(cached.Notes)
            ? UILabels.Get("UpdateNotesUnavailable")
            : cached.Notes;
        SkipUpdateButton.Content = string.Format(
            UILabels.Get("SkipVersion"),
            FormatVersion(cached.LatestVersion));
        UpdateDialogOverlay.Visibility = Visibility.Visible;
    }

    private void SkipUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var cached = UpdateChecker.GetCachedResult();
        if (cached.LatestVersion is not null)
            UpdateChecker.SkipVersion(cached.LatestVersion);

        ApplyVersionState(UpdateChecker.GetCachedResult());
    }

    private void OpenReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateDialogOverlay.Visibility = Visibility.Collapsed;
        OpenReleasesPage();
    }

    private static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical("Failed to open update URL.", ex);
        }
    }

    private static string FormatVersion(Version version) => version.Build < 0
        ? $"{version.Major}.{version.Minor}"
        : $"{version.Major}.{version.Minor}.{version.Build}";

    private void ApplyLocalization()
    {
        Title = UILabels.Get("AppTitle");
        LblAppTitle.Text = UILabels.Get("AppTitle");
        LblSubtitle.Text = UILabels.Get("Subtitle");
        OnGameDataLocksChanged(GameDataAccess.HasOpenLocks);
        UpdateShellStatus();

        var lang = UILabels.Current switch
        {
            UILabels.Lang.SimplifiedChinese => "简",
            UILabels.Lang.TraditionalChinese => "繁",
            _ => "EN",
        };
        LangBtn.Content = lang;

        UpdateThemeIcon();
        ApplyVersionState(UpdateChecker.GetCachedResult());
        OpenReleasesButton.Content = UILabels.Get("OpenReleasePage");
        OpenDataFolderButton.Content = UILabels.Get("OpenAppDataFolder");
        OpenDataFolderButton.ToolTip = UILabels.Get("OpenAppDataFolder");

        // Refresh nav list display names
        NavList.Items.Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _pluginManager.EventBus.Unsubscribe<GameContextChanged>(OnGameContextChanged);
            _pluginManager.EventBus.Unsubscribe<LeagueChanged>(OnLeagueChanged);
            GameDataAccess.LocksChanged -= OnGameDataLocksChanged;
            FileLogger.EntryLogged -= OnFileLogEntry;
            _pluginManager.Shutdown();
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical("Failed during application shutdown.", ex);
        }
        finally
        {
            base.OnClosed(e);
            try
            {
                Application.Current.Shutdown();
            }
            catch (InvalidOperationException)
            {
                // The dispatcher may already be shutting down.
            }
        }
    }

    private void OnGameContextChanged(GameContextChanged _) => UpdateShellStatus();

    private void OnLeagueChanged(LeagueChanged _) => UpdateShellStatus();

    private void OnGameDataLocksChanged(bool hasOpenLocks)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGameDataLocksChanged(GameDataAccess.HasOpenLocks));
            return;
        }

        hasOpenLocks = GameDataAccess.HasOpenLocks;
        var label = hasOpenLocks
            ? UILabels.Get("ReleaseGgpkLocks")
            : UILabels.Get("GgpkLocksReleased");
        ReleaseGgpkButton.Content = label;
        ReleaseGgpkButton.ToolTip = label;
    }

    private void UpdateShellStatus()
    {
        var game = _sessionState.Game != PoeGameKind.Unknown
            ? _sessionState.Game
            : _sessionState.LeagueGame;
        var gameText = game switch
        {
            PoeGameKind.Poe1 => "PoE1",
            PoeGameKind.Poe2 => "PoE2",
            _ => UILabels.Get("ShellGameUnknown"),
        };
        var processText = UILabels.Get(_sessionState.IsPoeRunning
            ? "ShellRunning"
            : "ShellNotRunning");
        var leagueText = _sessionState.CurrentLeague ?? UILabels.Get("ShellLeagueUnknown");
        StatusLabel.Text = string.Format(UILabels.Get("ShellStatus"), gameText, processText, leagueText);
    }

    private ICollectionView CreateNavigationView()
    {
        var entries = _pluginManager.Plugins
            .Select(plugin => new NavigationEntry(
                GetPluginGroup(plugin),
                GetGroupOrder(plugin),
                plugin.Order,
                plugin))
            .ToList();

        // Keep the POE2 section visible while its tools are temporarily unavailable.
        entries.Add(new NavigationEntry("POE2", 2, int.MaxValue, null));

        var view = new ListCollectionView(entries);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavigationEntry.GroupName)));
        view.SortDescriptions.Add(new SortDescription(nameof(NavigationEntry.GroupOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(NavigationEntry.PluginOrder), ListSortDirection.Ascending));
        return view;
    }

    private static string GetPluginGroup(IPlugin plugin)
        => plugin is PoEToolbox.Plugins.PoeCnPatch.PoeCnPatchPlugin
            ? "POE1"
            : plugin is PoEToolbox.Plugins.Poe2Font.Poe2FontPlugin
                ? "POE2"
            : "通用";

    private static int GetGroupOrder(IPlugin plugin)
        => GetPluginGroup(plugin) switch
        {
            "POE1" => 1,
            "POE2" => 2,
            _ => 0,
        };

    private sealed record NavigationEntry(
        string GroupName,
        int GroupOrder,
        int PluginOrder,
        IPlugin? Plugin)
    {
        public string Name => Plugin?.Name ?? string.Empty;
        public string IconGlyph => Plugin?.IconGlyph ?? string.Empty;
    }

    // ═══ Global log dock ═══════════════════════════════════
    private void GlobalLogToggle_Click(object sender, RoutedEventArgs e)
    {
        var show = GlobalLogPanel.Visibility != Visibility.Visible;
        GlobalLogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GlobalLogToggle.Content = show ? "▤ 收起日志" : "▤ 输出日志";
        if (show) GlobalLogBox.ScrollToEnd();
    }

    private void HideGlobalLogButton_Click(object sender, RoutedEventArgs e)
    {
        GlobalLogPanel.Visibility = Visibility.Collapsed;
        GlobalLogToggle.Content = "▤ 输出日志";
    }

    private void ClearGlobalLogButton_Click(object sender, RoutedEventArgs e)
        => GlobalLogBox.Document.Blocks.Clear();

    private void OnFileLogEntry(LogLevel level, string message, Exception? ex)
    {
        if (GlobalLogBox is null) return; // may fire before XAML is ready

        void Append()
        {
            try
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                var run = new Run($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}");
                ApplyLogLevelStyle(run, level);
                paragraph.Inlines.Add(run);
                if (ex is not null)
                {
                    var exRun = new Run("  ⟶ " + ex.Message) { FontStyle = FontStyles.Italic };
                    ApplyLogLevelStyle(exRun, level);
                    paragraph.Inlines.Add(exRun);
                }
                GlobalLogBox.Document.Blocks.Add(paragraph);
                while (GlobalLogBox.Document.Blocks.Count > 800)
                    GlobalLogBox.Document.Blocks.Remove(GlobalLogBox.Document.Blocks.FirstBlock);
                if (GlobalLogPanel.Visibility == Visibility.Visible)
                    GlobalLogBox.ScrollToEnd();
            }
            catch
            {
                // The dispatcher may be shutting down.
            }
        }

        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(Append); } catch { }
            return;
        }
        Append();
    }

    private static void ApplyLogLevelStyle(Run run, LogLevel level)
    {
        switch (level)
        {
            case LogLevel.Error:
                run.SetResourceReference(TextElement.ForegroundProperty, "ErrorBrush");
                run.FontWeight = FontWeights.Bold;
                break;
            case LogLevel.Warn:
                run.SetResourceReference(TextElement.ForegroundProperty, "WarningBrush");
                break;
            case LogLevel.Debug:
                run.SetResourceReference(TextElement.ForegroundProperty, "PlaceholderBrush");
                break;
            default:
                run.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                break;
        }
    }
}
