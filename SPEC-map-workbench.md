# SPEC — 地图工作台（洗图助手）

> 对标外部工具「地图小助手」（`ditu/`，一份反编译的 WinForms + 付费授权程序）。
> 本 SPEC 只采用其**思路**（Ctrl+C 读物品文本 + 坐标点击），不逐行移植其代码；解析与规则层全部重写。
> 参考实现：`src/PoEToolbox.Plugins.BagCleaner/`（校准 + 热键 + 网格遍历的既有落地形态）。
> 参考架构约束：`docs/ARCHITECTURE.md`（尤其 §1 引用方向、§4 不变式 6/7/12/14）。

## 1. 评审备注

- **不移植外部代码（法务与质量双重约束）**：外部工具的解析层（`POEExtension.GetMapNumber` 用 `IndexOf("+")`→`IndexOf("%")`→`Substring` 抠数字）、坐标表（`BaseConfig` 的 `QuadCellSize * 22.5` 之类硬编码倍率）、词缀判定（`Count('{') >= 6`）均**不可照抄**。理由有两条且都要写在实现注释里：① 它是反编译产物，来源与许可不明；② 它本身是错的——负数词缀会串行匹配到下一个词缀的 `+`，解析失败静默返回 0，用户从界面看不出区别。本 SPEC 的解析层按 `{}` 分块 + 带符号数值正则重写。
- **人类保留地**：① `MapItemParser` 是纯函数、必须先把真实样本的解析测试跑绿，再动任何执行编排；② 执行路径复用既有 `InputSimulator` / `GridCalculator` / `HotkeyService` / `PoeDetector`，**不新写 Win32 输入或坐标换算**；③ 所有"要不要用这个通货/这个格子跳过"的判定必须是可单测的纯逻辑，不埋在循环里。
- **与既有插件的复用边界（§4 不变式 12）**：插件之间**不许互相引用**。地图工作台需要的输入模拟、网格换算已在 `Core`（`Input/`、`ScreenGrid/`），热键契约在 `Abstractions`（`IHotkeyService` + `IHotkeyServiceFactory`），均无需新增跨插件边。若发现需要 BagCleaner 的某个类型，正确做法是把**纯逻辑**下沉到 `Core`，而不是加一条 `Plugins.MapWorkbench → Plugins.BagCleaner` 引用——`PluginReferenceGuardTests` 会当场判红。
- **`Clipboard` 线程模型（实机踩点）**：WPF 的 `System.Windows.Clipboard` 要求 STA 且对"剪贴板被占用"会抛 `COMException`。既有 `VoyageEngine` 的做法是**空 catch 后判空**（`VoyageEngine.cs` 注释明确"剪贴板被别的进程占着是常态，记日志会刷屏"）。本 SPEC 沿用该策略，但把重试次数收敛进 `RetryTimes` 常量，不散落在调用点。
- **风险提示必须保留**：批量自动操作客户端明确违反 GGG 服务条款。视图顶部保留红字警示（外部工具原文是"工具有风险，使用需谨慎！"），措辞重写为不复制原文的等价表达。

## 2. 技术决策

