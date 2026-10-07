# SPEC：UI 系统重铸（单主题 · 中性冷灰 + PoE 金）

> 对应 PRD：`PRD-ui-rebuild.md`（单主题版）。本 SPEC 解决技术设计：文件布局、令牌表、组件清单、替换映射、删除清单、实施步骤。
>
> **现状前提（2026-09-20 实测）**：全库 21 个 XAML / 3553 行；组件 key 被 **286 处引用 / 14 个页面**；`App.xaml.cs:50` 硬编码 `ThemeManager.Apply(ThemeManager.Theme.Light)`，即**程序本来就在浅色单主题下运行**，深色路径从未对用户开放。因此本次改动对已发布行为几乎零风险。

## 1. 架构决策（PRD 开放问题答复）

| # | 问题 | 决策 | 理由 |
|---|---|---|---|
| Q1 | 隐式默认样式（Window/TextBox/ComboBox/CheckBox/ScrollBar 无 `x:Key`）保留还是显式化 | **保留隐式**，但资源必须挂到 `Application.Resources` | 隐式样式的查找链包含 `Application.Resources`，插件视图挂在 App 可视树下即可命中。改成显式 key 会牵动大量页面引用，违背「页面不动」。风险与验证见 §8 |
| Q2 | 动效时长/缓动 | 令牌 `MotionFast = 0.12s`、`MotionNormal = 0.16s`，缓动 `KeySpline 0.4,0,0.2,1` | 参考 transitions.dev 的保守档；桌面工具不宜更长。目视后可调 |
| Q3 | `Components.xaml` 单文件还是拆分 | **拆 5 份 + 1 个合并入口** | 项目已有 `FxPatchEngine` 1783 行的教训；XAML 拆分成本低，按控件族分文件便于长期维护 |
| — | 令牌键名是否改为 shadcn 风格（`bg`/`fg`/`muted`） | **不改，沿用现有键名**（`WindowBgBrush` / `TextPrimaryBrush` / `AccentBrush` …） | 改键名 = 改 286 处引用，直接违背「页面不动」。shadcn 的分层思想照搬，命名沿用本工程既有风格 |
| — | 资源落点 | `src/PoEToolbox.Ui/Themes/`（**不放 App**） | Ui 被 9 插件 + App 共 10 个 csproj 引用；放 App 则插件视图取不到令牌 |

## 2. 文件布局

### 新增（`src/PoEToolbox.Ui/Themes/`）

| 文件 | 内容 | 预估行数 |
|---|---|---|
| `DesignSystem.xaml` | 合并入口，App 只引这一个 | ~15 |
| `Tokens.xaml` | 基础色板 + 语义令牌 + 尺寸/字阶/圆角/动效 | ~110 |
| `Typography.xaml` | `TitleText` / `SectionText` / `NormalText` / `SmallText` / `SectionTitle` / `FieldLabel` | ~70 |
| `Controls.Button.xaml` | `PrimaryButton` / `SecondaryButton` / `DangerButton` / `GhostButton` / `IconButton` / `CategoryChip` | ~150 |
| `Controls.Input.xaml` | TextBox / ComboBox / CheckBox / RadioButton / Slider（隐式）+ `FocusRing` | ~120 |
| `Controls.Layout.xaml` | Window（隐式）/ `CardBorder` / `GroupBox` / `ToolTip` / ScrollBar（隐式）/ `Divider` | ~110 |
| `Controls.Data.xaml` | DataGrid / ListView / TreeView / TabControl / ProgressBar | ~130 |

合并顺序（`DesignSystem.xaml` 内）：`Tokens` → `Typography` → `Controls.Button` → `Controls.Input` → `Controls.Layout` → `Controls.Data`。后合者覆盖先合者。

### 删除

| 路径 | 说明 |
|---|---|
| `src/PoEToolbox.App/Themes/dark.xaml` | 深色主题，从未对用户开放 |
| `src/PoEToolbox.App/Themes/light.xaml` | 内容迁入 `Tokens.xaml` + `Controls.*.xaml` |
| `src/PoEToolbox.App/Themes/ThemeManager.cs` | 整体删除（11–59 行） |

### 修改

