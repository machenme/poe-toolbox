namespace PoEToolbox.Shared;

/// <summary>
/// Multi-language UI strings. Supports EN/SC/TC.
/// Plugins register their own localization keys via AddKeys().
/// </summary>
public static class UILabels
{
    public enum Lang { English, SimplifiedChinese, TraditionalChinese }

    public static Lang Current { get; private set; } = Lang.SimplifiedChinese;

    // ═══ App shell keys ═════════════════════════════════
    private static readonly Dictionary<string, string> EN = new()
    {
        ["AppTitle"] = "PoE Toolbox",
        ["Subtitle"] = "Stream of Exile utility tools",
        ["Version"] = "v0.1.2",
        ["Theme"] = "Theme",
        ["FollowSystem"] = "Follow System",
        ["Light"] = "Light",
        ["Dark"] = "Dark",
        ["Language"] = "Language",
        ["ReleaseGgpkLocks"] = "Release game data locks",
        ["GgpkLocksReleased"] = "Game data locks released",
        ["English"] = "English",
        ["SimplifiedChinese"] = "简体中文",
        ["TraditionalChinese"] = "繁體中文",

        // Shell
        ["PluginPriceTagger"] = "Price Tagger (Global)",
        ["PluginBagCleaner"] = "Bag Cleaner",
        ["PluginVoyager"] = "Voyager",
        ["Settings"] = "Settings",
        ["ShellStatus"] = "Game: {0} · Running: {1} · League: {2}",
        ["ShellGameUnknown"] = "Not selected",
        ["ShellRunning"] = "Running",
        ["ShellNotRunning"] = "Not running",
        ["ShellLeagueUnknown"] = "Not selected",
        ["UiModPoe2Disabled"] = "PoE2 already includes Traditional Chinese.",
        ["VersionTooltip"] = "Current version",
        ["UpdateAvailableTooltip"] = "Update available",
        ["UpdateDialogTitle"] = "Update available",
        ["UpdateNotesUnavailable"] = "No release notes available.",
        ["OpenReleasePage"] = "View releases",
        ["SkipVersion"] = "Skip v{0}",
        ["OpenAppDataFolder"] = "Open data folder",
    };

    private static readonly Dictionary<string, string> TC = new()
    {
        ["AppTitle"] = "流放工具箱",
        ["Subtitle"] = "流亡黯道實用工具集合",
        ["Version"] = "v0.1.2",
        ["Theme"] = "主題",
        ["FollowSystem"] = "跟隨系統",
        ["Light"] = "淺色",
        ["Dark"] = "深色",
        ["Language"] = "語言",
        ["ReleaseGgpkLocks"] = "釋放遊戲資料佔用",
        ["GgpkLocksReleased"] = "已釋放遊戲資料佔用",
        ["SimplifiedChinese"] = "简体中文",
        ["TraditionalChinese"] = "繁體中文",

        ["PluginPriceTagger"] = "物價標註(國際服)",
        ["PluginBagCleaner"] = "背包清理",
        ["PluginVoyager"] = "航海助手",
        ["Settings"] = "設定",
        ["ShellStatus"] = "遊戲：{0} · 運行狀態：{1} · 聯盟：{2}",
        ["ShellGameUnknown"] = "未選擇",
        ["ShellRunning"] = "運行中",
        ["ShellNotRunning"] = "未運行",
        ["ShellLeagueUnknown"] = "未選擇",
        ["UiModPoe2Disabled"] = "PoE2 已內建繁體中文。",
        ["VersionTooltip"] = "目前版本",
        ["UpdateAvailableTooltip"] = "有可用更新",
        ["UpdateDialogTitle"] = "有可用更新",
        ["UpdateNotesUnavailable"] = "暫無更新說明。",
        ["OpenReleasePage"] = "查看發佈頁",
        ["SkipVersion"] = "跳過 v{0}",
        ["OpenAppDataFolder"] = "開啟資料夾",
    };

