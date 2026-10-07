# PoE Toolbox UI 系统重构（Token 化设计系统）PRD

> **PM 备注**：这个想法的功能面是完整的，但闭环缺一个关键环节——重构类需求最大的失控风险是「没有验收口径」。现有工程的惯例是发布前人工目视（见 CHANGELOG D6：主题自动化验收已取消，只剩本人开一次程序目视），因此本次重构必须自带一个**组件总览页（Theme Gallery）**作为目视验收面，否则「彻底重构」无法被任何人在任何时点判定为完成。另一个缺口：`ThemeBtn` 当前 `Visibility="Collapsed"`（源码注释"hidden for now"），主题能力已存在但用户摸不到——本次要把「切换可用」纳入范围闭环。补救方向已写入范围。当前方案补齐验收面和切换入口后逻辑闭环完整，可直接进入设计阶段。

## 1. 项目概述

> **PoE Toolbox UI 系统重构**：把散装的主题文件与逐页手写的控件样式，收敛为一套 shadcn 思路的 WPF 原生设计系统——单一 token 真相源 + 统一组件样式库 + 真正可用的主题切换。

**核心目标（要解决什么问题）**：

1. **主题词汇双轨并存**：`Themes/dark.xaml` / `light.xaml` 里同一颜色存在两套键名（如 `WindowBgBrush` 与 BagCleaner 兼容别名 `BgBase`），每份主题文件 250+ 行里 palette、组件样式、别名混杂，改一处视觉要跨文件找全。
2. **组件样式没有落点**：现有主题文件只覆盖 Button/TextBox/ComboBox/CheckBox/ScrollBar 等少数控件；各插件视图（最大 464 行）在页面里手写背景、边距、圆角，同一个"卡片"在不同页面长相不一致。
3. **硬编码颜色残留**：MainWindow.xaml、BagCleanerView.xaml 等存在直接写死的 hex 色值，切换主题时这些位置不变色。
4. **主题切换不可用**：ThemeManager 支持 FollowSystem/Light/Dark 三态，但界面上没有入口（按钮被隐藏），用户只能被动跟随系统。
5. **视觉语言过时**：整体是"深色贴灰块"风格，缺乏成体系的字阶、间距、圆角、状态色规范。

**明确不做（当前阶段排除）**：

- ❌ 更换技术栈（不换 Avalonia/WinUI，不引入第三方 WPF UI 库，全部代码自持——已拍板）
- ❌ 跨平台（保持 `net10.0-windows`）
- ❌ 任何功能/业务逻辑变更：补丁、字体表、数据浏览等模块的行为一律不动
- ❌ 交互流程重设计：布局只做适配性调整，不重排信息架构，不改导航模型
- ❌ 自定义标题栏 / 窗口 Fluent 磨砂特效
- ❌ 用户自定义主题包（导入/导出主题 JSON）
- ❌ 中英双语界面完善（`LangBtn` 维持现状隐藏）
- ❌ 主题切换的自动化视觉回归测试（沿用 D6 决议：人工目视验收）

## 2. 目标用户

- **唯一用户/维护者：项目本人（osmc）**。单人本地工具，无外部用户访谈渠道，不编造 persona。
- **已知场景与约束**：日常在 dark 下开发使用；每次发布前开一次程序逐页目视（既有验收习惯）；对「游戏数据路径、字体表、补丁账本」等功能路径极熟，UI 重构不能让任何一条操作路径变难。
- **核心痛点**：想改一个视觉属性（如圆角）要跨 21 个 XAML 文件逐个找；想临时切 light 对比颜色时无入口。
- **未知项与验证方式**：无外部用户，"是否好用"只能由本人在 Theme Gallery 页与逐页目视中验证，验证方式即既有发布前目视流程。

## 3. 功能矩阵与业务说明

### 功能总览