| 文件 | 改动 |
|---|---|
| `src/PoEToolbox.App/App.xaml` | 第 5–7 行空 `Application.Resources` 改为合并 `DesignSystem.xaml` |
| `src/PoEToolbox.App/App.xaml.cs` | 删 49–50 行（`ThemeManager.AppResources` / `ThemeManager.Apply`） |
| `src/PoEToolbox.App/MainWindow.xaml` | 删 64–72 行（ThemeBtn 含注释行） |
| `src/PoEToolbox.App/MainWindow.xaml.cs` | 删 128–138 行（`ThemeBtn_Click`）、176–184 行（`UpdateThemeIcon`）、380 行（调用点） |
| `src/PoEToolbox.Shared/UILabels.cs` | 删 20 / 60 / 98 行 `"FollowSystem"` 三语条目 |
| `src/PoEToolbox.Plugins.BagCleaner/BagCleanerView.xaml` | 别名 → 真令牌（13 色）、10 处硬编码 → 令牌、`Card` → `CardBorder`。**不动结构** |

`App.xaml` 目标形态：

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="pack://application:,,,/PoEToolbox.Ui;component/Themes/DesignSystem.xaml"/>
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

> 构建前提：确认 `PoEToolbox.Ui.csproj` 未禁用默认 `Page` 项（`.xaml` 需以 `Page` 编译）。若被 `EnableDefaultPageItems=false` 关闭，需显式加 `<Page Include="Themes\*.xaml"/>`。资源随程序集走，不落 exe 外置文件（满足单文件发布约束）。

## 3. 令牌表（`Tokens.xaml`）

命名空间需加：`xmlns:sys="clr-namespace:System;assembly=System.Runtime"`。

### 3.1 语义色（键名沿用现状，仅改值）

| 键 | 值 | 说明 |
|---|---|---|
| `WindowBgBrush` | `#F4F4F5` | 窗口底（zinc-100） |
| `CardBgBrush` | `#FFFFFF` | 卡片/表面 |
| `CardHoverBrush` | `#F4F4F5` | 卡片 hover |
| `SurfaceBrush` | `#FAFAFA` | 次级表面、只读输入底（zinc-50） |
| `BorderBrush` | `#E4E4E7` | 边框（zinc-200） |
| `TextPrimaryBrush` | `#18181B` | 主文字（zinc-900） |
| `TextSecondaryBrush` | `#52525B` | 次文字（zinc-600） |
| `ButtonBgBrush` | `#F4F4F5` | 常规按钮底 |
| `ButtonHoverBrush` | `#E4E4E7` | 按钮 hover |
| `InputBgBrush` | `#FFFFFF` | 输入框底 |
| `TagSelectedBgBrush` | `#FDF3DC` | 标签/选中底（浅金） |
| `PlaceholderBrush` | `#A1A1AA` | 占位文字（zinc-400） |
| `AccentBrush` | `#C89B3C` | **PoE 金，不变** |
| `AccentHoverBrush` | `#DDBB68` | 金 hover |
| `AccentPressedBrush` | `#A87D2C` | 金 pressed |
| `SuccessBrush` | `#15803D` | 成功（green-700） |
| `WarningBrush` | `#B45309` | 警告（amber-700） |
| `ErrorBrush` | `#B91C1C` | 错误（red-700） |

### 3.2 新增语义色

| 键 | 值 | 用途 |
|---|---|---|
| `AccentForegroundBrush` | `#1A1A1A` | 金底按钮上的前景（顶替 `dark.xaml:65` 写死的 `#1A1A1A`） |
| `OverlayBrush` | `#80000000` | 半透明遮罩（顶替 `MainWindow.xaml:287`） |
| `DisabledBgBrush` | `#F4F4F5` | 禁用底 |
| `DisabledTextBrush` | `#A1A1AA` | 禁用文字 |
| `FocusRingBrush` | `#C89B3C` | 统一焦点环 |
| `RowHoverBgBrush` | `#F4F4F5` | 列表行 hover |
| `RowSelectedBgBrush` | `#FDF3DC` | 列表行选中（顶替 `BagCleanerView.xaml:28` 的 `#E6F0FE`） |
| `SuccessBorderBrush` | `#15803D` | 成功态边框（顶替 `BagCleanerView.xaml:57` 的 `#0B5A0B`） |

### 3.3 非颜色令牌

| 类别 | 键 / 值 |
|---|---|
| 圆角 | `RadiusSm`=6、`RadiusMd`=8、`RadiusLg`=10、`RadiusPill`=16 |
| 间距 | `SpaceXs`=4、`SpaceSm`=8、`SpaceMd`=12、`SpaceLg`=16、`SpaceXl`=24 |
| 控件高 | `ControlHSm`=28、`ControlHMd`=32、`ControlHLg`=36 |
| 字号 | `FontXs`=12、`FontSm`=13、`FontMd`=14、`FontLg`=20 |
| 动效 | `MotionFast`=`0:0:0.12`、`MotionNormal`=`0:0:0.16` |
| 缓动 | `EaseStandard`=`KeySpline 0.4,0,0.2,1` |

