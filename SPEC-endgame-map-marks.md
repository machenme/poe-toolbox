# 挖坟词缀（终局地图内容标记）— 实施计划

> Standard SPEC ｜ 2026-09-24 ｜ 上游：`PRD-endgame-map-marks.md`（D1–D18）
> 模式判定：新插件工程 + 跨 `Core`/`Shared`/`Ui`/`Plugins` 四层 + 对游戏数据的不可逆写入 + 跨会话 ⇒ Standard。

## 1. 评审备注

- **W0 是闸门，不是可选项。** A-1（`Datc64File` 对 `EndgameMaps` 的往返正确性）与 A-5（dat 颜色语法）未验证前，W1/W2 一行都不许写。历史教训：记忆里已记有「`Datc64File` 对部分表失败」。
- **颜色切片（W2）被主人授权可整块取消**（D17）。W1 结束时模块必须已是完整可发布功能。
- **人类保留地**：游戏数据写入路径（`FxEngineRunner` → fx-patch 引擎）与 `uisettings.xml` 改动。这两处改动只能走既有通道，本 SPEC 不引入任何新的写入路径（D6）。
- **无并发风险**：单用户桌面工具，页面同一时刻只持一个 `GameDataAccess`；写入由 `FxEngineRunner` 的 busy 互斥串行化（既有机制，不新增）。
- 未发现需要新增外部依赖、服务、存储或配置项。

## 2. 技术决策