    private static readonly Dictionary<string, string> SC = new()
    {
        ["AppTitle"] = "流放工具箱",
        ["Subtitle"] = "流放之路实用工具集合",
        ["Version"] = "v0.1.2",
        ["Theme"] = "主题",
        ["FollowSystem"] = "跟随系统",
        ["Light"] = "浅色",
        ["Dark"] = "深色",
        ["Language"] = "语言",
        ["ReleaseGgpkLocks"] = "释放游戏数据占用",
        ["GgpkLocksReleased"] = "已释放游戏数据占用",
        ["SimplifiedChinese"] = "简体中文",
        ["TraditionalChinese"] = "繁體中文",

        ["PluginPriceTagger"] = "物价标注(国际服)",
        ["PluginBagCleaner"] = "背包清理",
        ["PluginVoyager"] = "航海助手",
        ["Settings"] = "设置",
        ["ShellStatus"] = "游戏：{0} · 运行状态：{1} · 联盟：{2}",
        ["ShellGameUnknown"] = "未选择",
        ["ShellRunning"] = "运行中",
        ["ShellNotRunning"] = "未运行",
        ["ShellLeagueUnknown"] = "未选择",
        ["UiModPoe2Disabled"] = "PoE2 已内置繁体中文。",
        ["VersionTooltip"] = "当前版本",
        ["UpdateAvailableTooltip"] = "有可用更新",
        ["UpdateDialogTitle"] = "有可用更新",
        ["UpdateNotesUnavailable"] = "暂无更新说明。",
        ["OpenReleasePage"] = "查看发布页",
        ["SkipVersion"] = "跳过 v{0}",
        ["OpenAppDataFolder"] = "打开数据文件夹",
    };

    // ═══ Plugin: Price Tagger keys ═══════════════════════
    private static readonly Dictionary<string, string> PT_EN = new()
    {
        ["WinTitle"] = "PoE Price Tagger",
        ["WinSubtitle"] = "Automatically update stash price tags",
        ["GGPK"] = "Game Data",
        ["GameDataHint"] = "Content.ggpk / _.index.bin",
        ["League"] = "League",
        ["Categories"] = "Categories",
        ["Options"] = "Options",
        ["Output"] = "Output",
        ["Browse"] = "Browse",
        ["Detect"] = "Detect League",
        ["ForceUpdate"] = "Download latest price data",
        ["All"] = "Select All",
        ["None"] = "Deselect All",
        ["ApplyPriceTags"] = "Apply Price Tags",
        ["BackupGGPK"] = "Backup game data",
        ["DryRun"] = "Dry Run (preview only)",
        ["Verbose"] = "Verbose",
        ["AutoScroll"] = "Auto-scroll",
        ["SaveLog"] = "Save Log",
        ["Clear"] = "Clear",
        ["Processed"] = "Processed",
        ["Updated"] = "Updated",
        ["Skipped"] = "Skipped",
        ["Duration"] = "Duration",
        ["Ready"] = "Ready",
        ["UiModBtn"] = "Traditional Chinese Patch",
        ["RestoreBtn"] = "Restore game data",
        ["CategoryHint"] = "(check categories to price tag)",
        ["LeagueHint"] = "Detect current league",
        ["CustomLeague"] = "Custom league...",
        ["LeagueGameUnknown"] = "Game not detected yet",
        ["LeagueGameFormat"] = "Current game: {0}",
        ["LeagueManualHint"] = "Not detected — click Detect to load the league list",
        ["Estimate"] = "Est. ~30s per category",
        ["Placeholder"] = "No output yet.\nSelect categories and click Apply Price Tags.",
    };

    private static readonly Dictionary<string, string> PT_TC = new()
    {
        ["WinTitle"] = "PoE 物價標註工具",
        ["WinSubtitle"] = "自動更新倉庫價格標籤",
        ["GGPK"] = "遊戲資料檔案",
        ["GameDataHint"] = "Content.ggpk / _.index.bin",
        ["League"] = "聯盟",
        ["Categories"] = "分類",
        ["Options"] = "選項",
        ["Output"] = "輸出",
        ["Browse"] = "瀏覽",
        ["Detect"] = "偵測目前聯盟",
        ["ForceUpdate"] = "重新下載物價資料",
        ["All"] = "全選",
        ["None"] = "取消全選",
        ["ApplyPriceTags"] = "套用價格標籤",
        ["BackupGGPK"] = "備份遊戲資料",
        ["DryRun"] = "預覽模式",
        ["Verbose"] = "詳細日誌",
        ["AutoScroll"] = "自動滾動",
        ["SaveLog"] = "儲存日誌",
        ["Clear"] = "清除",
        ["Processed"] = "已處理",
        ["Updated"] = "已更新",
        ["Skipped"] = "已跳過",
        ["Duration"] = "耗時",
        ["Ready"] = "就緒",
        ["UiModBtn"] = "繁體中文補丁",
        ["RestoreBtn"] = "還原遊戲資料",
        ["CategoryHint"] = "(勾選想要標價的通貨類別)",
        ["LeagueHint"] = "偵測目前聯盟",
        ["CustomLeague"] = "自訂聯盟...",
        ["LeagueGameUnknown"] = "尚未識別遊戲版本",
        ["LeagueGameFormat"] = "目前遊戲：{0}",
        ["LeagueManualHint"] = "尚未偵測 — 點擊「偵測目前聯盟」載入聯盟清單",
        ["Estimate"] = "預估 每個分類 ~30 秒",
        ["Placeholder"] = "尚無輸出。\n勾選分類後點擊套用價格標籤。",
    };