| 优先级 | 功能名称 | 描述 |
|--------|----------|------|
| P0 | 设计令牌层（Token Layer） | 颜色/字号/字重/间距/圆角/动效时长全部收敛为语义化资源键，单一真相源；主题文件只保留两套 token 值，不再含组件样式与别名 |
| P0 | 统一组件样式库 | Button（primary/secondary/ghost/danger）、输入框、ComboBox、CheckBox、Toggle/Chip、卡片、滚动条、TabControl、DataGrid/ListView、ProgressBar、ToolTip、菜单等一套自持样式，键名与变体口径全工程统一 |
| P0 | 主窗口壳迁移 | MainWindow、DisclaimerPage、OutputPanel 改为只消费 token 与组件库，清除全部硬编码色值 |
| P0 | 9 个插件视图迁移 | PriceTagger、DataBrowser（含 MapNumberView 及两窗口）、BagCleaner（含 CalibrationWindow）、Voyager（含 CalibrationWindow）、PoeCnPatch、Poe2Font、FxPatch（含 Creator）、TermTranslator、AffixWorkbench（含 ColorEditorWindow）全部页面翻新，行为不变 |
| P0 | 主题切换入口恢复 | 顶栏 ThemeBtn 取消隐藏，三态（跟随系统/亮/暗）即时生效且记忆到配置，已打开的插件页无需重开 |
| P0 | 兼容别名收编 | BagCleaner 别名字段（BgBase/BgCard/…）全部重定向到统一 token，别名区从主题文件中删除 |
| P1 | Theme Gallery 组件总览页 | 一个集中展示所有 token 与组件各状态（hover/pressed/disabled/聚焦）的页面，作为目视验收面；入口仅开发期可见 |
| P1 | 动效统一 | hover/press/主题切换的过渡时长与缓动收敛为动效 token（参考 transitions.dev 的 motion token 口径），时长值在验收时由本人确认 |
| P1 | 键盘焦点可见性 | Tab 序内控件有统一、可辨识的焦点框（shadcn focus ring 口径） |
| P2 | 窗口标题栏与任务栏跟随主题 | 标题栏色随主题变化（Win10/11 通过 ImmersiveDarkMode 属性）；价值待验证，可裁剪 |

**P0 闭环理由**：令牌层与组件库是本次重构的"产品"本身，缺任一个则回到散装状态；壳与插件迁移是"彻底重构"的定义（范围已拍板为全量）；不恢复切换入口则主题系统仍然半残；别名不收编则双轨病灶复发。P1 的 Gallery 是验收闭环（见 PM 备注），动效与焦点是"新设计系统"与"换皮"的分界，但单独缺失不阻断主线。

### 核心业务流程说明

- **设计令牌层业务流程**：定义 token 命名规范（语义名，不含具体色值含义，如 `SurfaceBgBrush` 而非 `Gray900Brush`）→ 亮/暗两套主题文件各自只填一组 token 值 → 组件样式与页面一律 `DynamicResource` 引用 → 换主题=换字典。触发：任何人想改一处视觉；结果：只改令牌层。
- **统一组件样式库业务流程**：从现状各视图里归纳重复模式（卡片、区块标题、表单行、工具栏按钮…）→ 提炼为组件样式及变体 → 提供 Gallery 页展示 → 插件迁移时把页面内手写样式替换为组件引用。触发：新页面开发或既有页面迁移；结果：长相由组件库决定，不由页面决定。
- **主题切换入口业务流程**：用户在顶栏点击主题按钮 → 三态轮切（跟随系统→亮→暗）→ ThemeManager 重挂字典 → 全部已开视图即时变色 → 选择写入配置文件 → 下次启动沿用。触发：用户点击；结果：即时、全局、可记忆。
- **插件视图迁移业务流程**：逐插件「迁移 → 亮/暗两态目视 → 该插件既有操作路径回归」为一个可交付增量；迁移期间新旧页面允许暂存（未迁移页面继续用别名桥接，不出现半截样式）。

## 4. 用户故事 & 验收标准

**Story 1: 单一真相源** (P0)
> 作为维护者，我想让任何视觉属性只有一个定义处，so that 改一处全局生效、亮暗两态永不漂移。

**验收标准：**
- Given 重构完成的代码库, When 对 `src/**/*.xaml`（排除 Themes 目录与令牌定义文件）grep 十六进制色值（`#[0-9A-Fa-f]{6,8}`）, Then 命中数为 0。
- Given 想把某个语义色（如卡片背景）整体调亮, When 只修改令牌层一个键值, Then 亮/暗两态下全部已迁移页面的该表面同步变化，无需触碰任何视图文件。
- Given 主题文件, When 检查其内容, Then 只含 token 定义，不含控件样式与兼容别名（别名收编完成的直接证据）。

**Story 2: 主题切换真正可用** (P0)
> 作为用户，我想在界面上切换亮/暗/跟随系统，so that 能即时对比颜色并长期按偏好使用。

**验收标准：**
- Given 程序运行中且已打开某插件页, When 点击顶栏主题按钮轮切三态, Then 主窗口与所有已打开视图（含弹过的窗口类页面）即时换色，不重启、不重开页面。
- Given 我切到 Light 并关闭程序, When 下次启动, Then 仍为 Light（配置记忆）。
- Given 选择"跟随系统"且 Windows 应用模式为暗, When 启动, Then 呈现暗色；系统主题注册表读取失败时按暗色兜底（现有 ThemeManager 行为）。

**Story 3: 组件一致性** (P0)
> 作为维护者，我想让同类控件在 21 个页面里只有一种长相，so that 新页面不再手写样式。