| 决策 | 当前证据 | 选择 | 理由 | 风险/验证 |
|------|----------|------|------|-----------|
| 物品数据来源 | 客户端悬停 + `Ctrl+C` 会把物品完整文本写入剪贴板；`VoyageEngine` 已跑通"移动→清剪贴板→Ctrl+C→`Clipboard.GetText()`→判空"全链路 | **不引入 OCR**，沿用剪贴板文本 | 零图像识别、零内存读写，且是仓库已有且验证过的路径 | 用真实白图/蓝图/稀有图样本做解析测试 |
| 坐标表示 | `GridConfig` 已用客户区相对比例（0.0~1.0），外部工具用绝对像素 | 复用 `GridConfig` 相对比例 | 换分辨率/DPI/窗口化不用重校准，**优于**外部工具方案 | 沿用 `GridCalculator.ToAbsoluteScreenPositions`，无新代码 |
| 数值解析 | 外部工具 `GetMapNumber` 对负数与格式变化脆弱、失败静默返回 0 | `{}` 分块 + `[+-]?\d+(\.\d+)?` 正则，解析失败标 `null` 而非 0 | 失败必须可区分于"真的是 0"；负数可正确取到 | 单测覆盖负数、无 `%`、多词缀同行、缺 `+` 号 |
| 词缀计数 | 外部工具 `Count('{') >= 6` 依赖客户端格式 | 同上：按 `{}` 分块计数，块数即词缀数 | 与数值解析共用同一次分块，避免两套解析漂移 | 用 6 词缀 / 8 词缀真实样本断言 |
| 词缀匹配 | 外部工具用 `string.Contains` 子串匹配用户手填关键词，无法区分档位 | 规则层保留"关键词包含"作为**默认**，但匹配对象是**单个词缀块**而非整段文本 | 逐块匹配消除"子串跨词缀误命中"；档位筛选由数值条件承担 | 单测：关键词在 A 词缀数值里出现时不得误判 B 词缀 |
| 筛选逻辑 | 外部工具有黑名单/白名单 + 三态（全部/任一/总和），逻辑本身可用 | 保留该语义，重写实现 | 语义经其用户验证，但实现需可测 | 三态各配真值表测试 |
| 配置持久化 | `Shared.ConfigService.GetPluginConfig<T>/SavePluginConfig`（`BagCleaner` 的 `ConfigService` 是范式），原子写、损坏改名 `.corrupt.<ts>` | 同范式，插件键 `MapWorkbench` | 与全仓一致，无新机制 | round-trip + 损坏配置回落测试 |
| 热键 | `IHotkeyService` 已有触发/停止/校准三键语义，`RegisterWithoutStopKeyAsync` 专为避免 Esc 抢游戏按键 | 直接复用，默认 F4 触发 / F6 校准 / Esc 停止 | 契约现成；`StopKey` 动态注册的时序坑已有正确做法 | 复用 `BagCleanerPlugin` 的接线顺序，不自行发挥 |
| 通货坐标 | 外部工具从"仓库左上角"锚点按格子倍率推导全部通货位置 | **不采用推导**：全部通货坐标由用户在校准页逐个标记 | 倍率推导依赖特定仓库布局与 UI 缩放，客户端改版即失效；显式校准虽麻烦但可自证 | 校准页用 `CaptureRelativePoint` 逐项捕获，并回显"未标记"项 |
| 权限 | 外部工具 `app.manifest` 要求 `requireAdministrator` | **不要求提权** | `SendInput` + 常规全局热键无需管理员；主项目既有插件均不提权 | 发布后确认无 UAC 弹窗 |
| 联网 | 外部工具启动即联网做机器码授权 + 试用倒计时，离线不可用 | **纯本地，零网络** | 这是本模块存在的意义；无授权、无机器码、无倒计时 | 断网全流程可用 |

## 3. 数据模型

#### 插件配置 `config.json` → `MapWorkbench` 段

