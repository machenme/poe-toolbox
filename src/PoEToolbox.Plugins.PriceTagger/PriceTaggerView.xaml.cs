using System.Windows.Controls;
using PoEToolbox.Shared;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using LibDat2;
using PoEToolbox.Sdk;
using PoEToolbox.Core.Pipeline;

namespace PoEToolbox.Plugins.PriceTagger;

public partial class PriceTaggerView : UserControl
{
    private static readonly string PoeNinjaDir = Path.Combine(
        ConfigService.CacheDirectory,
        "poe_ninja");
    private const string WorkDir = "work";

    private string[] _categories = PoeNinjaFetcher.Poe1ExchangeTypes;
    private readonly List<ToggleButton> _catToggles = [];
    private ComboBoxItem? _customLeagueItem;
    private readonly Dictionary<string, PoeNinjaFetcher.PoeGame?> _leagueGames =
        new(StringComparer.OrdinalIgnoreCase);
    private LeagueChoice? _selectedLeague;
    private PoeNinjaFetcher.PoeGame _activeGame = PoeNinjaFetcher.PoeGame.Poe1;
    private PoeNinjaFetcher.PoeGame? _clientGame;
    private readonly IConfigService<PriceTaggerConfig> _config =
        new PluginConfigService<PriceTaggerConfig>("PriceTagger");
    private readonly IEventBus _eventBus;

    private bool _langSwapped;
    private int _totalProcessed, _totalUpdated, _totalSkipped;
    private readonly Stopwatch _timer = new();
    private PriceTaggerConfig _ptConfig = null!;

    private void SavePtConfig() => _config.Save(_ptConfig);

    public PriceTaggerView(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        _ptConfig = _config.Load();
        InitializeComponent();
        RestoreCachedGamePreference();
        InitCategories();
        InitLeagueCombo();
        ApplyLocalization();
        Loaded += async (_, _) =>
        {
            RestoreCachedGgpkPath();
            await DetectCurrentClientGameAsync();
            await LoadLeaguesAsync();
        };
    }

    private static readonly TimeSpan LeagueCacheTtl = TimeSpan.FromMinutes(15);

    private sealed record LeagueChoice(PoeNinjaFetcher.PoeGame Game, string League);

    private async Task LoadLeaguesAsync(bool selectCurrentLeague = false)
    {
        var poe1Task = PoeNinjaFetcher.GetLeaguesAsync(PoeNinjaFetcher.PoeGame.Poe1);
        var poe2Task = PoeNinjaFetcher.GetLeaguesAsync(PoeNinjaFetcher.PoeGame.Poe2);
        await Task.WhenAll(poe1Task, poe2Task);

        var poe1Leagues = await poe1Task;
        var poe2Leagues = await poe2Task;
        PopulateLeagueCombo(poe1Leagues, poe2Leagues);

        var preferredGame = _clientGame ?? _activeGame;

        if (selectCurrentLeague)
        {
            var current = (preferredGame == PoeNinjaFetcher.PoeGame.Poe1 ? poe1Leagues : poe2Leagues)
                .FirstOrDefault();
            if (current is not null)
            {
                var choice = new LeagueChoice(preferredGame, current.Id);
                ApplyLeague(choice);
                CacheLeague(choice);
            }
            return;
        }

        if (TryGetCachedLeague(out var cachedLeague) && IsCompatibleWithClient(cachedLeague))
        {
            SetActiveGame(cachedLeague.Game);
            ApplyLeague(cachedLeague);
            return;
        }

        var defaultLeague = (preferredGame == PoeNinjaFetcher.PoeGame.Poe1
            ? poe1Leagues
            : poe2Leagues).FirstOrDefault();
        if (defaultLeague is not null)
        {
            var choice = new LeagueChoice(preferredGame, defaultLeague.Id);
            ApplyLeague(choice);
            CacheLeague(choice);
        }
    }

    private void RestoreCachedGamePreference()
    {
        if (Enum.TryParse<PoeNinjaFetcher.PoeGame>(_ptConfig.LeagueCacheGame, out var game))
            _activeGame = game;
    }

    private bool IsCompatibleWithClient(LeagueChoice choice) =>
        _clientGame is null || _clientGame == choice.Game;