类型写法示例：

```xml
<sys:Double x:Key="RadiusMd">8</sys:Double>
<CornerRadius x:Key="CornerMd">8</CornerRadius>
<Thickness x:Key="PadMd">16</Thickness>
<Duration x:Key="MotionFast">0:0:0.12</Duration>
<KeySpline x:Key="EaseStandard">0.4,0,0.2,1</KeySpline>
```

## 4. 组件样式清单

**保留现状键名（286 处引用依赖，一律不动）**：`PrimaryButton`、`SecondaryButton`、`CardBorder`、`CategoryChip`、`TitleText`、`SectionText`、`NormalText`、`SmallText`。

**新增**：

| 键 | 用途 |
|---|---|
| `DangerButton` | 危险操作（GUI 约定：Secondary 形态 + ErrorBrush 文字 + 二次确认） |
| `GhostButton` | 无边框图标按钮（顶栏图标类） |
| `IconButton` | 36×36 方形图标按钮 |
| `FieldLabel` | 表单标签（自别名区提升为正式组件，12px + 下边距 4） |
| `SectionTitle` | 区块标题（自别名区提升为正式组件，14px Semibold + 下边距 12） |
| `Divider` | 分隔线 |
| `FocusRing` | 统一焦点环（键盘 Tab 可见） |

**隐式默认样式（无 key，见 Q1）**：Window、TextBox、ComboBox、CheckBox、RadioButton、Slider、ScrollBar、GroupBox、ToolTip、DataGrid、ListView、TreeView、TabControl、ProgressBar。

> **对 PRD 的修正 1**：PRD 要求删除 `SectionTitle` / `FieldLabel` / `Card` 三个样式别名。实际处理改为 —— `SectionTitle` 与 `FieldLabel` **提升为正式组件保留**（它们语义独立、无重复对应项，删除会改变 BagCleanerView 布局）；仅 `Card` 删除并统一到 `CardBorder`（会产生预期视觉变化，见 §7）。

## 5. 替换映射

### 5.1 硬编码色值（12 处）

| 文件:行 | 现值 | 目标令牌 |
|---|---|---|
| `MainWindow.xaml:100` | `#E53935` | `ErrorBrush` |
| `MainWindow.xaml:287` | `#80000000` | `OverlayBrush` |
| `BagCleanerView.xaml:28` | `#E6F0FE` | `RowSelectedBgBrush` |
| `BagCleanerView.xaml:41` | `#605E5C` | `TextSecondaryBrush` |
| `BagCleanerView.xaml:47` | `#FAFAFA` | `SurfaceBrush` |
| `BagCleanerView.xaml:48` | `#D0D0D0` | `BorderBrush` |
| `BagCleanerView.xaml:56` | `#107C10` | `SuccessBrush` |
| `BagCleanerView.xaml:57` | `#0B5A0B` | `SuccessBorderBrush` |
| `BagCleanerView.xaml:318/322/326/330` | `#F8F8F8` | `SurfaceBrush` |
| `dark.xaml:65`（随 dark 删除消失，其语义需落到 `PrimaryButton`） | `#1A1A1A` | `AccentForegroundBrush` |

### 5.2 兼容别名 → 真令牌（13 色，仅 BagCleanerView 使用）

`BgBase→WindowBgBrush`、`BgCard→CardBgBrush`、`BgHover→CardHoverBrush`、`BgPressed→SurfaceBrush`、`Border→BorderBrush`、`TextPrimary→TextPrimaryBrush`、`TextSecondary→TextSecondaryBrush`、`Accent→AccentBrush`、`AccentHover→AccentHoverBrush`、`AccentPressed→AccentPressedBrush`、`Warn→WarningBrush`、`Ok→SuccessBrush`、`Err→ErrorBrush`。

> 注意 `Accent` 在 `light.xaml:230` 的取值是蓝色 `#0078D4`，替换后将变为金色 `#C89B3C` —— 这是一处**预期视觉变化**，见 §7。

## 6. 实施步骤（三个提交，每步可独立验证）