| 字段名 | 类型 | 约束 | 备注 |
|--------|------|------|------|
| `grid` | `GridConfig` | 复用 `Core.ScreenGrid.GridConfig` | 背包 12×5 格；`SkipMask` 支持"某些格永不处理" |
| `stashGrid` | `GridConfig` | 同上，默认 12×12（普通仓）或 24×24（quad） | 仓库扫描用 |
| `hotkey` | `{triggerKey, triggerModifier, stopKey, calibrateKey}` | 默认触发 `0x73`(F4) / 校准 `0x75`(F6) / 停止 `0x1B`(Esc) | 键值为 Win32 VK |
| `timing` | `{moveSettleDelayMs, copySettleDelayMs, actionSettleDelayMs}` | 默认 80 / 100 / 100；均 > 0 | 与 `BagCleaner` 命名保持一致 |
| `antiDetection` | `{clickIntervalMinMs, clickIntervalMaxMs, positionOffsetMinPx, positionOffsetMaxPx}` | 复用 `Core` 既有同名模型语义 | 同 `CleanEngine` 的随机化策略 |
| `currencyPoints` | `{ [key: string]: {relX, relY} }` | key ∈ 下方枚举；未标记则不出现 | 全部通货/工具坐标，用户逐个校准 |
| `stashType` | `enum Normal \| Quad` | 默认 `Normal` | 影响仓库网格行列数 |
| `currencyStashTab` | int 1~60 | 默认 1 | 通货所在仓库页 |
| `mapStashTab` | int 1~60 | 默认 2 | 洗好的图存入页 |
| `whiteMapStashTab` | int 1~60 | 默认 3 | 取白图来源页 |
| `mapAffixes` | `[{keyword, kind}]` | `kind ∈ Exclude \| Require` | 用户手填关键词，非硬编码词缀表 |
| `manualConditions` | `{itemQuantity?, itemRarity?, monsterPackSize?, moreMaps?, moreScarabs?, moreCurrency?}` | 各为可空 int，null = 不启用 | 数值门槛，来自 `GetMapNumber` 等价解析 |
| `matchMode` | `enum All \| Any \| Total` | 默认 `Any` | 三态匹配 |
| `matchTotalMin` | int | 仅 `Total` 模式生效 | 各启用项数值之和的下限 |
| `includeMode` | `enum AllRequire \| AnyRequire` | 默认 `AnyRequire` | `mapAffixes` 中 `Require` 项的满足方式 |
| `mapRegex` | string? | 非空时**完全接管**判定，数值条件全部短路 | 逃生舱；与 `matchMode` 互斥（UI 上互斥勾选） |
| `polish` | `{method: Alchemy \| Chaos, chiselTo20: bool, exaltedToSix: bool, vaalOrb: bool}` | 默认 Alchemy / true / false / false | 洗练流水线开关 |

**`currencyPoints` 的 key 集合**（校准页逐项列出；未标记的项在需要时提示用户先校准，而不是猜坐标）：
`scouring`、`alchemy`、`chaos`、`exalted`、`vaal`、`chisel`、`wisdom`、`transmutation`、`alteration`、`regal`、`augmentation`、`stashTopLeft`、`bagFirstCell`。

#### 解析产物 `ParsedItem`（纯内存，不持久化）

| 字段 | 类型 | 说明 |
|------|------|------|
| `Rarity` | `enum Normal \| Magic \| Rare \| Unique \| Unknown` | 由稀有度行判定 |
| `IsMap` | bool | 命中地图关键词 |
| `IsIdentified` | bool | 未命中"未鉴定" |
| `IsCorrupted` / `IsMirrored` | bool | 状态词 |
| `MapTier` | int? | 地图阶级 |
| `Quality` | int? | 品质；用于"制图钉刷到 20" |
| `AffixBlocks` | `string[]` | 按 `{}` 分块后的原始词缀文本 |
| `AffixCount` | int | `AffixBlocks.Length` |
| `Stats` | `Dictionary<string,int>` | 关键词 → 数值；解析失败**不入字典**（区别于记 0） |
| `RawText` | string | 原文，供正则模式与日志 |

**关键约定**：`Stats` 里"键不存在"= 该词缀没出现或没解析出来；"值为 0"= 真的解析到 0。外部工具把两者混为一谈正是它静默误判的根因。

## 4. 控制流