**验收标准：**
- Given 任意两个已迁移页面各放一个主操作按钮, When 并排目视比较, Then 高度、圆角、hover/pressed/disabled 态完全一致（同一组件样式，无页面内 ControlTemplate）。
- Given 已迁移页面, When 检查其 XAML, Then 不出现仅为本页服务的按钮/输入框内联 Template（一次性特例需白名单并注明理由）。

**Story 4: 全量插件迁移、行为零变化** (P0)
> 作为维护者，我想让 9 个插件工程全部落入新设计系统，so that 不留旧体系残部。

**验收标准：**
- Given 每个插件迁移完成的增量, When 跑既有 Release/Debug 测试, Then 全绿（当前 200 条口径不减少）。
- Given 全部迁移完成后的一次发布前目视, When 逐页走查每个插件的核心操作路径（选游戏数据、应用/还原补丁、字体逐条微调、数据浏览、价签等）, Then 每条路径可完成且无控件错位、文字截断、滚动失效。
- Given 未选择游戏数据时的提示态, When 在亮/暗两态下查看, Then 提示与占位样式均取自新 token，无残留旧配色。

**Story 5: 验收面（Theme Gallery）** (P1)
> 作为维护者，我想有一页能看全所有 token 与组件状态，so that 发布前目视从"逐页翻"变成"一页看完体系、再抽查页面"。

**验收标准：**
- Given Gallery 页, When 打开并轮切主题, Then 每个组件样式的全部状态（默认/hover/pressed/disabled/聚焦）与每个语义 token 的色卡即时呈现。
- Given 组件库新增一个变体, When 未在 Gallery 登记, Then 本次重构的收尾检查视为不通过（Gallery 与组件库一一对应）。

## 5. 相关非功能需求

- **性能**：主题切换为字典重挂，要求主观无可感知停顿（现有测试体系不覆盖渲染，验证方式=本人目视；若出现可感知卡顿再立专项排查，不预设毫秒阈值）。单文件发布（PublishSingleFile+ReadyToRun）体积与启动行为不劣化——以 `build.bat` 产物能正常启动为准。
- **兼容性**：Windows 10/11；100%–200% DPI 缩放下已迁移页面无可点击性丢失（目视抽查）；中文字体沿用 Microsoft YaHei 现状。
- **工程约束（继承既有不变式）**：`IUiPlugin.CreateView()` 契约与不变式 7（Abstractions 不引用 WPF）不动；`InternalsVisibleTo` 边界不因样式重构而扩大；主题应用仍走 ThemeManager 单一入口。
- **可回退性**：迁移按「壳层 → 逐插件」增量推进，每个增量可独立回滚且不破坏未迁移页面（依赖别名桥接层的存在期）。

## 6. 假设与开放问题

| # | 内容 | 类型 | 验证方式 |
|---|------|------|----------|
| A1 | 保留 PoE 金强调色（`#C89B3C` 一系）作为亮暗两态共用 Accent，视觉方向为"shadcn 中性基底 + 金色点缀" | 假设 | 令牌层初稿 + Gallery 首版目视时确认，不满意只改令牌层 |
| A2 | 主题按钮采用三态轮切而非下拉菜单（跟随系统/亮/暗） | 假设 | 实现后本人试用 |
| A3 | Gallery 页入口：仅当配置文件开关或调试构建暴露，正式版不显示 | 假设 | 与用户确认一次即可定稿 |
| Q1 | 动效 token 的具体时长/缓动值（transitions.dev 只给 Web 参考） | 开放问题 | 由 spec 阶段定为初始值，目视验收时一次调整 |
| Q2 | DataGrid/ListView 等复杂控件是否需要重模板（现状插件页未见重度使用） | 开放问题 | spec 阶段盘点实际用量后裁剪 |
| Q3 | P2（标题栏跟随主题）是否随本次交付 | 开放问题 | P0/P1 验收通过后再拍板 |

## 7. 范围口径备注

- 插件工程为 **9 个**（PriceTagger / DataBrowser / BagCleaner / Voyager / PoeCnPatch / Poe2Font / FxPatch / TermTranslator / AffixWorkbench），含各自附属窗口共 21 个 XAML、约 3553 行。
- 五个参考站均为 Web（React/Tailwind）组件库，无可直接安装的 WPF 资源；采信分工：**shadcn/ui = 架构蓝本（token 化 + copy-own 组件哲学）**，Beautiful UI = 信息密度与状态呈现参考，beui/rareui/transitions = 动效参数参考。

---

```
State: VERIFY → HANDOFF
Mode: Standard PRD
Passed: 每个 P0 均有可观察 AC（grep 硬编码=0、三态轮切即时生效、200 测试绿、逐页目视）；非目标显式列出 8 条；假设均带验证方式
Pending: A3（Gallery 入口方式）、Q1–Q3（技术口径）
Handoff: spec-maker（跨模块 token 命名、迁移顺序与桥接策略、WPF 实现风险属技术设计，不进 PRD 展开）
```