    private static readonly Dictionary<string, string> PT_SC = new()
    {
        ["WinTitle"] = "PoE 物价标注工具",
        ["WinSubtitle"] = "自动更新仓库价格标签",
        ["GGPK"] = "游戏数据文件",
        ["GameDataHint"] = "Content.ggpk / _.index.bin",
        ["League"] = "联盟",
        ["Categories"] = "分类",
        ["Options"] = "选项",
        ["Output"] = "输出",
        ["Browse"] = "浏览",
        ["Detect"] = "检测当前联盟",
        ["ForceUpdate"] = "重新下载物价数据",
        ["All"] = "全选",
        ["None"] = "取消全选",
        ["ApplyPriceTags"] = "应用价格标签",
        ["BackupGGPK"] = "备份游戏数据",
        ["DryRun"] = "预览模式",
        ["Verbose"] = "详细日志",
        ["AutoScroll"] = "自动滚动",
        ["SaveLog"] = "保存日志",
        ["Clear"] = "清除",
        ["Processed"] = "已处理",
        ["Updated"] = "已更新",
        ["Skipped"] = "已跳过",
        ["Duration"] = "耗时",
        ["Ready"] = "就绪",
        ["UiModBtn"] = "繁体中文补丁",
        ["RestoreBtn"] = "还原游戏数据",
        ["CategoryHint"] = "(勾选想要标价的通货类别)",
        ["LeagueHint"] = "检测当前联盟",
        ["CustomLeague"] = "自定义联盟...",
        ["LeagueGameUnknown"] = "尚未识别游戏版本",
        ["LeagueGameFormat"] = "当前游戏：{0}",
        ["LeagueManualHint"] = "尚未检测 — 点击「检测当前联盟」载入联盟列表",
        ["Estimate"] = "预估 每个分类 ~30 秒",
        ["Placeholder"] = "暂无输出。\n勾选分类后点击应用价格标签。",
    };

    // ═══ Plugin: Bag Cleaner keys ══════════════════════
    private static readonly Dictionary<string, string> BC_EN = new()
    {
        ["BC_HotkeySettings"] = "Hotkey Settings",
        ["BC_TriggerKey"] = "Trigger Hotkey",
        ["BC_StopKey"] = "Stop Hotkey",
        ["BC_CalibrateKey"] = "Calibrate Hotkey",
        ["BC_GridSettings"] = "Grid Settings",
        ["BC_SkipCells"] = "Skip Cells",
        ["BC_Columns"] = "Columns",
        ["BC_Rows"] = "Rows",
        ["BC_TimingDelay"] = "Move Settle Delay (ms)",
        ["BC_PoeProcesses"] = "POE Process Names",
        ["BC_AddProcess"] = "Add",
        ["BC_CalibrateBtn"] = "Start Calibration",
        ["BC_SaveSettings"] = "Save",
        ["BC_CancelBtn"] = "Cancel",
        ["BC_RestoreDefaults"] = "Restore Defaults",
        ["BC_StatusIdle"] = "Ready – press hotkey to start",
        ["BC_StatusExecuting"] = "Cleaning bag...",
        ["BC_StatusStopped"] = "Stopped by user",
        ["BC_CalibrateStep1"] = "Move mouse to top-left cell and press calibrate key",
        ["BC_CalibrateStep2"] = "Move mouse to bottom-right cell and press calibrate key",
        ["BC_AntiDetection"] = "Anti-Detection",
        ["BC_IntervalMin"] = "Click Interval Min (ms)",
        ["BC_IntervalMax"] = "Click Interval Max (ms)",
        ["BC_OffsetMin"] = "Position Offset Min (px)",
        ["BC_OffsetMax"] = "Position Offset Max (px)",
    };

