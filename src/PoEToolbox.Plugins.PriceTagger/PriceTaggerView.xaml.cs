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
    // 中间产物也放工具箱数据目录：exe 旁目录可能不可写（装在 Program Files 时会直接失败）
    private static readonly string WorkDir = Path.Combine(ConfigService.CacheDirectory, "work");

    private string[] _categories = PoeNinjaFetcher.Poe1ExchangeTypes;
    private readonly List<ToggleButton> _catToggles = [];
    private ComboBoxItem? _customLeagueItem;
    private readonly Dictionary<string, PoeNinjaFetcher.PoeGame?> _leagueGames =
        new(StringComparer.OrdinalIgnoreCase);
    private LeagueChoice? _selectedLeague;
    private PoeNinjaFetcher.PoeGame _activeGame = PoeNinjaFetcher.PoeGame.Poe1;
    private PoeNinjaFetcher.PoeGame? _clientGame;
    private string? _gameDataPath;
    private readonly IConfigService<PriceTaggerConfig> _config =
        new PluginConfigService<PriceTaggerConfig>("PriceTagger");
    private readonly IEventBus _eventBus;

    private bool _langSwapped;
    private bool _applying;
    private int _totalProcessed, _totalUpdated, _totalSkipped;
    private readonly Stopwatch _timer = new();
    private PriceTaggerConfig _ptConfig = null!;

    private void SavePtConfig() => _config.Save(_ptConfig);

    public PriceTaggerView(IEventBus? eventBus = null)
    {
        _eventBus = eventBus ?? new EventBus();
        _eventBus.Subscribe<GameContextChanged>(OnGameContextChanged);
        _ptConfig = _config.Load();
        InitializeComponent();
        InitCategories();
        InitLeagueCombo();
        ApplyLocalization();
        // Manual only: opening the game data file or hitting poe.ninja never
        // happens automatically — the user must click "Detect current league".
        // (Keeps plugin startup light; GGPK indexing costs a lot of memory.)
        Loaded += (_, _) =>
        {
            RestoreCachedLeague();
        };
    }

    public void Dispose() => _eventBus.Unsubscribe<GameContextChanged>(OnGameContextChanged);

    // Last used league is only used to pre-fill the box; it never triggers
    // a network request or a game data read.
    private static readonly TimeSpan LeagueCacheTtl = TimeSpan.FromHours(12);

    private sealed record LeagueChoice(PoeNinjaFetcher.PoeGame Game, string League);

    /// <summary>
    /// Fetch the league lists and select the current league.
    /// Only called from the Detect button — never automatically on load.
    /// </summary>
    private async Task DetectAndLoadLeaguesAsync()
    {
        if (_clientGame is null)
            return;

        var clientGame = _clientGame.Value;
        var leagues = await PoeNinjaFetcher.GetLeaguesAsync(clientGame);
        PopulateLeagueCombo(leagues, clientGame);

        var current = leagues.FirstOrDefault();
        if (current is not null)
        {
            var choice = new LeagueChoice(clientGame, current.Id);
            ApplyLeague(choice);
            CacheLeague(choice);
        }

        if (leagues.Count > 0)
            LeagueManualHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Pre-fill the league box with the league the user used last time.
    /// No network call, no game data access.
    /// </summary>
    private void RestoreCachedLeague()
    {
        if (!TryGetCachedLeague(out var cachedLeague) || !IsCompatibleWithClient(cachedLeague))
            return;

        // Register it so the combo does not drop the selection on focus loss
        // before the league list has been fetched.
        _leagueGames[cachedLeague.League] = cachedLeague.Game;
        ApplyLeague(cachedLeague);
    }

    // ═══ Idle release ══════════════════════════════════════════

    /// <summary>True while a league detection or apply job is still running.</summary>
    public bool IsBusy => _detectingLeague || _applying;

    /// <summary>
    /// Drop everything the detect and apply steps produced: league list, detected
    /// client version, log text, statistics. Called by the plugin 5s after the user
    /// leaves this module; the view is thrown away right afterwards, so this only
    /// has to break the references that would otherwise keep that data alive.
    /// </summary>
    public void ReleaseMemory()
    {
        _selectedLeague = null;
        _clientGame = null;
        _leagueGames.Clear();
        _cachedLangStatus = null;
        _langSwapped = false;
        _totalProcessed = _totalUpdated = _totalSkipped = 0;

        LeagueCombo.Items.Clear();
        LeagueCombo.SelectedItem = null;
        _customLeagueItem = null;
        LeagueCombo.Text = UILabels.Get("LeagueHint");
        LeagueManualHint.Visibility = Visibility.Visible;
        UpdateLeagueGameHint();
        UpdatePermanentTcAvailability();

        OutputBox.Document.Blocks.Clear();
        SetPlaceholder(true);
        StatusLabel.Text = UILabels.Get("Ready");
        StatusCategory.Text = "";
        StatusTime.Text = "";
        ProgressBar.Value = 0;
        ProgressBar.Visibility = Visibility.Collapsed;
        ProgressPct.Text = "";
        StatProcessed.Text = StatUpdated.Text = StatSkipped.Text = "0";
        StatDuration.Text = "--";
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
        IReadOnlyList<PoeNinjaFetcher.League> leagues,
        PoeNinjaFetcher.PoeGame game)
    {
        _leagueGames.Clear();
        LeagueCombo.Items.Clear();

        AddLeagueOptions(leagues, game);

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
        LblLeague.Text = UILabels.Get("League");
        LeagueManualHint.Text = UILabels.Get("LeagueManualHint");
        LblCategories.Text = UILabels.Get("Categories");
        LblOptions.Text = UILabels.Get("Options");
        LblOutput.Text = UILabels.Get("Output");
        LblCategoryHint.Text = UILabels.Get("CategoryHint");

        // Buttons
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
        var ggpkPath = GetGameDataPath();
        if (ggpkPath is null) { LogError("Please select game data in the main window first."); return; }

        var clientGame = _clientGame;
        if (clientGame != PoeNinjaFetcher.PoeGame.Poe1)
        {
            LogInfo(clientGame is null
                ? "Client version is not identified yet. Please wait for the shell to finish identifying the selected game data."
                : "Permanent TC is only available for PoE1. PoE2 already includes Traditional Chinese.");
            return;
        }

        var isZh = UILabels.Current != UILabels.Lang.English;

        // Lazy detection: only open GGPK when user clicks the button
        if (_cachedLangStatus == null)
        {
            StatusLabel.Text = isZh ? "检测语言状态..." : "Detecting language...";
            var detected = await DetectLanguageModAsync(ggpkPath);
            if (detected is null)
            {
                // 检测失败不能当"未劫持"处理，否则用户会据此做出错误的还原决定
                LogError(isZh ? "语言状态检测失败，请检查游戏数据文件。" : "Language detection failed. Check the game data file.");
                StatusLabel.Text = "Ready";
                return;
            }
            _langSwapped = detected.Value;
            _cachedLangStatus = _langSwapped ? "true" : "false";
            _ptConfig.LangSwapped = _langSwapped; SavePtConfig();
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
            // Everything that touches the client runs off the UI thread. The callback reports what
            // it changed; logging and control updates happen after the await, back on the dispatcher.
            var (flagSwapped, langSwapped) = await GameDataLoader.UseAsync(
                ggpkPath, GameDataMode.ReadWrite, (gd, _) =>
                {
                    var backup = IndexBackupService.Begin(gd);
                    var swappedFlag = false;
                    var swappedLang = false;

                    if (gd.Index.TryGetFile("Art/UIImages1.txt", out var ff))
                    {
                        var fd = ff.Read().ToArray();
                        if (EditTools.TrySwapFlagCoords(fd))
                        {
                            ff.Write(fd);
                            swappedFlag = true;
                        }
                    }

                    if (gd.Index.TryGetFile("Data/Languages.dat", out var lf))
                    {
                        var dat = new DatContainer(lf.Read().ToArray(), "Languages.dat");
                        EditTools.SwapFrenchTraditionalChinese(dat);
                        lf.Write(dat.Save(false, false));
                        swappedLang = true;
                    }

                    gd.Save();
                    IndexBackupService.Complete(gd, backup, "language-swap", new Dictionary<string, string>
                    {
                        ["restoringLanguage"] = restoring.ToString(),
                    });

                    return Task.FromResult((swappedFlag, swappedLang));
                });

            // Update state after successful swap
            _langSwapped = !restoring;
            _cachedLangStatus = _langSwapped ? "true" : "false";
            _ptConfig.LangSwapped = _cachedLangStatus == "true"; SavePtConfig();
            UiModBtn.Content = _langSwapped
                ? (isZh ? "还原语言" : "Restore Language")
                : UILabels.Get("UiModBtn");

            if (flagSwapped) LogSuccess("Flag swapped: fr ↔ zhCN");
            if (langSwapped) LogSuccess("Language swapped: French ↔ TC");
            LogSuccess(restoring
                ? (isZh ? "语言已还原！" : "Language restored!")
                : (isZh ? "永久繁体中文已应用！" : "Permanent TC applied!"));
        }
        catch (Exception ex) { LogError($"UI mod failed: {ex.Message}"); }
    }

    // ═══ Restore GGPK ════════════════════════════════════════════

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var ggpkPath = GetGameDataPath();
        if (ggpkPath is null) { LogError("Please select game data in the main window first."); return; }

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
            // RestoreBaseline rewrites the whole index — keep it off the UI thread.
            await GameDataLoader.UseAsync(ggpkPath, GameDataMode.ReadWrite, (gd, _) =>
            {
                IndexBackupService.RestoreBaseline(gd);
                // 与「特效补丁」页「彻底还原游戏客户端」同口径：基线回写后残留的 PATCHED bundle 已无引用，顺手清掉
                gd.CleanupOrphanCustomBundles(saveIndex: true);
                return Task.CompletedTask;
            });

            // 自检悬空补丁引用：基线可能过期或被污染（在补丁应用状态下创建），恢复出的索引仍可能
            // 指向已丢失的补丁文件。尽力而为，失败不改变「还原已完成」的结论。
            try
            {
                var repaired = PatchBundleRepair.RepairIfBroken(ggpkPath);
                if (repaired > 0)
                    LogSuccess(isZh
                        ? $"已修复 {repaired} 个指向丢失补丁文件的索引引用。"
                        : $"Repaired {repaired} dangling patch-file references.");
            }
            catch (Exception ex)
            {
                LogError(isZh
                    ? $"还原后自检悬空补丁引用未完成：{ex.Message}"
                    : $"Post-restore dangling-reference check failed: {ex.Message}");
            }

            LogSuccess(isZh ? "游戏数据已还原！" : "Game data restored!");
            // Clear language mod cache after restore
            _cachedLangStatus = null;
            _langSwapped = false;
            _ptConfig.LangSwapped = false; SavePtConfig();
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

    private void OnGameContextChanged(GameContextChanged context)
    {
        if (string.IsNullOrWhiteSpace(context.GameDataPath))
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnGameContextChanged(context));
            return;
        }

        if (context.Game == PoeGameKind.Unknown)
        {
            _clientGame = null;
            UpdatePermanentTcAvailability();
            UpdateLeagueGameHint();
        }
        else
        {
            var clientGame = ToPoeNinjaGame(context.Game);
            _clientGame = clientGame;
            SetActiveGame(clientGame);
            UpdatePermanentTcAvailability();
            UpdateLeagueGameHint();
            if (_selectedLeague is not null && _selectedLeague.Game != clientGame)
                ClearLeagueSelection();
        }
        _gameDataPath = context.GameDataPath;
    }

    private string? GetGameDataPath()
    {
        var path = _gameDataPath ?? GameDataPathPreference.Get();
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    /// <summary>Detect language mod status (opens game data). Called only from UiMod.
    /// null = 检测失败（数据打开/解析出错），与"未检测到劫持"区分。</summary>
    private async Task<bool?> DetectLanguageModAsync(string ggpkPath)
    {
        try
        {
            // Runs off the UI thread — parsing the client takes seconds.
            return await GameDataLoader.UseAsync(ggpkPath, GameDataMode.Read, (gd, _) =>
            {
                if (gd.Index.TryGetFile("Data/Languages.dat", out var langFr))
                {
                    var dat = new DatContainer(langFr.Read().ToArray(), "Languages.dat");
                    var frId = dat.FieldDatas[1][1].Value as string;
                    return Task.FromResult<bool?>(frId == "Traditional Chinese");
                }
                return Task.FromResult<bool?>(false);
            });
        }
        catch (Exception ex)
        {
            FileLogger.App.Warn($"Language mod detection failed: {ex.Message}");
            return null;
        }
    }

    // ═══ League ════════════════════════════════════════════════

    private bool _detectingLeague;

    private async void DetectLeague_Click(object sender, RoutedEventArgs e)
    {
        if (_detectingLeague) return;
        _detectingLeague = true;
        DetectBtn.IsEnabled = false;
        StatusLabel.Text = "Detecting league...";
        try
        {
            if (_clientGame is null)
            {
                LogWarn("Client version is not identified yet. Please wait for the shell to finish identifying the selected game data.");
                return;
            }
            await DetectAndLoadLeaguesAsync();
            LogSuccess($"League: {LeagueCombo.Text}");
            StatusLabel.Text = "Ready";
        }
        catch (Exception ex)
        {
            LogError($"Detect error: {ex.Message}");
            StatusLabel.Text = "Failed";
        }
        finally
        {
            _detectingLeague = false;
            DetectBtn.IsEnabled = true;
        }
    }

    private PoeNinjaFetcher.PoeGame? ResolveGame() => _clientGame;

    private static PoeNinjaFetcher.PoeGame ToPoeNinjaGame(PoeGameKind game)
        => game == PoeGameKind.Poe2 ? PoeNinjaFetcher.PoeGame.Poe2 : PoeNinjaFetcher.PoeGame.Poe1;

    private static PoeGameKind ToSessionGame(PoeNinjaFetcher.PoeGame game)
        => game == PoeNinjaFetcher.PoeGame.Poe2 ? PoeGameKind.Poe2 : PoeGameKind.Poe1;

    private void UpdatePermanentTcAvailability()
    {
        var isPoe2League = _selectedLeague?.Game == PoeNinjaFetcher.PoeGame.Poe2;
        var isPoe2Client = _clientGame == PoeNinjaFetcher.PoeGame.Poe2;
        var canApplyPermanentTc = _clientGame == PoeNinjaFetcher.PoeGame.Poe1 && !isPoe2League;

        UiModBtn.Visibility = Visibility.Visible;
        UiModBtn.IsEnabled = canApplyPermanentTc;
        UiModBtn.ToolTip = canApplyPermanentTc
            ? null
            : _clientGame is null
                ? "等待主窗口识别客户端版本"
                : UILabels.Get("UiModPoe2Disabled");
        RestoreBtn.SetValue(Grid.ColumnProperty, 2);
        RestoreBtn.SetValue(Grid.ColumnSpanProperty, 1);

        if (isPoe2Client)
        {
            _cachedLangStatus = null;
            _langSwapped = false;
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
        var ggpkPath = GetGameDataPath();
        if (ggpkPath is null) { LogError("Please select game data in the main window first."); return; }

        var clientGame = _clientGame;
        if (clientGame is null)
        {
            LogWarn("Client version is not identified yet. Please wait for the shell to finish identifying the selected game data.");
            return;
        }
        if (_activeGame != clientGame.Value)
        {
            LogInfo($"Client is {GameLabel(clientGame.Value)}; categories switched. Select categories and apply again.");
            return;
        }

        var league = LeagueCombo.Text.Trim();
        if (string.IsNullOrEmpty(league)) { LogWarn("League not set."); return; }

        var game = ResolveGame();
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

        _applying = true;
        ApplyBtn.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressPct.Text = "0%";
        StatusTime.Text = "";

        // 统计只在任务内累加，收尾时交回 UI 线程统一发布，避免跨线程读写共享字段
        var totalProcessed = 0;
        var totalUpdated = 0;
        var totalSkipped = 0;

        void ResetApplyUi(string statusText)
        {
            StatusLabel.Text = statusText;
            StatusCategory.Text = "";
            _applying = false;
            ApplyBtn.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
        }

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
                _timer.Stop();
                ResetApplyUi("Failed");
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

                    totalProcessed += result.Matched;
                    totalUpdated += result.Tagged;
                    totalSkipped += result.Skipped;
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
                    _totalProcessed = totalProcessed;
                    _totalUpdated = totalUpdated;
                    _totalSkipped = totalSkipped;
                    StatProcessed.Text = totalProcessed.ToString();
                    StatUpdated.Text = totalUpdated.ToString();
                    StatSkipped.Text = totalSkipped.ToString();
                    StatDuration.Text = dur;
                    ProgressBar.Value = 100;
                    ProgressPct.Text = "100%";
                    ResetApplyUi(succeeded ? (dryRun ? "Dry run complete" : "Done") : "Failed");
                    StatusTime.Text = $"Finished {DateTime.Now:HH:mm}";
                    LogInfo(new string('─', 40));
                    if (succeeded)
                        LogSuccess($"Finished — {totalUpdated} updated, {totalSkipped} skipped in {dur}");
                    // The index that was opened for this run is closed now — hand the few hundred MB
                    // back without blocking the UI thread on the collection itself.
                    MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
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
    // 警告与错误同时落盘：屏幕日志会随界面切换/重开丢失，文件日志是排障的依据
    private void LogWarn(string msg)
    {
        FileLogger.App.Warn(msg);
        AppendColored(msg, (Brush)FindResource("WarningBrush"));
    }
    private void LogError(string msg)
    {
        FileLogger.App.Error(msg);
        AppendColored(msg, (Brush)FindResource("ErrorBrush"));
    }
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
}
