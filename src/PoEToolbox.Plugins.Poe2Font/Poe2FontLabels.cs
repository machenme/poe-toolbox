namespace PoEToolbox.Plugins.Poe2Font;

/// <summary>
/// 条目 id / 作用域的人话说明。id 名是 GGG 自己起的英文，这里只做直译式标注，
/// 不额外断言游戏里某一处具体长什么样——界面同时显示原始 id，对得上就对得上。
/// </summary>
public static class Poe2FontLabels
{
    private const string GlobalScopeName = "全局";

    private static readonly Dictionary<string, string> ScopeNotes = new(System.StringComparer.Ordinal)
    {
        ["PathOfExile"] = "全局：整个客户端共用的基准字号",
        ["PathOfExile/CharacterCreation"] = "建角界面",
        ["PathOfExile/Button"] = "通用按钮",
        ["PathOfExile/HUDButton"] = "HUD 按钮",
        ["PathOfExile/Panel"] = "通用面板",
        ["PathOfExile/CharacterPanel"] = "角色面板",
        ["PathOfExile/AchievementCompleteDisplay"] = "成就完成提示",
        ["PathOfExile/ImportantMessage"] = "重要消息弹窗",
        ["PathOfExile/BattlePassWindow"] = "战令窗口",
        ["PathOfExile/BattlePassRewardReceivedModal"] = "战令领奖弹窗",
        ["PathOfExile/WorldQuestObjectiveDescription"] = "世界任务目标说明",
        ["PathOfExile/AscendancySelectorWindow"] = "进阶选择窗口",
        ["PathOfExile/AreaTitleBanner"] = "区域标题横幅",
        ["PathOfExile/EShopPanel"] = "商城面板",
        ["PathOfExile/EShopLoanerLabel"] = "商城试用装备标签",
        ["PathOfExile/ETradeMarketPanel"] = "市集面板",
    };