    private static readonly Dictionary<string, string> BC_TC = new()
    {
        ["BC_HotkeySettings"] = "熱鍵設定",
        ["BC_TriggerKey"] = "觸發清包熱鍵",
        ["BC_StopKey"] = "緊急停止熱鍵",
        ["BC_CalibrateKey"] = "校準熱鍵",
        ["BC_GridSettings"] = "背包格設定",
        ["BC_SkipCells"] = "跳過格子",
        ["BC_Columns"] = "列數",
        ["BC_Rows"] = "行數",
        ["BC_TimingDelay"] = "移動停頓延遲 (ms)",
        ["BC_PoeProcesses"] = "POE 程序名稱",
        ["BC_AddProcess"] = "添加",
        ["BC_CalibrateBtn"] = "開始校準",
        ["BC_SaveSettings"] = "儲存",
        ["BC_CancelBtn"] = "取消",
        ["BC_RestoreDefaults"] = "恢復預設",
        ["BC_StatusIdle"] = "就緒 – 按熱鍵開始清包",
        ["BC_StatusExecuting"] = "清包執行中...",
        ["BC_StatusStopped"] = "已停止",
        ["BC_CalibrateStep1"] = "將滑鼠移到背包第一格中心，按下校準鍵",
        ["BC_CalibrateStep2"] = "將滑鼠移到背包最後一格中心，按下校準鍵",
        ["BC_AntiDetection"] = "反檢測設定",
        ["BC_IntervalMin"] = "點擊間隔下限 (ms)",
        ["BC_IntervalMax"] = "點擊間隔上限 (ms)",
        ["BC_OffsetMin"] = "位置偏移下限 (px)",
        ["BC_OffsetMax"] = "位置偏移上限 (px)",
    };

    private static readonly Dictionary<string, string> BC_SC = new()
    {
        ["BC_HotkeySettings"] = "热键设置",
        ["BC_TriggerKey"] = "触发清包热键",
        ["BC_StopKey"] = "紧急停止热键",
        ["BC_CalibrateKey"] = "校准热键",
        ["BC_GridSettings"] = "背包格设置",
        ["BC_SkipCells"] = "跳过格子",
        ["BC_Columns"] = "列数",
        ["BC_Rows"] = "行数",
        ["BC_TimingDelay"] = "移动停顿延迟 (ms)",
        ["BC_PoeProcesses"] = "POE 进程名称",
        ["BC_AddProcess"] = "添加",
        ["BC_CalibrateBtn"] = "开始校准",
        ["BC_SaveSettings"] = "保存",
        ["BC_CancelBtn"] = "取消",
        ["BC_RestoreDefaults"] = "恢复默认",
        ["BC_StatusIdle"] = "就绪 – 按热键开始清包",
        ["BC_StatusExecuting"] = "清包执行中...",
        ["BC_StatusStopped"] = "已停止",
        ["BC_CalibrateStep1"] = "将鼠标移到背包第一格中心，按下校准键",
        ["BC_CalibrateStep2"] = "将鼠标移到背包最后一格中心，按下校准键",
        ["BC_AntiDetection"] = "反检测设置",
        ["BC_IntervalMin"] = "点击间隔下限 (ms)",
        ["BC_IntervalMax"] = "点击间隔上限 (ms)",
        ["BC_OffsetMin"] = "位置偏移下限 (px)",
        ["BC_OffsetMax"] = "位置偏移上限 (px)",
    };

    // ═══ Category name translations (Price Tagger) ═════
    private static readonly Dictionary<string, string> CatEN = new()
    {
        ["Currency"] = "Currency", ["Fragment"] = "Fragment",
        ["Runegraft"] = "Runegraft", ["AllflameEmber"] = "AllflameEmber",
        ["Tattoo"] = "Tattoo", ["Omen"] = "Omen",
        ["Ducat"] = "Ducat", ["EnshroudingCrystal"] = "EnshroudingCrystal",
        ["DivinationCard"] = "DivinationCard", ["Artifact"] = "Artifact",
        ["Oil"] = "Oil", ["DeliriumOrb"] = "DeliriumOrb",
        ["Scarab"] = "Scarab", ["Astrolabe"] = "Astrolabe",
        ["Fossil"] = "Fossil", ["Resonator"] = "Resonator",
        ["Essence"] = "Essence",
    };