```
启动
  ConfigService.LoadConfig()  →  校验 currencyPoints 完整性
      未标记项 > 0  →  视图提示"请先在「坐标校准」补齐"，功能不启动（不猜坐标）
        ↓
校准（F6 触发，PoeDetector.IsPoeForeground() 为前置条件）
  用户把鼠标悬停到目标位置 → F6
  GridCalculator.CaptureRelativePoint(poeHwnd)  →  写入对应 currencyPoints / grid
  （可选）读剪贴板文本回显"这一格识别到的是 XXX"，让用户自证标对了
        ↓
洗图（F4 启停；执行期动态注册 Esc 为停止键）
  1) 前置校验：PoeDetector 前台 + 各通货坐标已标记 + 坐标换算非空
  2) 遍历背包 12×5（跳过 SkipMask 标记格）
       移鼠标 → 清剪贴板 → Ctrl+C → 读文本
       ├─ 空文本 → 连续空格数 ≥ ceil(sqrt(总格数)) 则跳出（提前止损）
       └─ 非空 → MapItemParser.Parse(text)
  3) 逐项分流：
       未鉴定     → 知识卷轴
       Normal     → chiselTo20 ? 制图钉刷品质至 20 : 跳过
       Magic      → 重铸石洗掉
       Rare       → RuleEngine.Matches(item) ?
                      ├─ 达标 → 强化：exaltedToSix（崇高石刷 6 词缀）
                      │                    → vaalOrb（瓦尔宝珠腐化）
                      │                    → SaveMap（Ctrl+Shift 点击存入 mapStashTab）
                      └─ 未达标 → 按 polish.method 继续洗（Alchemy 或 Chaos）
  4) 每次动通货后：IsEmpty 探空 → 用尽则中止并提示
  5) 全流程 finally：ForceReleaseAllModifiers()
        ↓
取图（可选，独立热键）
  切到 whiteMapStashTab → 逐格扫描 → 按条件筛选 → Ctrl 点击取回背包
```

**失败生命周期**：任何一步异常 → `FileLogger.Error` + 视图状态栏中文原因 + `IsStarted=false`；已发生的操作不回滚（回滚会让状态更乱），但**必须保证修饰键释放**。用户按 Esc → `ReleaseCtrl()` 先于取消处理（避免 Ctrl+Esc 触发系统菜单，这是 `BagCleanerPlugin` 已踩过的坑）。

**防呆约定**（继承外部工具被验证有效的部分，实现重写）：
- 洗练无效果判据：本轮文本 == 上轮文本 → 探空该通货 → 决定继续或中止。
- 仓库扫描提前止损：连续空格数达到阈值即跳出，不扫完整页。
- 翻仓库页用方向键计数，不用滚轮（滚轮步进不可靠）；页数差即为按键次数。

## 5. 代码结构与变更位置

| Path | Current responsibility | Change |
|------|------------------------|--------|
| `src/PoEToolbox.Plugins.MapWorkbench/`（proposed，新项目） | — | 新插件工程，命名空间 `PoEToolbox.Plugins.MapWorkbench`，`IUiPlugin` + `UserControl` |
| `.../Core/MapItemParser.cs` | （proposed） | 物品文本 → `ParsedItem`。纯函数、无 IO、无 UI，静态类，**本 SPEC 的第一交付物** |
| `.../Core/RuleEngine.cs` | （proposed） | `ParsedItem` + 配置 → 是否达标。纯函数，三态匹配 + 黑/白名单 + 正则短路 |
| `.../Core/PolishEngine.cs` | （proposed） | 洗图执行编排（状态机）；依赖 `IInputSimulator` 与配置，可注入假实现做行为测试 |
| `.../Core/IClipboardReader.cs` + `ClipboardReader.cs` | （proposed） | 剪贴板读写收口（含占用重试），便于测试替身；实现走 WPF `Clipboard` |
| `.../Models/MapWorkbenchConfig.cs` 等 | （proposed） | 配置模型 + 枚举（`MatchMode` / `AffixKind` / `StashType` / `PolishMethod`） |
| `.../Services/ConfigService.cs` | （proposed） | 仿 `BagCleaner/Services/ConfigService.cs`，键 `MapWorkbench` |
| `.../ViewModels/`、`.../Views/MapWorkbenchView.xaml` | （proposed） | MVVM（按仓库规范用 `CommunityToolkit.Mvvm`，参照 `BagCleaner`）；四个区：洗图条件 / 坐标校准 / 洗练设置 / 日志 |
| `src/PoEToolbox.App/PluginManager.cs` | 手动注册插件 | 增加 `Register(new MapWorkbenchPlugin())` |
| `tests/PoEToolbox.Tests/MapItemParserTests.cs` | （proposed） | 解析真值表（见 §6） |
| `tests/PoEToolbox.Tests/RuleEngineTests.cs` | （proposed） | 三态匹配、黑白名单、正则短路 |
| `tests/PoEToolbox.Tests/Fixtures/map-items/*.txt` | （proposed） | 真实 `Ctrl+C` 样本（白图/蓝图/6词缀/8词缀/已腐化/未鉴定各若干） |
| `src/PoEToolbox.Core/`（按需） | Win32 输入、网格换算 | **仅在**需要新的纯输入/几何能力时下沉新增；不复用 BagCleaner 私有类型（§4 不变式 12） |
| `tests/PoEToolbox.Tests/PoEToolbox.Tests.csproj` | 引用 4 个插件工程 | 增加 `MapWorkbench` 的 `ProjectReference` |