**提交 1 — 资源落点迁移 + 两层拆分**
1. 在 `PoEToolbox.Ui/Themes/` 建 `Tokens.xaml` / `DesignSystem.xaml`，值先**原样搬** `light.xaml`（不换色），保证此步零视觉变化。
2. 把 `light.xaml` 的组件样式按 §2 拆入 `Typography` / `Controls.*.xaml`。
3. `App.xaml` 合并 `DesignSystem.xaml`；删 `App.xaml.cs:49-50`；删 `dark.xaml` / `light.xaml` / `ThemeManager.cs`。
4. 删 ThemeBtn（`MainWindow.xaml:64-72`、`MainWindow.xaml.cs:128-138/176-184/380`、`UILabels.cs:20/60/98`）。
5. **验证**：构建通过 + 启动 + 视觉与改动前一致（此时尚未换肤）。

**提交 2 — 换肤 + 清债**
6. `Tokens.xaml` 按 §3 换中性冷灰值 + 新增令牌。
7. 执行 §5.1 / §5.2 全部替换。
8. 组件样式改用尺寸/圆角/动效令牌，补齐 §4 新增组件与隐式样式。
9. **验证**：逐页目视 21 个 XAML，重点核对 §7 的预期变化点。

**提交 3 — 护栏**
10. 新增测试：扫描 `src/**/*.xaml`，排除 `Themes/` 目录与令牌定义文件，断言 `#[0-9A-Fa-f]{6,8}` 命中数为 0。
11. **验证**：`dotnet test tests/PoEToolbox.Tests -c Release` 全绿（176 条口径不减少）。

## 7. 预期视觉变化点（目视重点）

1. **全局**：暖灰 → 中性冷灰，金色更跳。所有页面。
2. **BagCleanerView 中原本蓝色的元素** → 变金 `#C89B3C`（原 `Accent` 别名在浅色下配成了 `#0078D4`）。
3. **BagCleanerView 中 `Card` 区块** → 圆角 8→10、内边距 20→16、新增下边距 16（统一到 `CardBorder`）。
4. **顶栏**：主题按钮消失，需确认布局不塌陷、无空洞。

## 8. 风险与验证

| 风险 | 应对 |
|---|---|
| 隐式样式跨程序集不生效（Q1 假设） | 提交 1 完成后，打开 DataBrowser 搜索框与 BagCleaner 输入框，确认圆角/内边距样式生效；不生效则改为合并到每个插件视图的 `Resources`（届时需回到 Q1 决策） |
| `PoEToolbox.Ui` 的 `.xaml` 未被编译为 `Page` | 提交 1 前先确认 csproj；pack URI 404 会直接抛异常，启动即失败，易发现 |
| 删除 ThemeBtn 后后台代码残留引用 | 删完整体构建，`ThemeBtn` 字段由 g.cs 生成，XAML 删除后 g.cs 同步消失，编译错误会暴露全部残留 |
| 换肤后对比度不足（金底深字、次文字 zinc-600） | 提交 2 目视重点核对：金色主按钮文字、`TextSecondaryBrush` 在白底上的可读性 |
| 单文件发布 | 资源编译进程序集，无需 `CopyToOutputDirectory`；发版前跑一次 `dotnet publish` 启动验证 |

## 9. 验收清单

- [ ] `src/**/*.xaml`（排除 Themes 与令牌文件）中 `#[0-9A-Fa-f]{6,8}` 命中数 = 0
- [ ] `Themes/` 下只有 `DesignSystem.xaml` / `Tokens.xaml` / `Typography.xaml` / `Controls.*.xaml`，无 `dark.xaml`、无别名区
- [ ] grep `ThemeManager` / `ThemeBtn` / `FollowSystem` / `IsSystemDark` / `UpdateThemeIcon` 命中数 = 0（文档与记忆除外）
- [ ] 启动后顶栏无主题按钮、布局不塌陷
- [ ] 21 个 XAML 逐页目视无破版、无文字看不清、无残留旧暖灰
- [ ] §7 四处预期变化点已确认接受
- [ ] Release 测试全绿（176 条不减少）
- [ ] `dotnet publish` 产物可正常启动且配色正确

---

```
State: SPEC READY
Scope: 单主题浅色 · 令牌/组件两层 · 资源落点 PoEToolbox.Ui · 删除切换
Key decisions: 沿用现有令牌键名（不动 286 处引用）｜隐式样式保留并挂 Application.Resources｜组件按族拆 5 份
Deviations from PRD: SectionTitle/FieldLabel 提升为正式组件保留（仅删 Card）
Open: 无（Q1–Q3 已在 §1 决策）
```