    private static readonly Dictionary<string, string> EntryNotes = new(System.StringComparer.Ordinal)
    {
        ["Normal"] = "正文",
        ["Small"] = "小号正文",
        ["Large"] = "大号正文",
        ["SmallNormal"] = "略小正文",
        ["LargeNormal"] = "略大正文",
        ["NormalSC"] = "正文（小型大写）",
        ["SmallSC"] = "小号（小型大写）",
        ["LargeSC"] = "大号（小型大写）",
        ["SmallNormalSC"] = "略小（小型大写）",
        ["LargeNormalSC"] = "略大（小型大写）",
        ["TinySmall"] = "极小正文",
        ["Tiny"] = "超小正文",
        ["TinyNormalSC"] = "极小（小型大写）",
        ["TinySmallSC"] = "超小（小型大写）",
        ["TinySC"] = "特小（小型大写）",
        ["ExtraLargeOP"] = "特大（ Optimus 字体）",
        ["ExtraNormalOP"] = "偏大（Optimus 字体）",
        ["ExtraSmallOP"] = "偏小（Optimus 字体）",
        ["SharpSmall"] = "清晰小字",
        ["SharpNormal"] = "清晰正文",
        ["QuikhandSmall"] = "手写体小字",
        ["QuikhandNormal"] = "手写体正文",
        ["QuikhandLarge"] = "手写体大字",
        ["QuikhandSmallNormal"] = "手写体略小",
        ["QuikhandLargeNormal"] = "手写体略大",
        ["Title"] = "标题",
        ["LabelNormal"] = "标签正文",
        ["LabelThin"] = "标签细体",
        ["LabelFont"] = "标签字体",
        ["ItemPopupTitle"] = "物品悬浮窗标题",
        ["ItemStackCountLabel"] = "堆叠数量",
        ["ItemStackCountLabelSmall"] = "堆叠数量（小）",
        ["ItemStackCountLabelQuad"] = "堆叠数量（四宫格）",
        ["ItemStackLevelLabel"] = "堆叠物品等级",
        ["ItemStackLevelLabelQuad"] = "堆叠物品等级（四宫格）",
        ["WorldItemDescriptionLabel"] = "地面掉落物名字",
        ["BossBarDisplayLabel"] = "BOSS 血条名字",
        ["UnifiedCraftingOptionName"] = "统一制作台·选项名",
        ["UnifiedCraftingOptionLevel"] = "统一制作台·等级",
        ["UnifiedCraftingOptionCost"] = "统一制作台·花费",
        ["UnifiedCraftingOptionError"] = "统一制作台·错误提示",
        ["UnifiedCraftingOptionDescription"] = "统一制作台·说明",
        ["HarvestCraftingOptionDescription"] = "Harvest 制作·说明",
        ["HarvestCraftingOptionQuantity"] = "Harvest 制作·数量",
        ["HarvestCraftingOptionCost"] = "Harvest 制作·花费",
        ["HarvestCraftingOptionLevel"] = "Harvest 制作·等级",
        ["HarvestMonsterLabel"] = "Harvest 怪物标签",
        ["HeistRoguePanelStatsHeader"] = "Heist 佣兵面板·表头",
        ["HeistRoguePanelStats"] = "Heist 佣兵面板·属性",
        ["LabyrinthSelectName"] = "迷宫·轨迹名",
        ["LabyrinthSelectNameSmall"] = "迷宫·轨迹名（小）",
        ["LabyrinthSelectLevel"] = "迷宫·层数",
        ["LabyrinthSelectCost"] = "迷宫·花费",
        ["LabyrinthEnchantingDescription"] = "迷宫附魔·说明",
        ["LabyrinthEnchantingLevel"] = "迷宫附魔·等级",
        ["LabyrinthEnchantingRemaining"] = "迷宫附魔·剩余",
        ["ProphecyTabSealButton"] = "预言页签·封印按钮",
        ["ProphecyTabNameLabel"] = "预言页签·名字",
        ["BestiaryTabCapturedMonsterName"] = "Bestiary 页签·已捕怪物",
        ["BestiaryTabCreaturePageTitle"] = "Bestiary 生物页·标题",
        ["BestiaryTabCreaturePageFlavour"] = "Bestiary 生物页·描述",
        ["BetrayalChoiceDetails"] = "Betrayal 抉择·详情",
        ["KiracMissionDescription"] = "Kirac 契约·说明",
        ["KiracMissionNPCHeader"] = "Kirac 契约·NPC 表头",
        ["UltimatumTitle"] = "Ultimatum 标题",
        ["UltimatumLabel"] = "Ultimatum 标签",
        ["UltimatumChooseASide"] = "Ultimatum 选边提示",
        ["UltimatumRewardCount"] = "Ultimatum 奖励数",
        ["UltimatumTimerLabel"] = "Ultimatum 倒计时",
        ["UltimatumPopupLabel"] = "Ultimatum 弹窗",
        ["UltimatumVoteCountLabel"] = "Ultimatum 票数",
        ["UltimatumProgressBarLabel"] = "Ultimatum 进度条",
        ["UltimatumReturnToRingLabel"] = "Ultimatum 返回提示",
        ["UltimatumFail"] = "Ultimatum 失败提示",
        ["ExpeditionWindowSectionHeader"] = "Expedition 窗口·分区表头",
        ["ExpeditionWindowSection"] = "Expedition 窗口·分区",
        ["ExpeditionMapPinNameLabel"] = "Expedition 地图标记·名字",
        ["ExpeditionMapPinSubNameLabel"] = "Expedition 地图标记·副标题",
        ["ExpeditionMapPinModLabel"] = "Expedition 地图标记·词缀",
        ["QuestDisplayTitleMKB"] = "任务提示·标题（键鼠）",
        ["QuestDisplayTitleGamepad"] = "任务提示·标题（手柄）",
        ["QuestDisplayInfoMKB"] = "任务提示·内容（键鼠）",
        ["QuestDisplayInfoGamepad"] = "任务提示·内容（手柄）",
        ["CompletionNotificationTitle"] = "完成通知·标题",
        ["CompletionNotificationDescription"] = "完成通知·描述",
        ["WorldQuestObjectiveDescription"] = "世界任务目标",
        ["ProgressBarTitle"] = "进度条标题",
        ["TrackNameTitle"] = "战令轨道名",
        ["RewardClaimedLabel"] = "已领取奖励",
        ["Reminder"] = "提醒",
        ["WarpToVaultButtonLabel"] = "跳转仓库按钮",
        ["PurchaseButtonLabel"] = "购买按钮",
        ["PriceLabel"] = "价格",
        ["Marketing"] = "推广文案",
        ["DescriptionFont"] = "描述",
        ["SearchBarFont"] = "市集·搜索框",
        ["TabButtonLabelFont"] = "市集·页签按钮",
        ["SearchPageOnlineStatusFont"] = "市集·在线状态",
        ["SearchPageFilterAndStatGeneralFont"] = "市集·筛选与属性",
        ["SearchPageFilterAndStatEmptyMessageFont"] = "市集·空列表提示",
        ["SearchBottomButtonFont"] = "市集·底部按钮",
        ["SearchPageGroupTitleFont"] = "市集·分组标题",
        ["SearchPageAddStatGroupComboBoxFont"] = "市集·添加属性下拉",
        ["SearchResultPageTitleFont"] = "市集·结果标题",
        ["SearchResultPageCountLabelFont"] = "市集·结果数量",
        ["SearchResultPageLoadingFont"] = "市集·加载中",
        ["SearchResultItemListingInfoLabelFont"] = "市集·单条信息",
        ["SearchResultItemListingPriceInfoLabelFont"] = "市集·单条价格",
        ["SearchResultItemExtendedInfoLabelFont"] = "市集·展开信息",
        ["IgnoreListPageAccountNameFont"] = "市集·黑名单账号",
        ["IgnoreListPagePaginationFont"] = "市集·黑名单翻页",
    };

    /// <summary>作用域显示名：全局收敛成一个短词，其余只留最后一段。</summary>
    public static string ScopeDisplayName(string scope)
        => scope == "PathOfExile" ? GlobalScopeName : scope[(scope.LastIndexOf('/') + 1)..];

    /// <summary>把「作用域 + id」拼成一句说明；没有标注时回退成 id 本身，不编造描述。</summary>
    public static string Describe(string scope, string id)
    {
        var entryNote = EntryNotes.TryGetValue(id, out var note) ? note : null;
        var scopeNote = ScopeNotes.TryGetValue(scope, out var scopeText) ? scopeText : null;
        return (entryNote, scopeNote) switch
        {
            (not null, not null) when scope != "PathOfExile" => $"{scopeNote.Split('：')[0]} · {entryNote}",
            (not null, _) => entryNote,
            (_, not null) when scope != "PathOfExile" => scopeNote.Split('：')[0],
            _ => id,
        };
    }
}
