# PRD：UI 系统重铸（单主题 · 中性冷灰 + PoE 金）

> **本 PRD 取代** `PRD-ui-refactor.md`、`PRD-ui-refresh.md`、`PRD-ui-rebuild.md`（深色/浅色双主题版）。
>
>（2026-10-08 清理：前两份已被本 PRD 取代且方案作废，随v0.2.6 一并从仓库删除；内容仍可在 git 历史 `c3a48b746^` 查到。本段保留取代关系，以免日后误以为漏了文档。）
>
> **2026-09-20 用户拍板三项**：① **只保留一套主题（浅色）**，不做深浅切换；② **彻底删除主题切换入口与 ThemeManager**；③ 视觉方向 = 中性冷灰 + PoE 金。
>
> **为什么力度收敛**：实测推翻了旧 PRD 的前提——全库已有 **286 处组件 key 引用，分布在 14 个页面**，页面早就在按设计系统干活。真正欠的债只有：两套主题文件全量复制、13 个语义漂移的兼容别名、12 处硬编码、主题入口被藏。既然不再有双主题，前三笔进一步收缩为「一份令牌 + 一份组件」，第四笔直接删除。

## 1. 项目概述

> **PoE Toolbox UI 重铸**：把「深浅两套主题文件各自全量复制 + 一套能用的雏形组件库 + 若干硬编码残留 + 被隐藏的切换按钮」，收敛为**单主题的两层结构——`Tokens.xaml`（只放值）+ `Components.xaml`（只放样式，唯一一份）**；同时把灰阶从 Material 暖灰换成中性冷灰，保留 PoE 金作强调色。页面 XAML 一行不改。

**核心目标**

1. **两层分离**：令牌层只放值（颜色/字号/间距/圆角/控件高度/动效时长），组件层只放样式且唯一一份。改视觉只动令牌层。
2. **视觉换肤**：灰阶 `#F5F5F7/#FFFFFF/#DDDDDD`（带黄的 Material 灰）→ 中性冷灰（zinc 系）；强调色统一为 PoE 金 `#C89B3C`。观感收益 100% 来自这一层，14 个页面自动跟随。
3. **删除主题切换**：删 `ThemeBtn`（`MainWindow.xaml:65-72`）、删 `ThemeManager.cs`（`App/Themes/ThemeManager.cs:11-59`）、删 `dark.xaml`；启动即加载唯一一套资源。
4. **清债**：13 个 BagCleaner 兼容别名删除；12 处硬编码色值改令牌。
5. **补齐组件库缺口**：新增 DataGrid/ListView/TreeView/TabControl/ProgressBar/ToolTip/RadioButton/GroupBox/Slider 样式，统一焦点环。
6. **护栏**：测试内断言——Themes 目录与令牌文件之外，XAML 中 `#[0-9A-Fa-f]{6,8}` 命中数为 0。

**明确不做**

- ❌ 不做深浅双主题、不做主题切换、不做「跟随系统」（已拍板删除）
- ❌ 不逐页重写布局与信息架构（14 个页面 XAML 保持不动）
- ❌ 不改任何功能行为与业务流程
- ❌ 不引入第三方 WPF UI 库（MaterialDesign / HandyControl / Avalonia / WebView2）
- ❌ 不顺带 MVVM 化
- ❌ 不做 Theme Gallery 页
- ❌ 不做用户自定义主题包、窗口磨砂、标题栏跟随主题
- ❌ 不追求像素级复刻 shadcn/ui 站点组件，只借鉴其令牌分层与 copy-own 哲学

## 2. 目标用户

- **唯一用户/维护者：主人本人**。单人本地工具，无外部用户。
- **已知场景**：Windows 桌面、中文界面、长时间盯数据表格与输出面板；发布前逐页目视（既有验收习惯，CHANGELOG D6）。
- **核心痛点**：① 改一个视觉属性要跨两份主题文件找全；② 视觉语言偏 2019，灰中带黄偏闷；③ 13 个兼容别名与真令牌语义打架（`Accent` 在深色是金、在浅色曾配成蓝）。
- **验证方式**：本人逐页目视 + 下述 grep 硬口径。