| 决策 | 当前证据 | 选择 | 理由 | 风险 / 验证 |
|---|---|---|---|---|
| 数据读取 | `GameDataAccess.OpenReadOnlyMapped(path, bundleDirectory)`（`Shared/GameDataAccess.cs:118`）；`ReadFile(path)` :273 | 沿用 `OpenReadOnlyMapped`，与 `AffixDataService.ConnectAsync`（`AffixDataService.cs:209`）同款 | 既有模式，句柄生命周期已被验证过 | 写盘前必须 `ReleaseFileLocks()`（`AffixWorkbenchView.xaml.cs:1086-1088` 的注释已写明原因：Windows 拒绝替换有映射打开的文件） |
| 表解析 / 回写 | `Datc64File.FromBytes(data, tableName, is64Bit, validFor)`（`Core/Binary/Datc64/Datc64File.cs:39`）→ `ToBytes()` :424；表名 PascalCase（现网写法 `"BaseItemTypes"`，`Cli/Program.cs:92`） | `FromBytes(bytes, "EndgameMaps", true, 2)` | 唯一现成的 datc64 读写实现 | **A-1 已验证**：语义无损 + 二次往返稳定 + 改一行不漂移；**字节不恒等**（+696 B）⇒ 见 C1/C2 |
| 列定位 | `schema.min.json` v7：`EndgameMaps` 27 列，第 25 列（0 基）`type=string`、无列名 | 运行时取 `Columns[25]`，**校验 `Type == "string"`，不符或列数 < 26 则抛可展示异常并终止** | D3；硬编码索引 + 类型双保险，宁可失败不可改错列 | 版本漂移时功能停用（可接受，优于写坏数据） |
| 产物通道 | `AffixPatchBuilder.FileChange(GamePath, byte[] Original, byte[] Modified)` + `Build(changes)`（`Shared/AffixPatchBuilder.cs:20/44`） | **复用 `AffixPatchBuilder`，新增 `PatchId = "endgame-maps"`** | 与词缀上色同一条通道，天然叠加/可还原；`FileChange` 三元组正好装下「整表替换」 | 需在 `AffixPatchBuilder` 里参数化 `PatchId`（现为 `const`，:18） |
| 执行 | `FxEngineRunner.RunAsync(...)`（`Ui/FxEngineRunner.cs`，`internal`） | 沿用；**在 `Ui.csproj` 的 `InternalsVisibleTo` 里加一行新插件** | Ui.csproj 现有三条 `InternalsVisibleTo`，同款加一行 | 不加会编译不过，属机械改动 |
| 语言判定 | `AffixDataService.DetectClientLanguage`（`private static`，`AffixDataService.cs:285`） | **把它与 `BalanceRoot`（:28）提到 `Shared`**，两处共用 | 避免复制第二份语言判定逻辑，两份会漂移 | 改动 `AffixDataService` 两行；跑 `AffixRampServiceTests` 回归 |
| 颜色模块归属 | `WorkbenchPalette.cs`（76 行）+ `ColorEditorWindow`（xaml 142 / cs 327）在插件内；模型层已在 `Shared/AffixColorScheme.cs` | **W2：搬到 `PoEToolbox.Ui`**（已拍板 A） | 插件间零互引是硬约定；Ui 有 `UseWPF`、已引用 `Shared`、构造只吃 Shared 类型；测试工程零引用 ⇒ 无覆盖损失 | **D16**：`WorkbenchPalette` 是 `public static` 全局且 `Update()` 整体替换 ⇒ 必须改成分桶登记 |
| 颜色语法 | csd 为 `<id>{{…}}</id>`（`Shared/CsdDocument.cs:19`）；dat 侧第三方用法为 `[<red>{…}]` | **dat 用 `<id>{…}`（单花括号）**，另写转换器，**不复用 `CsdDocument.TryApplyColor`** | D14；两套语法不同 | **A-5**：W0 验证；不通过则砍 W2（主人已授权） |
| 颜色定义落地 | `Shared/UISettingsDoc.cs`；`ExternalColors` | 自定义色写 `uisettings.xml`（`<Colour id value="r,g,b"/>`）；内置色（red/green/unique…）只读、不动 uisettings | D13/D15 | 与词缀上色共用命名空间 ⇒ id 带 `EM` 前缀 + 禁止与游戏已有色同名（D13） |
| 规则持久化 | `ConfigService.DataDirectory`（`%LOCALAPPDATA%\PoEToolbox\`） | JSON 存 `<DataDirectory>/endgame-maps/rules.json` | 与既有「配置/缓存放用户目录」一致；发布只有单个 exe，禁止往 exe 旁写 | 无 |
| 状态判定 | `ComputeState` 基于内容（`Shared/Fx/FxPatchState.cs`） | 沿用：应用前比对「连接时快照」，已生效的行跳过 | 记忆硬结论：判据必须落在**逻辑内容**上，不许用物理位置 | 无 |

### 备选方案（已放弃）

| 备选 | 放弃原因 | 何时重新评估 |
|---|---|---|
| 新插件直接引用 `Plugins.AffixWorkbench` 复用颜色 | 违反「插件间零互引」硬约定 | 该约定被推翻时 |
| 只搬调色板、取色窗口写精简版（原方案 B） | 等于复制 469 行且长期双份维护；实测 A 仅需搬 545 行 | A 的 D16 分桶改造失败时 |
| 用 fx-patch 的 `edittext` op 改 dat | `edittext` 是整文件文本替换、要求目标串唯一（`FxPatchOperations.cs:126-148`），datc64 是二进制，不适用 | — |
| ~~原地覆盖字符串（不改长度则不动偏移）~~ | 受新文本长度限制，换版本即崩；W0 已证明 `Datc64File` 重编码语义无损且稳定，不需要它 | 不再评估 |
| 建新类库放共享 UI | Ui 已被 9 个插件引用，够用；新建工程只为搬两个文件不划算 | Ui 出现职责冲突时 |

## 2.1 W0 实测结果（2026-09-24，真实 PoE2 Steam 客户端 → PASS）

`probe-endgamemaps` 输出摘要（完整数据见 PRD「W0 实测结果」）：

- 表在 **base 与繁中覆盖层都存在**；**173 行 / 29 列**（注意：内嵌 schema v7 只有 27 列，运行时用的是下载的新 schema ⇒ **列数会随版本变，只能靠 `Columns[25].Type` 校验兜底**）
- 目标列 `Columns[25] = "Unknown25"`，`Type = "string"` ✓
- 繁中列非空 **20 条**，`閃光的未必是金……` / `近乎天堂。` / `好人一個……` …⇒ **内置 19 条全部可命中**，另有 1 条未匹配：`瘋狂酋長……`（行 83）⇒ 列表里显示为「未匹配」，让用户自己补规则
- **⚠️ 表里有两个字符串列，别改错**：列 2 `FlavourText` 非空 172 条（每张图的风味描述，`船身撞擊並碎裂在岸上。`）；**列 25 `Unknown25` 非空 20 条才是目标**。交叉验证：对方 CSV 的行号 144 = `墮落的起源……`，与我方实测行号 144 完全吻合
- base 与繁中覆盖层**结构完全一致、行号对齐**（都是全量表，不是差量表）⇒ 按客户端语言只改一份即可，行号可直接复用
- 往返 50,863 → 51,559 B（+696，+1.4%），**语义全等 / 二次往返稳定 / 改一行其余不漂移**
- 该列 **0 条**含 `<` 与 `{` ⇒ **A-5 仍无证据**，W2 只能靠应用后目视（A9）判定生死

### 由此新增的两条硬约束（必须写进实现）

| # | 约束 | 落点 |
|---|---|---|
| **C1** | 状态判定与「已生效跳过」**只比 `Unknown25` 文本**（`MarkRow.State`），绝不比整文件字节或 SHA | `EndgameMapsService` 的 `State` 计算 |
| **C2** | **没有任何一行需要改动时不产出 `FileChange`，不写盘** | `ComputeChanges()` 返回空 ⇒ 直接提示「已是最新」，不调 `AffixPatchBuilder.Build` |

### 附带教训（写给以后写同类探测的人）

数组列的值是 `List<object>`，用 `Equals` 比的是**引用** ⇒ 会像本次一样报出 519 处假漂移。**一律用 `JsonSerializer.Serialize` 比对**（`CmdCmp` 已是这个做法）。

## 3. 数据模型

> 全部为本模块新增（`Shared` 或插件工程内），不改任何既有实体。

#### `EndgameMarkRule`（一条规则，持久化到 `rules.json`）

| 字段 | 类型 | 约束 | 备注 |
|---|---|---|---|
| `Id` | `string` | 非空、唯一 | 内置规则用 `builtin-<n>`，用户规则用 GUID |
| `MatchText` | `string` | 非空 | 匹配用纯文本（去开头 `[...]` 前缀后精确比对，D4） |
| `Template` | `string` | 可空；含 `{原名}` 占位符 | 渲染后的写入值；为空 = 该规则禁用 |
| `Enabled` | `bool` | 默认 `true` | 列表勾选 |
| `BuiltIn` | `bool` | — | 内置规则不可删除，可改 `Template` / `Enabled` |

#### `EndgameMarkScheme`（持久化根）

| 字段 | 类型 | 备注 |
|---|---|---|
| `Version` | `int` | 结构版本，便于以后迁移 |
| `Rules` | `List<EndgameMarkRule>` | 内置 18 条默认存在，可增删改 |

#### `MarkRow`（运行时行视图，不持久化）

| 字段 | 类型 | 备注 |
|---|---|---|
| `RowIndex` | `int` | dat 行号，仅展示 |
| `Original` | `string` | 该列原文（连接时快照） |
| `PlainText` | `string` | 去括号前缀后的匹配键 |
| `Rendered` | `string` | 模板渲染结果（应用后写入的目标文本） |
| `State` | `enum` | `Unchanged` / `WillChange` / `AlreadyApplied` / `Ambiguous` / `Unknown` |

## 4. 代码结构与变更位置

新增工程（proposed）：`src/PoEToolbox.Plugins.EndgameMaps/`

| Path | 现状 | 变更 |
|---|---|---|
| `src/PoEToolbox.Plugins.EndgameMaps/PoEToolbox.Plugins.EndgameMaps.csproj`（proposed） | — | 照抄 `Plugins.AffixWorkbench.csproj` 的四个 `ProjectReference`（Abstractions / Shared / Ui / Core） |
| `…/EndgameMapsPlugin.cs`（proposed） | — | 照抄 `AffixWorkbenchPlugin.cs`：`IUiPlugin`，`Name = "挖坟词缀"`，`Order = 19`，订阅 `ReleaseGameDataLocksRequested`，`OnDeactivated`/`TryPrepareForAppClose` 释放句柄 |
| `…/EndgameMapsService.cs`（proposed） | — | 照抄 `AffixDataService` 的生命周期方法：`ConnectAsync` / `VerifyUnchanged` / `AdvanceBaseline` / `MarkStale` / `ReleaseFileLocks` / `Dispose`；新增 `LoadRows()` / `ComputeChanges()` |
| `…/EndgameMarkRenderer.cs`（proposed） | — | 纯函数：`Render(template, plainText)`；`ParseToInlines(text)`（W2，dat 语法 → 彩色 Inline） |
| `…/EndgameMapsView.xaml(.cs)`（proposed） | — | 照抄 `AffixWorkbenchView` 的 `Output`/`UiStatus`/`RunBusyAsync`/`FxEngineRunner` 接线；列表两列「原文本 / 新文本」 |
| `src/PoEToolbox.Shared/AffixPatchBuilder.cs` | `PatchId` 为 `const`（:18） | **参数化 `PatchId`**：`Build(changes, patchId, patchesDir?)`，保留原签名重载指向 `affix-workbench` |
| `src/PoEToolbox.Shared/AffixColorScheme.cs` | 已有模型层 | 加 `EndgameMarkRule` / `EndgameMarkScheme`（放 `Shared` 便于测试工程引用） |
| `src/PoEToolbox.Shared/…`（语言判定，proposed `ClientLanguage.cs`） | `AffixDataService.DetectClientLanguage` 是 `private static`（:285） | 提出为 `Shared` 公开方法；`AffixDataService` 改为调用它 |
| `src/PoEToolbox.Ui/WorkbenchPalette.cs`（proposed，W2） | 现位于 `Plugins.AffixWorkbench/` | 搬入 `PoEToolbox.Ui`；**`Update()` 改分桶**：`Register(owner, defs)` / `Unregister(owner)` |
| `src/PoEToolbox.Ui/ColorEditorWindow.xaml(.cs)`（proposed，W2） | 现位于 `Plugins.AffixWorkbench/` | 搬入 `PoEToolbox.Ui`；`xmlns:local` 改 `PoEToolbox.Ui`；标题/提示文案参数化 |
| `src/PoEToolbox.Ui/PoEToolbox.Ui.csproj` | 三条 `InternalsVisibleTo` | 追加 `PoEToolbox.Plugins.EndgameMaps` |
| `src/PoEToolbox.App/PluginManager.cs` | :37 注册 `AffixWorkbenchPlugin` | 追加 `Register(new PoEToolbox.Plugins.EndgameMaps.EndgameMapsPlugin(EventBus));` |
| `src/PoEToolbox.App/PoEToolbox.App.csproj` | 11 条插件 `ProjectReference` | 追加本插件 |
| `src/PoEToolbox.Cli/Program.cs` | 命令分发表（:30-56）+ `CmdCmp`（:766 已有往返比对，可参照） | **已加 `probe-endgamemaps`**（分发表 :50 后、`CmdProbeEndgameMaps`、`PrintUsage` 一行）。只读，不写任何文件；验证完保留为诊断命令 |
| `tests/PoEToolbox.Tests/` | 已引用 `Plugins.AffixWorkbench`（csproj:32） | 追加本插件引用 + 新测试文件（模板渲染、列定位、歧义拒绝） |

**调用方向**：`App → Plugins.EndgameMaps → {Ui, Core, Shared}`，与既有插件同向，无反向边。

## 5. 数据流

### 读取（W1）

```
点「读取地图文本」
  → GameDataAccess.OpenReadOnlyMapped(路径)          // GameDataAccess.cs:118
  → 判定客户端语言（Shared 新方法）
  → path = data/balance/<语言>/endgamemaps.datc64
  → ReadFile(path) → byte[] before                    // 快照存内存，作为还原基准（D8）
  → Datc64File.FromBytes(before, "EndgameMaps", true, 2)
  → 校验 Columns[25].Type == "string"，否则终止
  → 逐行取第 25 列 → MarkRow(Original/PlainText)
  → 套规则：PlainText 命中唯一 → Rendered；命中 0 → Unknown；命中 >1 → Ambiguous
```

### 应用（W1）

```
点「应用」
  → RequirePoe2() + IsConnected 校验
  → VerifyUnchanged()（比对 before 快照；不符 → 提示重读，不写）
  → 存在 Ambiguous 行 → 拒绝整批，指出命中行号，不写盘      // A7
  → 构造 FileChange(path, before, afterBytes)             // afterBytes = 改后的 ToBytes()
  → AffixPatchBuilder.Build(changes, "endgame-maps")
  → ReleaseFileLocks()                                    // 必须在写盘前，否则 Windows 拒绝替换
  → FxEngineRunner.RunAsync(..., [gd, jsonPath, "apply"], ...)
  → AdvanceBaseline(changes) / MarkStale()
```

### 还原（W1）

```
点「还原」
  → 定位 <PatchesDirectory>/endgame-maps/endgame-maps.patch.json
  → 不存在 → 提示「还没应用过」
  → ReleaseFileLocks() → FxEngineRunner(..., "revert") → MarkStale()
```

与 `AffixWorkbenchView.Apply_Click`（:1047-1109）/ `Revert_Click`（:1136-1169）逐行同构，可直接照抄骨架。

### W2（颜色）

在 `ComputeChanges` 里多产出一个 `FileChange(UiSettingsPath, origUi, modUi)`（与 `AffixDataService.ComputeChanges` :468 的既有写法一致）。**仅当存在自定义色时产出**；只用内置色时不碰 uisettings（A15）。取消 W2 = 删掉这段 + 不搬 Ui 里的两个文件，其余不受影响。

## 6. 失败生命周期

| 失败点 | 行为 | 是否落盘 |
|---|---|---|
| 列定位失败（列数不足 / 类型不是 string） | 抛可展示异常，页面提示「表结构已变化，本功能不可用」 | 否 |
| 读取时文件不存在 | 提示「客户端里没有这张表」，保留空列表 | 否 |
| `VerifyUnchanged` 发现文件被外部改动 | 提示重读，中止 | 否 |
| 命中多行（Ambiguous） | 拒绝整批，列出行号 | 否 |
| `ToBytes()` 抛异常 | 捕获并提示，中止 | 否 |
| 引擎执行失败 | `FxEngineRunner` 返回 false，提示「详见引擎输出」 | 引擎自行保证；本模块不 `AdvanceBaseline` |
| 补丁 bundle 悬空（`IsMissingPatchBundle`） | 照抄 `RecoverBrokenPatchBundleAsync`（:1115）：释放句柄 → `PatchBundleRepair.RepairIfBroken` → 重连 | 否 |
| 页面关闭 / 切走 / 退出 | `ReleaseFileLocks()`（插件 `OnDeactivated` / `TryPrepareForAppClose` / `OnAppShutdown`） | — |
| 颜色标记写坏（`{` 未闭合） | 预览区显示可见提示，不崩；应用时按字面文本写入前给出一次确认 | 用户确认后 |

## 7. 工作切分与验证矩阵

| 阶段 | 内容 | 完成判据 |
|---|---|---|
| ~~W0 闸门~~ | CLI 命令 `probe-endgamemaps`（**已实现**，`Cli/Program.cs`，只读） | **已跑通：PASS**。① 表存在（含语言覆盖层自动探测）② `Columns[25].Type == "string"` ③ 语义无损 ④ 二次往返稳定 ⑤ 改一行不漂移 ⑥ A-5 样本统计。见 §2.1 |
| **W1 文字切片** | 新工程 + 插件注册 + 读取 + 规则模板 `{原名}` + 改前/改后预览 + 应用/还原 + 内置 18 条 + 规则持久化 + **C1/C2** | A1–A13 全绿；模块可独立发布。**验收时必测「无改动不写盘」** |
| **W2 颜色切片** | `WorkbenchPalette` + `ColorEditorWindow` 下沉 Ui（含 D16 分桶）+ dat 语法转换器 + uisettings 写入 + `DetectClientLanguage` 提 Shared | A14/A15/A16/A17 绿；`dotnet test -c Release -m:1` 无回归（基线 221） |

### 验证命令

```bash
export PATH="/usr/bin:/bin:$PATH"
dotnet build src/PoEToolbox.App -c Release -m:1          # 必须 -m:1（sourcelink 并发写）
dotnet test tests/PoEToolbox.Tests -c Release -m:1       # 基线 221 passed，只允许增加
dotnet publish src/PoEToolbox.App -c Release -m:1        # 发版前；CI 校验输出无 *.json
```

### 新增测试（测试工程已可引用插件，零新增引用边）

| 测试 | 覆盖 |
|---|---|
| `EndgameMarkRendererTests` | `{原名}` 替换、空模板、模板含标点/空格、多次占位符 |
| `EndgameMarkLocatorTests` | 列数不足 / 类型不符 / 正例定位到第 25 列 |
| `EndgameMarkMatcherTests` | 去括号前缀匹配、命中 0 / 1 / 多 → 三种 State |
| `AffixPatchBuilderTests`（新增用例） | `PatchId` 参数化后 `affix-workbench` 行为不变（防回归） |

## 8. 假设、风险与人工审查

| # | 内容 | 验证 / 处置 |
|---|---|---|
| A-1 | `Datc64File` 对 `EndgameMaps` 往返字节一致 | W0 `probe`；不过 ⇒ 改原地覆盖（备选已列） |
| A-2 | PoE2 客户端存在该表（含语言覆盖层）且第 25 列是目标文本 | W0 `probe` |
| A-5 | dat 颜色语法为 `<id>{…}` 单花括号 | W0 `probe`；不过 ⇒ **砍 W2** |
| A-6 | 颜色模块下沉不破坏词缀上色 | 下沉后人工开一次该页 + 跑既有测试 |
| R-1 | 整表替换会覆盖第三方对**同一张表**的改动 | 与词缀上色同口径（本来就是整文件替换）；应用前 `VerifyUnchanged` 会拦住「连接后被改」的情况 |
| R-2 | 游戏版本更新后列序漂移 | 类型校验会拒绝运行，功能停用而非写坏数据 |
| **人工审查点 1** | 写入游戏数据的最终结果 —— 应用后必须**启动游戏目视**（A9）才能发版 | 主人执行 |
| **人工审查点 2** | `uisettings.xml` 的颜色注册（W2）会影响全客户端界面配色，必须确认 id 不撞已有色 | 主人确认命名前缀 |
| **人工审查点 3** | 内置 18 条文案沿用（A-4） | 主人确认 |

### 未决（按 PRD 默认进入实现，有异议再回改）

Q-1 只写勾选项 ｜ Q-2 第一增量只保证繁中 ｜ Q-3 模块名暂用「挖坟词缀」 ｜ Q-5 预览支持同一行多色分段。