    private bool TryGetCachedLeague(out LeagueChoice choice)
    {
        choice = null!;
        var cached = _ptConfig.LeagueCache;
        var checkedStr = _ptConfig.LeagueCacheTime;
        if (string.IsNullOrWhiteSpace(cached) || string.IsNullOrWhiteSpace(checkedStr)
            || !DateTime.TryParse(checkedStr, out var checkedAt)
            || DateTime.UtcNow - checkedAt >= LeagueCacheTtl
            || !Enum.TryParse<PoeNinjaFetcher.PoeGame>(_ptConfig.LeagueCacheGame, out var game))
            return false;

        choice = new LeagueChoice(game, cached);
        return true;
    }

    private void CacheLeague(LeagueChoice choice)
    {
        _ptConfig.LeagueCache = choice.League;
        _ptConfig.LeagueCacheGame = choice.Game.ToString();
        _ptConfig.LeagueCacheTime = DateTime.UtcNow.ToString("O");
        SavePtConfig();
    }

    private void PopulateLeagueCombo(
        IReadOnlyList<PoeNinjaFetcher.League> poe1Leagues,
        IReadOnlyList<PoeNinjaFetcher.League> poe2Leagues)
    {
        _leagueGames.Clear();
        LeagueCombo.Items.Clear();

        AddLeagueOptions(poe1Leagues, PoeNinjaFetcher.PoeGame.Poe1);
        AddLeagueOptions(poe2Leagues, PoeNinjaFetcher.PoeGame.Poe2);

        _customLeagueItem = new ComboBoxItem
        {
            Content = UILabels.Get("CustomLeague"),
            Tag = "__custom_league__",
        };
        LeagueCombo.Items.Add(_customLeagueItem);
    }