## 3. 功能矩阵与业务说明

| 优先级 | 功能 | 描述 |
|---|---|---|
| P0 | 令牌层 `Tokens.xaml` | 基础色板（中性冷灰阶 + 金阶 + 状态色）→ 语义令牌（Bg / Surface / Border / Text / Accent / Status）；外加字号、间距、圆角、控件高度、动效时长 |
| P0 | 组件层 `Components.xaml` | 唯一一份组件样式，合并令牌层；改视觉不动这一层 |
| P0 | 视觉换肤 | 灰阶换中性冷灰，强调色统一 PoE 金 `#C89B3C`；字阶/间距/圆角/控件高度全部令牌化 |
| P0 | 删除主题切换 | 删 `ThemeBtn` 及其后台 `ThemeBtn_Click`；删 `ThemeManager.cs` 与其配置键；删 `dark.xaml`；`App.xaml` 合并唯一资源字典 |
| P0 | 别名删除 | 删 13 个 `Bg*`/`Accent`/`AccentHover`/`AccentPressed`/`Warn`/`Ok`/`Err` 别名 + 3 个 `SectionTitle`/`FieldLabel`/`Card` 样式 |
| P0 | 资源落点迁移 | 主题资源从 `App/Themes/` 迁到 `src/PoEToolbox.Ui/Themes/`（Ui 被 9 插件 + App 共 10 个 csproj 引用，放 App 插件取不到） |
| P1 | 硬编码清零 | `MainWindow.xaml:100`（`#E53935`）、`:287`（`#80000000`）、`BagCleanerView.xaml:28/41/47/48/56/57/318/322/326/330`（浅色专用色）共 12 处改令牌。单主题下危害已从「切换不变色」降级为「改色板要到处找」，但仍一并清掉，成本极低 |
| P1 | 组件库补齐 | DataGrid / ListView / TreeView / TabControl / ProgressBar / ToolTip / RadioButton / GroupBox / Slider 样式 + 统一 focus ring |
| P1 | 动效令牌 | hover/press 过渡收敛为令牌；初值 120ms（hover）/ 160ms（展开），缓动 `CubicBezier(0.4,0,0.2,1)`，目视后可调 |
| P1 | 护栏检查 | 测试断言：Themes 目录与令牌定义文件之外，XAML 中 `#[0-9A-Fa-f]{6,8}` 命中数为 0 |

**P0 闭环理由**：令牌层 + 组件层 + 换肤 + 删切换 + 删别名是一个垂直切片——缺任何一个，「改一处生效全局」的结果都观察不到。迁移落点是硬前提：不迁到 Ui，插件视图拿不到令牌，等于没做。

## 4. 用户故事 & 验收标准

**Story 1：单一真相源** (P0)
> 作为维护者，我想让任何视觉属性只有一个定义处，so that 改一处全局生效。

- Given 重构后的代码库，When 对 `src/**/*.xaml`（排除 Themes 目录与令牌定义文件）grep `#[0-9A-Fa-f]{6,8}`，Then 命中数为 **0**。
- Given 想把卡片背景调整一个色阶，When 只改语义令牌一个键，Then 全部页面的该表面同步变化，无需触碰任何视图 XAML。
- Given 主题目录，When 检查其内容，Then 只有 `Tokens.xaml` 与 `Components.xaml` 两份，无 `dark.xaml`、无兼容别名区、无 `ThemeManager.cs`。

**Story 2：视觉换肤生效** (P0)
> 作为用户，我想让工具看起来是现代的，so that 长时间盯表格不累。