    private static readonly Dictionary<string, string> CatZH = new()
    {
        ["Currency"] = "通貨", ["Fragment"] = "碎片",
        ["Runegraft"] = "符文之結", ["AllflameEmber"] = "不滅之火",
        ["Tattoo"] = "紋身", ["Omen"] = "預兆",
        ["Ducat"] = "達克特", ["EnshroudingCrystal"] = "壟罩晶石",
        ["DivinationCard"] = "命運卡", ["Artifact"] = "探險文物",
        ["Oil"] = "油瓶", ["DeliriumOrb"] = "譫妄玉",
        ["Scarab"] = "聖甲蟲", ["Astrolabe"] = "星盤",
        ["Fossil"] = "化石", ["Resonator"] = "鑄新儀",
        ["Essence"] = "精髓",
    };

    // PoE2 exchange API types use different category names from PoE1.
    // Keep these keys aligned with PoeNinjaFetcher.Poe2ExchangeTypes.
    private static readonly Dictionary<string, string> Poe2CatEN = new()
    {
        ["Currency"] = "Currency", ["Fragments"] = "Fragments",
        ["Abyss"] = "Abyssal Bones", ["UncutGems"] = "Uncut Gems",
        ["LineageSupportGems"] = "Lineage Gems", ["Essences"] = "Essences",
        ["SoulCores"] = "Soul Cores", ["Idols"] = "Idols",
        ["Runes"] = "Runes", ["Ritual"] = "Omens",
        ["Expedition"] = "Expedition", ["Delirium"] = "Liquid Emotions",
        ["Breach"] = "Catalysts", ["Verisium"] = "Verisium",
    };

    private static readonly Dictionary<string, string> Poe2CatZH = new()
    {
        ["Currency"] = "通货", ["Fragments"] = "门票碎片",
        ["Abyss"] = "深渊", ["UncutGems"] = "未切割的宝石",
        ["LineageSupportGems"] = "宝石", ["Essences"] = "精髓",
        ["SoulCores"] = "灵魂核心", ["Idols"] = "魔偶",
        ["Runes"] = "符文", ["Ritual"] = "祭祀",
        ["Expedition"] = "探险通货", ["Delirium"] = "谵妄",
        ["Breach"] = "催化剂", ["Verisium"] = "死境探险",
    };

    // ═══ Lookup ════════════════════════════════════════

    /// <summary>Get a localized string from the global key set.</summary>
    public static string Get(string key)
    {
        var dict = Current switch
        {
            Lang.SimplifiedChinese => SC,
            Lang.TraditionalChinese => TC,
            _ => EN,
        };
        if (dict.TryGetValue(key, out var v)) return v;

        // Fallback: try Price Tagger keys
        var ptDict = Current switch
        {
            Lang.SimplifiedChinese => PT_SC,
            Lang.TraditionalChinese => PT_TC,
            _ => PT_EN,
        };
        if (ptDict.TryGetValue(key, out v)) return v;

        // Fallback: try Bag Cleaner keys
        var bcDict = Current switch
        {
            Lang.SimplifiedChinese => BC_SC,
            Lang.TraditionalChinese => BC_TC,
            _ => BC_EN,
        };
        if (bcDict.TryGetValue(key, out v)) return v;

        return key; // raw key as final fallback
    }

    /// <summary>Translate a category name to current language.</summary>
    public static string Cat(string enName)
    {
        var dict = Current == Lang.English ? CatEN : CatZH;
        return dict.GetValueOrDefault(enName, enName);
    }

    /// <summary>Translate a PoE2 exchange category to the current language.</summary>
    public static string CatPoe2(string apiType)
    {
        var dict = Current == Lang.English ? Poe2CatEN : Poe2CatZH;
        return dict.GetValueOrDefault(apiType, apiType);
    }

    /// <summary>Switch language. Returns true if changed.</summary>
    public static bool SetLanguage(Lang lang)
    {
        if (Current == lang) return false;
        Current = lang;
        return true;
    }
}