调用方向：`View → ViewModel → RuleEngine/PolishEngine（纯逻辑）→ Core.Input / Core.ScreenGrid → Win32`。插件**不引用** `App` / `Ui` / 其它插件。

## 6. 验证、发布和回滚

- **单元测试（第一交付物，必须先绿）**：
  - `MapItemParserTests`：① 稀有度四态识别；② 词缀块数与真实样本一致（6 / 8 词缀）；③ 数值提取覆盖 `+35%`、`-10%`（**负数是外部工具会挂的用例**）、无 `%`、同行多词缀、`+` 号缺失；④ 解析失败入 `Stats` 为"键不存在"而非 0；⑤ 未鉴定 / 已腐化 / 已复制 状态位；⑥ 品质与地图阶级。
  - `RuleEngineTests`：① `All`/`Any`/`Total` 三态真值表；② `Exclude` 命中即否决（优先于一切）；③ `Require` 的 `AllRequire`/`AnyRequire` 两态；④ 关键词只在 A 词缀命中时不得误判 B 词缀（逐块匹配的回归）；⑤ `mapRegex` 非空时数值条件全短路。
  - `ConfigServiceTests`：配置 round-trip；损坏配置回落默认并记日志（沿用 `Shared.ConfigService` 既有行为，不新增机制）。
- **测试前置约束（§4 不变式 9）**：测试程序集整体关并行；新测试类若触碰进程级静态（配置路径注入缝、`FileLogger.EntryLogged`），必须挂既有 `Collection` 或新增并在 `AssemblyInfo.cs` 登记。
- **人工验收（真机，测试替代不了）**：
  1. 校准 13 个坐标点 → 逐项回显识别结果正确；
  2. 单张稀有图全流程：洗 → 判 → 强化 → 存仓，日志可复盘每一步；
  3. 通货用尽 → 中止且提示准确；
  4. Esc 中止 → 修饰键确认已释放（按一下 Ctrl 观察游戏无粘滞）；
  5. 切分辨率 / 窗口化 → 免重校准仍准确（验证相对坐标方案）；
  6. 断网全流程可用（**本模块的核心承诺**）。
- **回滚边界**：本模块**不改游戏数据**、不产生补丁、不写 `Bundles2`，因此没有游戏侧回滚概念——把插件从 `PluginManager.RegisterAll()` 注释掉即完全停用；用户配置留在 `config.json` 的 `MapWorkbench` 段，删除该段即复位。
- **发布**：`CHANGELOG.md`「未发布 → 新功能」加一条；`README.md` 功能表新增一行（中文名「地图工作台」，支持范围 PoE1，说明含"自动化操作，违反服务条款风险自负"）；`version.json` 按现有流程升版。
- **架构校验**：`PluginReferenceGuardTests` 必须保持绿（证明没有新增跨插件引用）；XAML 新增资源需过 `XamlTokenGuardTests`。