- Given 任意已引用组件 key 的页面，When 目视，Then 背景/卡片/边框呈中性冷灰（无黄偏），金 `#C89B3C` 作强调仍清晰可辨。
- Given 21 个 XAML，When 逐页目视，Then 无文字看不清、无控件错位、无残留旧暖灰块。
- Given 金色主按钮，When 查看其文字，Then 前景为深色令牌（沿用 `#1A1A1A` 语义，不再是写死值），对比度足够。

**Story 3：切换代码清干净** (P0)
> 作为维护者，我想让删掉的能力不留残骸。

- Given 代码库，When grep `ThemeManager` / `ThemeBtn` / `FollowSystem` / `IsSystemDark`，Then 命中数为 0（文档与记忆文件除外）。
- Given 主窗口顶栏，When 目视，Then 无主题按钮占位空隙、布局不塌陷。

**Story 4：行为零回归** (P0)
> 作为维护者，我想让翻新不影响任何功能。

- Given 全部改动完成，When 跑 Release 测试，Then 全绿（2026-09-19 实测 176 条口径不减少）。
- Given 发布前逐页目视，When 走查核心操作路径（选游戏数据、应用/还原补丁、字体微调、数据浏览、价签、背包清理校准、词缀工作台配色），Then 每条路径可完成。
- Given `dotnet publish` 产物，When 启动，Then 正常启动且配色正确（资源编译进程序集，不依赖 exe 旁外置文件）。

## 5. 相关非功能需求

- **兼容性**：`net10.0-windows`；100%–200% DPI 下不破版；中文沿用 Microsoft YaHei。
- **性能**：不引入大体积资产；单文件发布体积与启动行为不劣化。
- **工程约束**：`IUiPlugin` / `IPlugin` 契约不动；`InternalsVisibleTo` 边界不因样式重构扩大。
- **发布约束**：发布产物只带单个 exe，运行时要读的外置文件一律失效 —— 新增 XAML 资源以 `Page`/`Resource` 编译进程序集，不得用 `CopyToOutputDirectory`。
- **可回退**：分三个提交落地 —— ① 资源落点迁移 + 两层拆分；② 换肤 + 清别名硬编码；③ 删切换代码。任一提交可独立回滚。

## 6. 假设与开放问题

| # | 内容 | 类型 | 验证方式 |
|---|---|---|---|
| A1 | 视觉方向 = shadcn 式中性冷灰（zinc 系）+ PoE 金 `#C89B3C` | 已拍板 | 首版目视；不满意只改令牌层 |
| A2 | 单主题（浅色），不做深浅切换 | 已拍板 | — |
| A3 | ThemeBtn 与 ThemeManager 彻底删除，不留兼容路径 | 已拍板 | — |
| A4 | 页面 XAML 一行不改（BagCleanerView 的 10 处仅做色值→令牌的机械替换，不动结构） | 假设 | 实施中若发现其他页面有浅色专用色残留，改一处记一处 |
| Q1 | 无 `x:Key` 的隐式默认样式（`light.xaml` 中的 Window/TextBox/ComboBox/CheckBox/ScrollBar）保留隐式还是改显式 key | 开放 | 交 SPEC 决策（跨程序集隐式样式传播需实测确认） |
| Q2 | 动效令牌具体时长/缓动 | 开放 | SPEC 定初值 120/160ms + `CubicBezier(0.4,0,0.2,1)`，目视后一次调整 |
| Q3 | `Components.xaml` 是否按控件拆分为多个文件（Button.xaml / Input.xaml / Data.xaml …） | 开放 | 交 SPEC 决策（单文件约 300 行尚可，拆分利于长期维护） |

---

```
State: VERIFY → HANDOFF
Mode: Standard PRD
Passed: 范围经实测收敛（286 处组件引用 ⇒ 页面不动）；三项已拍板；P0 均有可观察 AC（grep=0、两份资源文件、切换代码命中=0、176 测试绿）；非目标 8 条显式列出；开放问题 3 条交 SPEC
Pending: Q1–Q3 技术口径
Handoff: spec-maker
```