    private void AddLeagueOptions(
        IReadOnlyList<PoeNinjaFetcher.League> leagues, PoeNinjaFetcher.PoeGame game)
    {
        foreach (var league in leagues)
        {
            if (_leagueGames.TryGetValue(league.Id, out var existing) && existing != game)
                _leagueGames[league.Id] = null;
            else
                _leagueGames[league.Id] = game;

            LeagueCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{league.Name} ({GameLabel(game)})",
                Tag = new LeagueChoice(game, league.Id),
            });
        }
    }

    private static string GameLabel(PoeNinjaFetcher.PoeGame game) =>
        game == PoeNinjaFetcher.PoeGame.Poe1 ? "PoE1" : "PoE2";

    private void ApplyLeague(LeagueChoice choice)
    {
        _selectedLeague = choice;
        LeagueCombo.Text = choice.League;
        UpdatePermanentTcAvailability();
        UpdateLeagueGameHint();
        _eventBus.Publish(new LeagueChanged(ToSessionGame(choice.Game), choice.League));
    }

    private void ClearLeagueSelection()
    {
        _selectedLeague = null;
        LeagueCombo.SelectedItem = null;
        LeagueCombo.Text = UILabels.Get("LeagueHint");
        UpdatePermanentTcAvailability();
        UpdateLeagueGameHint();
    }

    private void SetActiveGame(PoeNinjaFetcher.PoeGame game)
    {
        if (_activeGame == game)
            return;

        _activeGame = game;
        InitCategories();
        UpdateLeagueGameHint();
    }

    /// <summary>Update all UI text to match current language.</summary>
    private void ApplyLocalization()
    {
        // Header

        // Section headers
        LblGGPK.Text = UILabels.Get("GGPK");
        GgpkPathHint.Text = UILabels.Get("GameDataHint");
        LblLeague.Text = UILabels.Get("League");
        LblCategories.Text = UILabels.Get("Categories");
        LblOptions.Text = UILabels.Get("Options");
        LblOutput.Text = UILabels.Get("Output");
        LblCategoryHint.Text = UILabels.Get("CategoryHint");

        // Buttons
        BrowseBtn.Content = UILabels.Get("Browse");
        DetectBtn.Content = UILabels.Get("Detect");
        AllBtn.Content = UILabels.Get("All");
        NoneBtn.Content = UILabels.Get("None");
        SaveLogBtn.Content = UILabels.Get("SaveLog");
        ClearOutputBtn.Content = UILabels.Get("Clear");
        ApplyBtn.Content = UILabels.Get("ApplyPriceTags");
        UiModBtn.Content = UILabels.Get("UiModBtn");
        RestoreBtn.Content = UILabels.Get("RestoreBtn");

        // Checkboxes
        AutoScrollBox.Content = UILabels.Get("AutoScroll");
        DryRunBox.Content = UILabels.Get("DryRun");
        ForceUpdateBox.Content = UILabels.Get("ForceUpdate");
        VerboseBox.Content = UILabels.Get("Verbose");

        // Other
        EstimateText.Text = UILabels.Get("Estimate");
        OutputPlaceholder.Text = UILabels.Get("Placeholder");

        // Statistics labels
        LblStatProcessed.Text = UILabels.Get("Processed");
        LblStatUpdated.Text = UILabels.Get("Updated");
        LblStatSkipped.Text = UILabels.Get("Skipped");
        LblStatDuration.Text = UILabels.Get("Duration");

        if (_customLeagueItem is not null)
            _customLeagueItem.Content = UILabels.Get("CustomLeague");

        // Window title
        // (handled by App shell)

        // League hint
        if (string.IsNullOrWhiteSpace(LeagueCombo.Text)
            || LeagueCombo.Text.Contains("检测")
            || LeagueCombo.Text.Contains("偵測")
            || LeagueCombo.Text.Contains("Detect"))
            LeagueCombo.Text = UILabels.Get("LeagueHint");

        // Category chips
        foreach (var tb in _catToggles)
        {
            var enName = tb.Tag as string ?? "";
            tb.Content = _activeGame == PoeNinjaFetcher.PoeGame.Poe2
                ? UILabels.CatPoe2(enName)
                : UILabels.Cat(enName);
        }

        UpdatePermanentTcAvailability();
        UpdateLeagueGameHint();
    }

    // ═══ UI Mod: Language swap ═══════════════════════════════════

    private string? _cachedLangStatus; // null=unchecked, "true"/"false"

    private async void UiMod_Click(object sender, RoutedEventArgs e)
    {
        var ggpkPath = GgpkPathBox.Text.Trim();
        if (!File.Exists(ggpkPath)) { LogError("Game data file not found."); return; }

        var clientGame = await DetectClientGameAsync(ggpkPath);
        if (clientGame != PoeNinjaFetcher.PoeGame.Poe1)
        {
            LogInfo("Permanent TC is only available for PoE1. PoE2 already includes Traditional Chinese.");
            return;
        }

        var isZh = UILabels.Current != UILabels.Lang.English;

        // Lazy detection: only open GGPK when user clicks the button
        if (_cachedLangStatus == null)
        {
            StatusLabel.Text = isZh ? "检测语言状态..." : "Detecting language...";
            _langSwapped = DetectLanguageMod(ggpkPath);
            _cachedLangStatus = _langSwapped ? "true" : "false";
            StatusLabel.Text = "Ready";
        }

        bool restoring = _langSwapped;

        string title, msg;
        if (restoring)
        {
            title = isZh ? "检测到繁中 — 还原语言" : "TC detected — Restore";
            msg = isZh
                ? "已检测到繁体中文劫持。\n\n是否还原到默认语言 (French)？"
                : "Traditional Chinese mod detected.\n\nRestore to default (French)?";
        }
        else
        {
            title = isZh ? "应用永久繁体中文" : "Apply Permanent TC";
            msg = isZh
                ? "未检测到繁中劫持。\n\n将把法语替换为永久繁体中文。\n游戏数据文件会新增自定义 Bundle，不修改原始文件。\n\n确定？"
                : "No TC mod detected.\n\nReplace French with Permanent TC?\nNew custom bundle, original files untouched.\n\nContinue?";
        }

        if (MessageBox.Show(msg, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        try
        {
            using var gd = GameDataAccess.Open(ggpkPath);
            var backup = IndexBackupService.Begin(gd);

            if (gd.Index.TryGetFile("Art/UIImages1.txt", out var ff))
            {
                var fd = ff.Read().ToArray();
                var txt = System.Text.Encoding.Unicode.GetString(fd);
                var fr = txt.IndexOf("Common/FlagIcons/fr\"");
                var cn = txt.IndexOf("Common/FlagIcons/zhCN\"");
                var fc = txt.IndexOf("1.dds\" ", fr) + 7;
                var cc = txt.IndexOf("1.dds\" ", cn) + 7;
                if (fr > 0 && cn > fr && fc > 7 && cc > 7)
                {
                    var fb = fc * 2; var cb = cc * 2;
                    var tmp = fd[fb..(fb + 26)].ToArray();
                    Array.Copy(fd, cb, fd, fb, 26);
                    Array.Copy(tmp, 0, fd, cb, 26);
                    ff.Write(fd);
                    LogSuccess("Flag swapped: fr ↔ zhCN");
                }
            }

            if (gd.Index.TryGetFile("Data/Languages.dat", out var lf))
            {
                var dat = new DatContainer(lf.Read().ToArray(), "Languages.dat");
                int frn = -1, tch = -1;
                for (var i = 0; i < dat.FieldDatas.Count; ++i)
                {
                    var name = (string)dat.FieldDatas[i][1].Value;
                    if (name == "French") frn = i;
                    else if (name == "Traditional Chinese") tch = i;
                }
                (dat.FieldDatas[tch][1], dat.FieldDatas[frn][1]) = (dat.FieldDatas[frn][1], dat.FieldDatas[tch][1]);
                (dat.FieldDatas[tch][2], dat.FieldDatas[frn][2]) = (dat.FieldDatas[frn][2], dat.FieldDatas[tch][2]);
                lf.Write(dat.Save(false, false));
                LogSuccess("Language swapped: French ↔ TC");
            }

            gd.Save();
            IndexBackupService.Complete(gd, backup, "language-swap", new Dictionary<string, string>
            {
                ["restoringLanguage"] = restoring.ToString(),
            });

            // Update state after successful swap
            _langSwapped = !restoring;
            _cachedLangStatus = _langSwapped ? "true" : "false";
            _ptConfig.LangSwapped = _cachedLangStatus == "true"; SavePtConfig();
            UiModBtn.Content = _langSwapped
                ? (isZh ? "还原语言" : "Restore Language")
                : UILabels.Get("UiModBtn");

            LogSuccess(restoring
                ? (isZh ? "语言已还原！" : "Language restored!")
                : (isZh ? "永久繁体中文已应用！" : "Permanent TC applied!"));
        }
        catch (Exception ex) { LogError($"UI mod failed: {ex.Message}"); }
    }

    // ═══ Restore GGPK ════════════════════════════════════════════

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var ggpkPath = GgpkPathBox.Text.Trim();
        if (!File.Exists(ggpkPath)) { LogError("Game data file not found."); return; }

        var isZh = UILabels.Current != UILabels.Lang.English;
        var result = MessageBox.Show(
            isZh
                ? "将恢复到此客户端首次修改前的原始索引。\n\n这会撤销所有工具修改（语言、国旗、物价标签等）。\n\n确定？"
                : "Will restore the original index saved before this client was first modified.\n\nThis reverts ALL toolbox modifications (language, flag, price tags, etc.).\n\nContinue?",
            isZh ? "还原游戏数据" : "Restore game data",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        try
        {
            using var gd = GameDataAccess.Open(ggpkPath);
            IndexBackupService.RestoreBaseline(gd);

            LogSuccess(isZh ? "游戏数据已还原！" : "Game data restored!");
            // Clear language mod cache after restore
            _cachedLangStatus = null;
            _langSwapped = false;
            _ptConfig.LangSwapped = false; SavePtConfig();
            GgpkModStatus.Visibility = Visibility.Collapsed;
            UiModBtn.Content = UILabels.Get("UiModBtn");
        }
        catch (Exception ex) { LogError($"Restore failed: {ex.Message}"); }
    }

    // ═══ Settings ⚙ ══════════════════════════════════════════════


    // ═══ Init ════════════════════════════════════════════════════

    private void InitCategories()
    {
        _categories = _activeGame == PoeNinjaFetcher.PoeGame.Poe1
            ? PoeNinjaFetcher.Poe1ExchangeTypes
            : PoeNinjaFetcher.Poe2ExchangeTypes;
        _catToggles.Clear();
        CategoryItems.Items.Clear();

        foreach (var cat in _categories)
        {
            var tb = new ToggleButton
            {
                Content = _activeGame == PoeNinjaFetcher.PoeGame.Poe2
                    ? UILabels.CatPoe2(cat)
                    : UILabels.Cat(cat),
                Style = (Style)FindResource("CategoryChip"),
                Tag = cat,
            };
            _catToggles.Add(tb);
            CategoryItems.Items.Add(tb);
        }
    }

    private void InitLeagueCombo()
    {
        _customLeagueItem = new ComboBoxItem
        {
            Content = UILabels.Get("CustomLeague"),
            Tag = "__custom_league__",
        };
        LeagueCombo.Items.Add(_customLeagueItem);
        LeagueCombo.Text = UILabels.Get("LeagueHint");
    }

    private void LeagueCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeagueCombo.SelectedItem == _customLeagueItem)
        {
            _selectedLeague = null;
            LeagueCombo.SelectedItem = null;
            LeagueCombo.Text = string.Empty;
            LeagueCombo.IsDropDownOpen = false;
            Dispatcher.BeginInvoke(() => LeagueCombo.Focus());
            return;
        }

        if (LeagueCombo.SelectedItem is ComboBoxItem { Tag: LeagueChoice choice })
        {
            LeagueCombo.IsDropDownOpen = false;
            Dispatcher.BeginInvoke(() =>
            {
                LeagueCombo.SelectedItem = null;
                ApplyLeague(choice);
            });
        }
    }

    private void LeagueCombo_LostKeyboardFocus(
        object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        var league = LeagueCombo.Text.Trim();
        if (_leagueGames.TryGetValue(league, out var game) && game is not null)
            ApplyLeague(new LeagueChoice(game.Value, league));
        else
        {
            _selectedLeague = null;
            UpdatePermanentTcAvailability();
        }
    }

    // ═══ GGPK ══════════════════════════════════════════════════

    /// <summary>Restore GGPK path from cache only — no file open.</summary>
    private void RestoreCachedGgpkPath()
    {
        if (!string.IsNullOrWhiteSpace(GgpkPathBox.Text) && File.Exists(GgpkPathBox.Text))
            return;

        // Restore from cache
        var cachedPath = _ptConfig.GgpkPath;
        if (cachedPath is not null && File.Exists(cachedPath))
        {
            GgpkPathBox.Text = cachedPath;
            GgpkStatus.Text = $"✓ {cachedPath}";
            GgpkStatus.Visibility = Visibility.Visible;
            GgpkAutoHint.Visibility = Visibility.Collapsed;
            if (_ptConfig.LangSwapped)
            {
                _langSwapped = true;
                var isZh = UILabels.Current != UILabels.Lang.English;
                UiModBtn.Content = isZh ? "还原语言" : "Restore Language";
                GgpkModStatus.Visibility = Visibility.Visible;
            }
            return;
        }

        // No cache — auto-detect once on first run
        AutoDetectGgpk();
    }

    private void AutoDetectGgpk_Click(object s, System.Windows.Input.MouseButtonEventArgs e) => AutoDetectGgpk();

    private void AutoDetectGgpk()
    {
        if (!string.IsNullOrWhiteSpace(GgpkPathBox.Text) && File.Exists(GgpkPathBox.Text))
            return;

        var path = PoeDetector.Default.DetectGameDataPath();
        if (path is not null) { SetGgpkPath(path); return; }
    }

    private void BrowseGgpk_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Game data files|*.ggpk;*.index.bin|Content.ggpk|*.ggpk|Index files|*.index.bin|All files|*.*",
            Title = "Select Content.ggpk or _.index.bin"
        };
        if (dlg.ShowDialog() == true)
        {
            SetGgpkPath(dlg.FileName);
            LogSuccess($"Selected: {Path.GetFileName(dlg.FileName)}");
        }
    }

    private void SetGgpkPath(string path)
    {
        GgpkPathBox.Text = path;
        GgpkStatus.Text = $"✓ {path}";
        GgpkStatus.Visibility = Visibility.Visible;
        GgpkAutoHint.Visibility = Visibility.Collapsed;
        _ptConfig.GgpkPath = path; SavePtConfig();
        PublishGameContext(PoeGameKind.Unknown, path);
        _ = DetectClientGameAsync(path);
    }

    private async Task DetectCurrentClientGameAsync()
    {
        var ggpkPath = GgpkPathBox.Text.Trim();
        if (File.Exists(ggpkPath))
            await DetectClientGameAsync(ggpkPath);
    }

    /// <summary>Detect language mod status (opens GGPK). Called only from UiMod.</summary>
    private bool DetectLanguageMod(string ggpkPath)
    {
        try
        {
            using var gd = GameDataAccess.Open(ggpkPath);
            if (gd.Index.TryGetFile("Data/Languages.dat", out var langFr))
            {
                var dat = new DatContainer(langFr.Read().ToArray(), "Languages.dat");
                var frId = dat.FieldDatas[1][1].Value as string;
                _langSwapped = frId == "Traditional Chinese";
                _ptConfig.LangSwapped = _langSwapped; SavePtConfig();
                return _langSwapped;
            }
        }
        catch { }
        return false;
    }

    // ═══ League ════════════════════════════════════════════════

    private async void DetectLeague_Click(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Detecting league...";
        try
        {
            await DetectCurrentClientGameAsync();
            await LoadLeaguesAsync(selectCurrentLeague: true);
            LogSuccess($"League: {LeagueCombo.Text}");
            StatusLabel.Text = "Ready";
        }
        catch (Exception ex) { LogError($"Detect error: {ex.Message}"); }
    }

    private async Task<PoeNinjaFetcher.PoeGame?> ResolveGameAsync(string league)
    {
        if (_selectedLeague?.League.Equals(league, StringComparison.OrdinalIgnoreCase) == true)
            return _selectedLeague.Game;

        if (_leagueGames.TryGetValue(league, out var game) && game is not null)
            return game.Value;

        StatusLabel.Text = "Identifying game...";
        var detectedGame = await PoeNinjaFetcher.DetectGameForLeagueAsync(league);
        if (detectedGame is not null)
            _leagueGames[league] = detectedGame;
        return detectedGame;
    }

    private async Task<PoeNinjaFetcher.PoeGame?> DetectClientGameAsync(string gameDataPath)
    {
        try
        {
            var game = await Task.Run(() =>
            {
                using var gameData = GameDataAccess.Open(gameDataPath, readOnly: true);
                return gameData.IsPoe2Client
                    ? PoeNinjaFetcher.PoeGame.Poe2
                    : PoeNinjaFetcher.PoeGame.Poe1;
            });

            if (!string.Equals(GgpkPathBox.Text.Trim(), gameDataPath, StringComparison.OrdinalIgnoreCase))
                return null;

            _clientGame = game;
            PublishGameContext(ToSessionGame(game), gameDataPath);
            UpdatePermanentTcAvailability();
            UpdateLeagueGameHint();
            if (_activeGame != game)
            {
                SetActiveGame(game);
                LogInfo($"Client data detected: {GameLabel(game)}");
            }
            if (_selectedLeague is not null && _selectedLeague.Game != game)
            {
                LogWarn($"Selected league is {GameLabel(_selectedLeague.Game)}, but client data is {GameLabel(game)}. League selection was reset.");
                ClearLeagueSelection();
            }

            return game;
        }
        catch (Exception ex)
        {
            if (string.Equals(GgpkPathBox.Text.Trim(), gameDataPath, StringComparison.OrdinalIgnoreCase))
                LogError($"Unable to read client data: {ex.Message}");
            return null;
        }
    }

    private void PublishGameContext(PoeGameKind game, string path)
    {
        _eventBus.Publish(new GameContextChanged(
            game,
            path,
            PoeDetector.Default.IsPoeRunning()));
    }

    private static PoeGameKind ToSessionGame(PoeNinjaFetcher.PoeGame game)
        => game == PoeNinjaFetcher.PoeGame.Poe2 ? PoeGameKind.Poe2 : PoeGameKind.Poe1;

    private void UpdatePermanentTcAvailability()
    {
        var isPoe2League = _selectedLeague?.Game == PoeNinjaFetcher.PoeGame.Poe2;
        var isPoe2Client = _clientGame == PoeNinjaFetcher.PoeGame.Poe2;
        var canApplyPermanentTc = !isPoe2League && !isPoe2Client;

        UiModBtn.Visibility = Visibility.Visible;
        UiModBtn.IsEnabled = canApplyPermanentTc;
        UiModBtn.ToolTip = canApplyPermanentTc ? null : UILabels.Get("UiModPoe2Disabled");
        RestoreBtn.SetValue(Grid.ColumnProperty, 2);
        RestoreBtn.SetValue(Grid.ColumnSpanProperty, 1);

        if (isPoe2Client)
        {
            _cachedLangStatus = null;
            _langSwapped = false;
            GgpkModStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateLeagueGameHint()
    {
        var game = _clientGame ?? _selectedLeague?.Game;
        LeagueGameHint.Text = game is null
            ? UILabels.Get("LeagueGameUnknown")
            : string.Format(UILabels.Get("LeagueGameFormat"), GameLabel(game.Value));
    }

    private static string GetPriceDataDir(PoeNinjaFetcher.PoeGame game, string league) =>
        Path.Combine(PoeNinjaDir, game.ToString(), Uri.EscapeDataString(league));

    // ═══ Apply ═════════════════════════════════════════════════

    private async void ApplyPrices_Click(object sender, RoutedEventArgs e)
    {
        var ggpkPath = GgpkPathBox.Text.Trim();
        if (!File.Exists(ggpkPath)) { LogError("Game data file not found."); return; }

        var gameBeforeClientDetection = _activeGame;
        var clientGame = await DetectClientGameAsync(ggpkPath);
        if (clientGame is null) return;
        if (gameBeforeClientDetection != clientGame.Value)
        {
            LogInfo($"Client is {GameLabel(clientGame.Value)}; categories switched. Select categories and apply again.");
            return;
        }

        var league = LeagueCombo.Text.Trim();
        if (string.IsNullOrEmpty(league)) { LogWarn("League not set."); return; }

        var game = await ResolveGameAsync(league);
        if (game is null)
        {
            LogWarn("Unable to identify PoE1 or PoE2 for this league.");
            StatusLabel.Text = "Ready";
            return;
        }

        if (clientGame != game.Value)
        {
            LogWarn($"Selected league is {GameLabel(game.Value)}, but the client data is {GameLabel(clientGame.Value)}.");
            return;
        }

        var leagueChoice = new LeagueChoice(game.Value, league);
        _selectedLeague = leagueChoice;
        CacheLeague(leagueChoice);
        var priceDataDir = GetPriceDataDir(game.Value, league);

        var selected = _catToggles
            .Where(t => t.IsChecked == true)
            .Select(t => (string)t.Tag)
            .ToList();

        if (selected.Count == 0) { LogWarn("No categories selected."); return; }

        var dryRun = DryRunBox.IsChecked == true;
        var forceUpdate = ForceUpdateBox.IsChecked == true;
        var isZh = UILabels.Current != UILabels.Lang.English;
        SetPlaceholder(false);

        // Reset stats
        _totalProcessed = _totalUpdated = _totalSkipped = 0;
        StatProcessed.Text = StatUpdated.Text = StatSkipped.Text = "0";
        StatDuration.Text = "--";
        _timer.Restart();

        ApplyBtn.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressPct.Text = "0%";
        StatusTime.Text = "";

        LogInfo(new string('─', 40));

        // Step 1: Fetch missing data
        var toFetch = new List<string>();
        foreach (var cat in selected)
        {
            var path = Path.Combine(priceDataDir, $"{cat}.json");
            if (forceUpdate || !File.Exists(path))
                toFetch.Add(cat);
        }

        if (toFetch.Count > 0)
        {
            StatusLabel.Text = "Fetching...";
            LogInfo(isZh
                ? $"需要获取 {toFetch.Count} 个分类的物价数据..."
                : $"Fetching {toFetch.Count} categories...");

            var fetchProgress = new Progress<string>(msg => LogLine(msg));
            var fetchResult = await Task.Run(() => PoeNinjaFetcher.FetchSelectedAsync(
                priceDataDir, league, [.. toFetch], fetchProgress, game.Value));

            if (fetchResult.Status != PoeNinjaFetcher.FetchStatus.Succeeded)
            {
                var failed = fetchResult.Items
                    .Where(item => item.Status == PoeNinjaFetcher.FetchStatus.Failed)
                    .Select(item => item.UsedExistingCache
                        ? $"{item.Category} (请求失败，存在旧缓存)"
                        : $"{item.Category} (请求失败，无可用缓存)")
                    .ToArray();
                LogError(isZh
                    ? $"物价获取未完成，已停止标价：{string.Join("、", failed)}"
                    : $"Price fetch did not complete; tagging was stopped: {string.Join(", ", failed)}");
                StatusLabel.Text = "Failed";
                StatusCategory.Text = "";
                ApplyBtn.IsEnabled = true;
                ProgressBar.Visibility = Visibility.Collapsed;
                _timer.Stop();
                return;
            }

            LogSuccess(isZh ? "物价获取完成。" : "Fetch done.");
        }
        else
        {
            LogInfo(isZh ? "所有分类已有本地数据，跳过获取。" : "All categories cached, skipping fetch.");
        }

        // Step 2: Apply price tags
        LogInfo(isZh
            ? $"开始标价：{selected.Count} 个分类, game={GameLabel(game.Value)}, league={league}, dryRun={dryRun}"
            : $"Starting: {selected.Count} categories, game={GameLabel(game.Value)}, league={league}, dryRun={dryRun}");
        LogInfo(new string('─', 40));

        var progress = new Progress<string>(msg => LogLine(msg));
        await Task.Run(() =>
        {
            var succeeded = false;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    StatusLabel.Text = "Processing...";
                    StatusCategory.Text = $"{selected.Count} categories";
                });

                var results = CategoryPriceTagger.RunMany(ggpkPath, selected,
                    poeNinjaDir: priceDataDir, workDir: WorkDir,
                    dryRun: dryRun, progress: progress);

                foreach (var category in selected)
                {
                    if (!results.TryGetValue(category, out var result))
                        continue;

                    _totalProcessed += result.Matched;
                    _totalUpdated += result.Tagged;
                    _totalSkipped += result.Skipped;
                    LogSuccess($"  {category}: complete.");
                }
                succeeded = true;
            }
            catch (Exception ex)
            {
                LogError($"Batch failed: {ex.Message}");
                if (ex.InnerException is not null)
                    LogError($"  -> {ex.InnerException.Message}");
            }
            finally
            {
                _timer.Stop();
                var dur = _timer.Elapsed.ToString(@"hh\:mm\:ss");

                Dispatcher.Invoke(() =>
                {
                    StatProcessed.Text = _totalProcessed.ToString();
                    StatUpdated.Text = _totalUpdated.ToString();
                    StatSkipped.Text = _totalSkipped.ToString();
                    StatDuration.Text = dur;
                    ProgressBar.Value = 100;
                    ProgressPct.Text = "100%";
                    StatusLabel.Text = succeeded ? (dryRun ? "Dry run complete" : "Done") : "Failed";
                    StatusCategory.Text = "";
                    StatusTime.Text = $"Finished {DateTime.Now:HH:mm}";
                    ApplyBtn.IsEnabled = true;
                    LogInfo(new string('─', 40));
                    if (succeeded)
                        LogSuccess($"Finished — {_totalUpdated} updated, {_totalSkipped} skipped in {dur}");
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    // Trim working set (release OS file cache pages from process memory)
                    try { using var p = System.Diagnostics.Process.GetCurrentProcess(); SetProcessWorkingSetSize(p.Handle, -1, -1); } catch { }
                });
            }
        });
    }

    // ═══ Select All / None ═════════════════════════════════════

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _catToggles) t.IsChecked = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _catToggles) t.IsChecked = false;
    }

    // ═══ Clear Output ═════════════════════════════════════════

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        OutputBox.Document.Blocks.Clear();
        OutputPlaceholder.Visibility = Visibility.Visible;
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Text files|*.txt|All files|*.*",
            DefaultExt = ".txt",
            FileName = $"pricetag_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dlg.ShowDialog() == true)
        {
            var range = new TextRange(OutputBox.Document.ContentStart, OutputBox.Document.ContentEnd);
            File.WriteAllText(dlg.FileName, range.Text);
            LogSuccess($"Log saved: {dlg.FileName}");
        }
    }

    private void SetPlaceholder(bool show) =>
        OutputPlaceholder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    // ═══ Colored Output ════════════════════════════════════════

    private void LogLine(string msg)
    {
        Brush color;
        if (msg.Contains(" [ ") && (msg.Contains("c ]") || msg.Contains("d ]") || msg.Contains("e ]")))
            color = (Brush)FindResource("AccentBrush");
        else if (msg.Contains("Error") || msg.Contains("Failed") || msg.Contains("error"))
            color = (Brush)FindResource("ErrorBrush");
        else if (msg.Contains("Done") || msg.Contains("complete") || msg.Contains("success")
              || msg.Contains("MATCHES") || msg.Contains("✓"))
            color = (Brush)FindResource("SuccessBrush");
        else if (msg.Contains("Warning") || msg.Contains("warn"))
            color = (Brush)FindResource("WarningBrush");
        else if (msg.Contains("──"))
            color = (Brush)FindResource("BorderBrush");
        else
            color = (Brush)FindResource("TextSecondaryBrush");

        AppendColored(msg, color);
    }

    private void LogInfo(string msg) => AppendColored(msg, (Brush)FindResource("TextSecondaryBrush"));
    private void LogSuccess(string msg) => AppendColored(msg, (Brush)FindResource("SuccessBrush"));
    private void LogWarn(string msg) => AppendColored(msg, (Brush)FindResource("WarningBrush"));
    private void LogError(string msg) => AppendColored(msg, (Brush)FindResource("ErrorBrush"));
    private void LogAccent(string msg) => AppendColored(msg, (Brush)FindResource("AccentBrush"));

    private void AppendColored(string text, Brush color)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendColored(text, color));
            return;
        }
        OutputPlaceholder.Visibility = Visibility.Collapsed;
        var p = new Paragraph { Margin = new Thickness(0) };
        p.Inlines.Add(new Run(text) { Foreground = color });
        OutputBox.Document.Blocks.Add(p);
        OutputBox.ScrollToEnd();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);
}