## 7. 假设与风险

| 假设/风险 | 影响 | 验证方式 |
|-----------|------|----------|
| 客户端 `Ctrl+C` 文本格式与样本一致（词缀确实被 `{}` 包裹） | 决定解析层是否成立 | **最先做**：真机复制若干张图存为 Fixtures，跑通解析测试；格式不符则解析层重设计（但剪贴板路线不变） |
| 「词缀数 = `{}` 块数」在 6/8 词缀图上成立 | 影响强化目标判定 | 用真实 6 词缀 / 8 词缀样本断言；不成立则改为按词缀行语义计数 |
| 通货坐标必须逐个校准（放弃了外部工具的倍率推导） | 首次使用门槛更高 | 校准页把 13 项做成清单 + 识别回显；若体验过差，再评估"标记 1 个锚点推导其余"作为可选快捷路径 |
| 中文 / 繁中 / 英文三语言关键词表 | 沿用外部工具的三语言并列关键词（`POEConst` 的做法），但**自建词表**并只覆盖本模块需要的项 | 按客户端语言选择词表；词表集中在一个静态类，便于后续补语言 |
| 全局热键与游戏按键冲突 | 用户无法触发 | 键位可配置；沿用 `RegisterWithoutStopKeyAsync` 避免 Esc 常驻抢键 |
| 自动化违反 GGG 服务条款 | 用户账号风险 | 视图常驻红字警示 + README 标注；这是产品定位问题，不是技术问题，不因"技术上不读内存"而淡化 |
| 反检测随机化不等于安全 | 可能被行为检测识别 | 只做"贴合既有 `CleanEngine` 策略"的随机化，**不承诺规避检测**；文档措辞不得暗示可以规避 |
| 外部工具仍在维护 / 其免费替代已存在 | 本模块价值下降 | 本模块的差异化在"零网络、无授权、与工具箱其余能力同源"；上线前复核是否与既有第三方工具重复 |

---

```
State: DESIGN → VERIFY（解析层与规则层已交付并跑绿）
Mode: Standard SPEC
Evidence: ditu/ 反编译源码（Map.cs / MapConfig.cs / Map.Designer.cs）、POEUtility.dll 反编译
          （BaseConfig / POEExtension / POESimulator / POEHook / POEConst / HttpUtitlity / REGUtility）、
          src/PoEToolbox.Plugins.BagCleaner/**（校准·热键·引擎范式）、
          src/PoEToolbox.Core/{Input,ScreenGrid}/**、src/PoEToolbox.Shared/PoeDetector.cs、
          src/PoEToolbox.Abstractions/IHotkeyService.cs、src/PoEToolbox.App/PluginManager.cs、
          docs/ARCHITECTURE.md §1/§4、tests/PoEToolbox.Tests/PoEToolbox.Tests.csproj
Passed: 范围、受影响 seam、技术决策（剔除联网授权与提权）、控制流、失败生命周期、验证计划、回滚边界
Delivered: §5 的 MapItemParser + ParsedItem + MapStat + MapRuleSet + RuleEngine +
          MapItemKeywords + 测试与样本；Debug/Release 全量 312 通过 0 失败（原 238 + 新增 74）；
          App 组合根编译通过；PluginReferenceGuardTests 保持绿
Pending: ① 真机取 Ctrl+C 样本替换 Fixtures（阻塞 PolishEngine 编码）；
         ② 「词缀数 = 花括号块数」与「词缀块优先于属性行」两条假设待样本证实（SPEC §7）；
         ③ 13 项逐个校准的交互体验待用户确认；
         ④ 插件尚未实现 IUiPlugin/视图，因此**故意未**注册进 PluginManager.RegisterAll()
            ——没有视图的导航项是半成品。PolishEngine + 视图完成后再注册
Handoff: MapItemParser + RuleEngine 已完成并单测覆盖；下一步等真机样本，再开 PolishEngine
```
