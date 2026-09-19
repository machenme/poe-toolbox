# PoE Toolbox 软件工程体检报告

- 日期：2026-09-19（v2，同日复核修正）
- 代码基线：`cb7bc4791`（main），版本 `version.json` = 0.2.3
- 实测：Release 全量测试 **176 通过 / 0 失败 / 3s**
- 范围：只读审查，未修改任何源码

> **有效期声明**：本报告中的**行号类证据会随代码漂移**。引用时请以提交 `cb7bc4791` 为基准；超过数次提交后请重新定位，不要直接采信行号。
> 报告是**冻结快照**，事后不逐条回改：其中标 ⚠️ 的问题有一部分已在后续提交里修掉（例如网络超时应统一为 `NetworkDefaults`、空 `catch` 的可诊断性、`Shared` 不再引用 WPF）。当前结构以 [`ARCHITECTURE.md`](ARCHITECTURE.md) 为准。
>
> **v2 修正**：① 技术栈 TFM 写错（Core/Cli 实际是 `net10.0-windows`）；② 4.1 测试分布表加总对不上（差 2）；③ **P0-2 证据行号全部过期**（原引 `FxPatchEngine.cs:353/480` 与 `FxDiff.cs:64/65` 均为 v0.2.3 之前的旧位置，现已重定位并区分「确认漏接 / 误报 / 待确认」）；④ 「零覆盖」措辞过强；⑤ 「零循环依赖」属平凡结论；⑥ 分支保护为未验证假设。

---

## 1. 执行摘要

**一句话判断：领域深度 A，工程底座 C+。** PoE2 Bundles2 二进制格式、可逆补丁引擎、基线防污染这套领域设计相当扎实，明显超出「个人小工具」的水准；但分层架构、测试布局、UI 组织还停留在「功能能跑就行」的阶段，已经开始对每个新功能收税。

| 维度 | 评分 | 核心依据 | 复核可信度 |
|---|---|---|---|
| 领域建模 | A- | 内容判据、逻辑地址稳定性、还原语义分离，想得透且写进了 docs | 高 |
| 发布工程 | B+ | version.json 单一源、tag 一致性校验、publish 无 loose json 护栏 | 高 |
| 可测试性 | B | 176 用例 3s 全绿，但分布严重失衡 | 高 |
| 分层架构 | C | Shared 上帝程序集（7332 行）+ Sdk 空壳（53 行） | 高 |
| UI 架构 | C- | 12 个 View 后台代码 7477 行，基本无 MVVM | 高 |
| 文档 | C | 7 篇专题文档，零架构总览 | 高 |
| 并发与健壮性 | B | 无 Thread.Sleep、全局异常兜底已接；但存在无限超时网络调用 | 中 |

**最该先动的三件事**

1. `FxPatchEngine.cs`（1783 行 / 80 方法，方法数为 grep 近似值）拆分 —— 所有补丁功能都堵在这个文件上
2. 内存回收从「人肉约定」改成「作用域保证」—— 已确认至少 1 处漏接（见 P0-2）
3. 补三条引擎级性质测试（apply→revert 恒等、重复 apply 无副作用、多补丁任意序 revert）

---

## 2. 项目画像

| 指标 | 数值 |
|---|---|
| 源码 | 181 个 `.cs`（不含 obj/bin）、33 928 行、21 个 `.xaml` |
| 测试 | 1 个测试项目、20 个测试文件、4 060 行、171 个 `[Fact]/[Theory]`（理论用例展开后共 176 个执行用例）、589 处断言 |
| 项目数 | 19 个 csproj：18 个 `src/` 项目（4 个基础库 + Shared/Core/Sdk + App/Cli + 9 个插件）+ 1 个测试项目 |
| NuGet 依赖 | 2 个：`CommunityToolkit.Mvvm` 8.4.0、`aianlinb.SystemExtensions` 1.8.0 |
| 目标框架 | **除 4 个基础库为 `net10.0` 外，其余全部 `net10.0-windows`**（含 Core 与 CLI） |
| Git | 20 个提交、单分支 `main`、无 PR 流程 |
| 构建发布 | GitHub Actions 单 workflow（build + test + publish + release），单文件 exe |

### 各项目规模

| 项目 | 文件 | 行数 | 说明 |
|---|---:|---:|---|
| PoEToolbox.Shared | 31 | 7 332 | 引擎与服务全部集中于此（**最大风险点**） |
| Plugins.DataBrowser | 17 | 3 729 | 数据浏览 + 地图编号 |
| PoEToolbox.Core | 13 | 3 009 | datc64、翻译、打标、DDS 渲染 |
| Plugins.AffixWorkbench | 6 | 2 630 | 词缀上色工作台 |
| Plugins.BagCleaner | 26 | 2 519 | 背包整理（唯一有 ViewModel 的模块） |
| LibBundle3 | 12 | 2 663 | Bundles2 索引（第三方改造） |
| LibGGPK3 | 11 | 2 578 | Content.ggpk（第三方改造） |
| LibDat2 | 25 | 2 101 | dat 表解析（第三方改造） |
| Plugins.FxPatch | 4 | 1 262 | 特效补丁 UI |
| PoEToolbox.Cli | 4 | 1 186 | 命令行 |
| PoEToolbox.App | 5 | 1 139 | WPF 宿主 |
| Plugins.Voyager | 8 | 1 064 | 暂未注册（隐藏） |
| Plugins.PriceTagger | 2 | 1 044 | 物价打标 |
| Plugins.Poe2Font | 3 | 614 | 字体 |
| Plugins.PoeCnPatch | 4 | 558 | 汉化补丁 |
| Plugins.TermTranslator | 3 | 289 | 术语翻译 |
| LibBundledGGPK3 | 3 | 158 | 统一入口 |
| PoEToolbox.Sdk | 4 | 53 | 仅 4 个接口 |

---

## 3. 架构评估

### 3.1 分层与依赖

四层结构（入口 → 插件 → 领域 → 基础库）。依赖方向单向、无反向边——不过需要说明：**csproj 层的循环依赖本就无法编译**，所以「无循环」是平凡结论，真正的价值在于确认没有意外的反向引用（已逐一核对，未发现）。

```
App (WPF exe)        Cli (控制台)
        │                 │
        └────────┬────────┘
                 ▼
      插件层（9 个项目 / 10 个 IPlugin 实例）
                 ▼
    Shared (7332)   Core (3009)   Sdk (53)
                 ▼
  LibBundle3 / LibGGPK3 / LibBundledGGPK3 / LibDat2
```

直接依赖矩阵（实测自各 `.csproj`）：

| 项目 | 依赖 |
|---|---|
| App | Shared + 9 个插件项目 |
| Cli | Core + LibDat2（经 Core 传递拿到 Shared） |
| Shared | Sdk + LibBundledGGPK3 + LibDat2 |
| Core | LibBundledGGPK3 + Shared |
| 插件 ×9 | Shared（全部）、Core（3 个）、Sdk（**仅 5 个**）、LibDat2（2 个）、LibBundledGGPK3（1 个） |

### 3.2 问题 A：Sdk 是空壳，「插件」名不副实

- `PoEToolbox.Sdk` 只有 4 个文件 53 行：`IPlugin` / `IEventBus` / `ILogger` / `IConfigService`。
- `PluginManager.RegisterAll()`（`PluginManager.cs:25-38`）硬编码 `new` 出 10 个实例，**Voyager 被注释掉临时隐藏**（`:37`）。
- 9 个插件里只有 5 个引用 Sdk —— 契约不统一，另外 4 个直接吃 Shared 的具体类型。
- `Shared.csproj` 有 5 条 `InternalsVisibleTo`：`PoEToolbox.Tests`、`PoEToolbox.Plugins.FxPatch`、`PoEToolbox.Plugins.AffixWorkbench`、`PoEToolbox.App`、`PoEToolbox`（exe 程序集名）。内部成员向消费者开放，说明边界是靠「友元」而非接口维持的。

> 结论：这不是插件架构，是**用接口解耦的 monolith**。好处是有可测试性，代价是每次加功能都要改 App 的 `ProjectReference` 和注册代码，且「插件可独立演进」的预期是假的。

### 3.3 问题 B：Shared 是上帝程序集，且混入 UI 关注点

Shared 7332 行里同时躺着：

- 领域引擎：`FxPatchEngine.cs`（1783 行）、`CsdDocument.cs`（1028 行）、`FxDiff.cs`、`AffixPatchBuilder.cs`
- 数据访问与内存：`GameDataAccess.cs`（539 行）、`GameDataLoader.cs`、`MemoryReclaimer.cs`
- 基础设施：`ConfigService`、`FileLogger`、`EventBus`、`IndexBackupService`、`PoeDetector`
- **UI 关注点**：`ThemeManager.cs`、`OutputPanel.xaml(.cs)`、`UiStatus.cs`、`UILabels.cs`

关键在于**领域与 UI 混在同一个程序集里，粒度太粗**：任何只想复用 `ConfigService` 或 `IndexBackupService` 的消费者，都被迫带上 WPF 与主题资源。CLI 虽然自己就是 `net10.0-windows`（不是 TFM 不匹配的问题），但它不需要 `ThemeManager` 与 `OutputPanel`。

### 3.4 问题 C：插件间直接依赖

`Plugins.Voyager` → `Plugins.BagCleaner`（`Voyager.csproj` 直接引用另一个插件项目）。插件之间互相依赖，绕过任何抽象层，BagCleaner 的内部改动会直接波及 Voyager。

### 3.5 亮点（务必保留）

| 设计决策 | 为什么好 |
|---|---|
| **改文件唯一手段是 `Redirect`**（`FileRecord.cs:139-149`），从不写原生 bundle | 原版数据天然保留，回滚有物理保证 |
| **状态判定基于内容**（`ComputeState`） | 天然支持补丁栈式叠加；不依赖会漂移的物理位置 |
| **逻辑地址稳定、物理位置会漂** | 判据落在逻辑内容上是正确抽象 |
| **两种还原语义分离**：恢复原版（基线索引回写）vs 回到上次改动前（内容快照写回） | 语义明确，不会误删第三方改动 |
| **基线污染三道防线**（v0.2.1） | `Begin()` 拒绝含 PATCHED 引用的索引、`RefreshBaselineIfStale` 核对、`RepairIfBroken` 拒绝假修复 |
| 对比第三方改动**比内容不比位置** | `record.Read().Span.SequenceEqual`，正确 |

这几条是整个工具可信度的地基，任何重构都不要动它们。

---

## 4. 测试评估

### 4.1 分布（实测）

| 测试文件 | 用例数 | 定位 |
|---|---:|---|
| `CsdDocumentTests` | 39 | 充分，含字节级往返一致、UTF-8 无 BOM、二次上色幂等 |
| `GameDataLoaderTests` | 26 | 充分 |
| `UISettingsDocTests` | 15 | 充分 |
| `LibBundle3AddFileTests` | 13 | 中等 |
| `AffixColorSchemeTests` | 11 | 中等 |
| `MapNumberRestoreTests` | 10 | 中等 |
| `FxRawPackPatchTests` | 10 | 中等 |
| `FxPatchBundleNamingTests` | 9 | 外围（命名规则） |
| `FxPatchStateStoreTests` | 6 | 账本 |
| `MemoryReclaimerTests` | 5 | 有 |
| `AffixOfficialRestoreTests` | 5 | 有 |
| `FxDiffPackagingTests` | 4 | 外围 |
| `PatchBundleRepairTests` | 3 | 有 |
| `FxPatchSecurityTests` | 3 | 有（路径穿越） |
| `AffixPatchBuilderTests` | 3 | **含唯一 1 个引擎全周期用例**（`:83`） |
| 其余 5 个文件 | 9 | 冒烟级 |
| **合计** | **171** | 展开理论用例后共 176 个执行用例 |

**核心矛盾**：`FxPatchEngine.cs` 1783 行是整个项目正确性与安全性的命门，但针对它的测试大多是外围（命名、打包、安全、账本），**状态机全周期只有 1 个用例**。

### 4.2 无直接测试的区域

| 区域 | 行数 | 现状 |
|---|---:|---|
| `LibDat2` | 2 101 | 无直接测试（经 DataBrowser 间接执行部分路径） |
| `LibGGPK3` | 2 578 | 无直接测试（经 LibBundledGGPK3 间接执行部分路径） |
| `App` | 1 139 | 无测试 |
| `Cli` | 1 186 | 无测试，**fx-patch 命令解析与退出码完全未被覆盖** |
| 插件 Poe2Font / PriceTagger / TermTranslator / Voyager | 3 011 | 无测试（测试项目引用了 PoeCnPatch 与 BagCleaner，但这两个项目下无测试代码） |

### 4.3 建议补的三条性质测试

```
1. apply → revert → 目标文件字节与初始完全一致（对每个 op 类型各来一遍）
2. apply → apply → 第二次无副作用，状态仍为 Applied
3. P1 → P2 → P3 叠加后，按任意顺序 revert，最终内容恒等
```

性质测试的价值在于：它覆盖的是**不变式**而不是「某个补丁的行为」，新增 op 类型时会自动生效。

---

## 5. 代码质量实测

| 指标 | 数值 | 评价 |
|---|---:|---|
| `TODO` / `FIXME` / `HACK` / `XXX` | 3 | 优（且全在第三方 LibBundle3 代码内） |
| `Thread.Sleep` | 0 | 优 |
| `.Wait()` / `.Result` | 1 | 优 |
| `async void` | 31 | 可接受（全部在 View 事件处理器，非业务逻辑） |
| 空 `catch {}` | 18 | 需治理（集中在 FileLogger、PoeDetector、FxPatchEngine 清理路径） |
| `MessageBox.Show` | 59 | 逻辑与 UI 强耦合，阻塞可测试性 |
| `InfiniteTimeSpan` 网络超时 | 2 | 差（见 P1-3） |

### 5.1 UI 层：基本没有 MVVM

匹配 `*View.xaml.cs` 与 `MainWindow.xaml.cs` 的 12 个文件：

```
AffixWorkbenchView.xaml.cs    1382
DataBrowserView.xaml.cs       1343
FxPatchView.xaml.cs           1017
PriceTaggerView.xaml.cs        971
MainWindow.xaml.cs             717
MapNumberView.xaml.cs          507
其余 6 个 View                1540
────────────────────────────────
合计                          7477
```

需要说明：21 个 `.xaml` 中还有 `MapNumberSettingsWindow`、`ColorEditorWindow`、`DisclaimerPage`、`OutputPanel` 等窗口/控件未计入，**UI 层代码总量实际高于 7477 行**。

`CommunityToolkit.Mvvm` 8.4.0 只被 **BagCleaner 一个项目**引用。配色方案、补丁状态机这些领域逻辑埋在事件处理器里 ⇒ 无法单测、改一处怕三处。

### 5.2 并发与健壮性

- ✅ `App.xaml.cs:15` 已挂 `DispatcherUnhandledException`
- ⚠️ 按 `UnobservedTaskException` / `AppDomain.UnhandledException` / `FirstChanceException` 四个关键词检索 `src/` 均无命中 —— `async void` 与 fire-and-forget Task 的异常会静默丢失
- ⚠️ `DatContainer.cs:37` 与 `PatchClient.cs:271` 使用 `Timeout.InfiniteTimeSpan` —— 网络抖动时 UI 永久挂起，用户只能杀进程
- ✅ `UpdateChecker.cs:96` 有 5s 超时 + bounded read（做得对，可作为范式推广）

### 5.3 依赖风险

`aianlinb.SystemExtensions` 1.8.0 的 `Array.RemoveAt/Insert/Add` 是**函数式**的（返回新数组、不改原数组）。当 void 用能编译通过但静默失效——本项目已经踩过一次（`Index.Save` 孤儿清理）。这是「每个人都会踩一次」的第三方 API 陷阱，建议封一层自己的扩展方法或直接替换。

---

## 6. 风险清单

### P0 — 不改会持续对每个新功能收税

| # | 问题 | 证据 | 影响 | 方案 |
|---|---|---|---|---|
| P0-1 | `FxPatchEngine.cs` 上帝文件 | 1783 行（全项目最大）、约 80 个方法、无 region 分区；同时承担补丁解析 / apply-revert / 状态判定 / 整包 rawpack / 内置补丁释放 / purge | 补丁引擎的任何改动都在一个巨型文件里做，冲突与回归风险高 | 拆为 `FxPatchEngine`(编排) / `FxPatchOperations`(4 类 op) / `FxPatchState`(判定) / `FxBuiltInPatches`(内嵌释放)。**先补测试再拆** |
| P0-2 | 内存回收靠人肉约定 | 见下方逐点核查表 | 结构性缺陷：新增 `Open*` 调用点容易漏回收 | 见下方方案修正 |

#### P0-2 逐点核查（v2 重做）

原报告引用的 `FxPatchEngine.cs:353/480`、`FxDiff.cs:64/65` **已随 v0.2.3 的改动漂移，全部失效**。重做后的实测（`Open*` 计数 vs `MemoryReclaimer.Reclaim` 计数，逐文件）：

| 文件 | Open | Reclaim | 判定 |
|---|---:|---:|---|
| `Shared/FxPatchEngine.cs` | 5 | 4 | **确认漏 1 处**：`:1141` 的 Pass 1 只读预检 `using (var gd = ...)` 块无对应回收（回收点分布在 `:1029 / :1094 / :1123 / :1284`） |
| `Shared/FxDiff.cs` | 2 | 1 | **无漏**：`:104` 一处回收覆盖 `:75-76` 两个流。原报告判定错误 |
| `Plugins.AffixWorkbench/AffixDataService.cs` | 1 | 0 | **已确认为有意**（`07fdfa49c` 加注）：`ConnectGameData` 打开的索引常驻到下次 Connect 或 `Dispose`，因为列表要持续按行读 csd/uisettings，每次访问重开索引不现实；重连前与 `Dispose` 时都会释放。不是漏回收 |
| `Shared/GameDataLoader.cs` | 4 | 2 | **误报**：4 个命中里含 2 处 XML 文档注释与 `switch` 分支，属计数噪声 |
| 其余 6 个文件 | 各 1~2 | 配平 | 正常 |

**方案修正**：原报告提议「加一条源码扫描测试，断言所有 `Open*` 都包在 `using` 内」——**这个方案不可靠**，上面的核查已经证明朴素计数会产生误报（GameDataLoader）与漏判（回收与打开不在同一文件/同一方法时）。改为：

1. 在 `GameDataAccess` 的构造与 `Dispose` 里维护一个静态「未关闭实例计数」；
2. 测试断言：跑完任一操作后计数归零、且每个关闭的实例都有对应回收请求；
3. 辅以一处 `FxPatchEngine.cs:1141` 的定点补漏。

这样测的是**运行时行为**而不是源码文本，不受格式与注释干扰。

### P1 — 影响可维护性与回归信心

| # | 问题 | 证据 | 方案 |
|---|---|---|---|
| P1-1 | UI 无 MVVM | 12 个 View 后台代码 7477 行（实际更高）；MVVM 仅 BagCleaner | 新模块强制 ViewModel；老模块随改动渐进迁移，不搞大爆炸重写 |
| P1-2 | 引擎级测试薄弱 | 全周期仅 `AffixPatchBuilderTests.cs:83` | 补 3 条性质测试（见 4.3） |
| P1-3 | 无限超时网络调用 | `DatContainer.cs:37`、`PatchClient.cs:271` | 统一超时策略，参照 `UpdateChecker.cs:96` |
| P1-4 | Sdk 空壳误导设计 | 53 行 / 4 接口；注册硬编码 `PluginManager.cs:25-38` | 二选一：补全契约（版本/能力/生命周期），或改名为 `Abstractions` 承认它是接口隔离层 |
| P1-5 | 插件间直接依赖 | `Voyager.csproj` → `BagCleaner.csproj` | 提取公共部分到 Core/Shared，或用事件总线解耦 |
| P1-6 | Shared 混入 UI 关注点 | `ThemeManager.cs`、`OutputPanel.xaml`、`UiStatus.cs`；粒度太粗，任何消费者都带上 WPF | 把 UI 关注点移到 App 或新建 `PoEToolbox.Ui` |
| P1-7 | 未挂 `UnobservedTaskException` | `src/` 无命中 | `App.xaml.cs` 补挂，落 FileLogger |

### P2 — 顺手可做

| # | 问题 | 方案 |
|---|---|---|
| P2-1 | `build.bat` 缺 `-m:1` | 本地出包偶发 MC1000（`Directory.Build.props` 已关共享编译，但并行仍会撞 obj 锁）。补上 |
| P2-2 | 无架构总览文档 | 补 `docs/ARCHITECTURE.md`：分层图 + 数据流 + 三条不变式 |
| P2-3 | 中文文案硬编码 | 无 resx，将来多语言要重写。短期不动，新文案注意集中 |
| P2-4 | 无分支保护 | **未验证**（本地无法读取 GitHub 分支保护设置，属假设）。若确实没有，CI 已跑测试，可开启「CI 绿才允许合并」 |
| P2-5 | 18 处空 `catch {}` | 至少加 `FileLogger.Write`，否则线上问题无法诊断 |
| P2-6 | 59 处 `MessageBox.Show` | 抽成 `IDialogService`，长期收益是可测试 |
| P2-7 | `docs/BUILT-IN-FX-PATCHES.txt` 未入库且与 .md 重复 | 删除 .txt |
| P2-8 | Debug 下 `DisposeWithoutSave_*` 用例必挂 | `Index.Dispose` 的 `Debug.Fail`（`Index.cs:1329`，行号来自项目记忆未复核）。要么修断言，要么给用例加 Debug 跳过条件 |

### 6.5 处置状态（2026-09-20 追加，报告正文的判定不随此改动重写）

| # | 状态 | 说明 |
|---|---|---|
| P0-1 | ✅ | `FxPatchEngine.cs` 1754 → 211 行，逻辑分散到 `Shared/Fx/` 九个模块（提交 `fc8a84441`） |
| P0-2 | ✅ | 回收下沉到 `GameDataAccess.Dispose`（`ddcedd5be`）。**本报告提议的「源码扫描测试」被实测否掉**——朴素计数既误报又漏判，改测运行时行为 |
| P1-1 / P1-2 | 部分 | P1-2 补成 9 条性质测试（非 3 条）；P1-1 MVVM 明确排除在本轮外，见 `docs/SPEC-engineering-hardening.md` §11 |
| P1-3 | ✅ | `NetworkDefaults` 统一超时 + 调用点降级与中文失败原因（`2327ae8c9`、`12bd84929`） |
| P1-4 | ✅ | 选改名 `Abstractions`（`574404bd4`），同时拆出 `IUiPlugin`（`2463179fe`） |
| P1-5 | ✅ **判定有误，但选型已做完并落地** | 不是误引：Voyager 有 8 处 `using PoEToolbox.Plugins.BagCleaner.*`，是真依赖，而且**本报告只给了行号级证据**（`Voyager.csproj` → `BagCleaner.csproj`），读起来像删一行引用就能了。沉到哪一层实测过三个选项，最终：纯 Win32/GDI 的 `Input/` + `ScreenGrid/` 下沉 `Core`（`e21cd1059`），WPF 的 `HotkeyService` 留在 BagCleaner、契约 `IHotkeyService`/`IHotkeyServiceFactory` 提到 Abstractions 倒置注入；`Voyager → BagCleaner` 工程引用已删 |
| P1-6 | ✅ | 新建 `PoEToolbox.Ui`（`c8cabcbd4`）、`ThemeManager` 移入 App（`69645d931`），`Shared` 不再引用 WPF |
| P1-7 | ✅ | App + Cli 均挂兜底（`f154c507f`） |
| P2-1 | ✅ | `build.bat` 补 `-m:1`（`d6b3d46ba`） |
| P2-2 | ✅ | `docs/ARCHITECTURE.md`（`9398f1b20`）。**本报告「三条不变式」太少，实际已立到 13 条** |
| P2-3 | 未动 | 报告自己的方案就是「短期不动」，本轮也未纳入 SPEC（§11 里没有这一项）。「新文案注意集中」这条无人执行，要靠本人日后自觉 |
| P2-4 | 未验证 | 仍需本人在 GitHub 侧确认 |
| P2-5 | ✅ **口径修正** | 不是 18 处：全仓 29 处，`src/` 下 19 处，逐处判定后补日志 4 处、写明有意吞掉 15 处。「全部加 FileLogger」会让日志在正常使用下被刷爆（`9290f15a9`） |
| P2-6 | 不动 | 与 MVVM 同批，SPEC §11 排除 |
| P2-7 | ✅ | `.txt` 已删（`62816cf26`）。补充事实：它是 `.md` 的严格子集，且 `.md` 之后又更正过两轮，双份必然再分叉 |
| P2-8 | ✅ **两个原方案都没采纳** | 既没改 `Index` 也没给用例加 Debug 跳过：测试期间接管 `Trace.Listeners`，把守卫本身断言下来（`587f6a96a`）。Debug 首次 195 全绿，且 `Debug.Fail` 从此有回归保护。CI 已加 Debug 测试腿，这条 `#if DEBUG` 断言现在真的会在 CI 上跑 |


---

## 7. 发布工程：做得好的部分

这部分明显优于代码结构，值得明确记录下来，别在重构时弄丢：

1. **版本单一源**：根 `version.json`；csproj 在未传 `/p:Version` 时自动读取（`PoEToolbox.App.csproj:8-11`），CI 与 `build.bat` 显式传参
2. **tag 一致性校验**：`.github/workflows/build.yml:45-52`，tag 与 version.json 不符直接失败
3. **publish 无 loose json 护栏**：`build.yml:80-85`。这是 v0.2.2 事故（发布包只带 exe，内置补丁描述没跟着走）的直接产物
4. **运行时数据全部内嵌**：补丁描述（`Shared.csproj` 的 `Patches\**\*.patch.json`）、`oo2core.dll`、`DatDefinitions.json`（LibDat2 内嵌）
5. **发布只带单个 exe**：`PublishSingleFile` + `ReadyToRun`

已知缺口：`build.bat` 缺 `-m:1`（P2-1）。

---

## 8. 整改路线图

| 阶段 | 内容 | 目标 |
|---|---|---|
| 第 1 步 | P0-2（`:1141` 定点补漏 + 开合计数断言）、P1-3（超时）、P1-7（异常兜底）、P2-1（build.bat） | 半天内完成，零业务风险 |
| 第 2 步 | P1-2（三条性质测试） | 给后续所有重构装上安全网 |
| 第 3 步 | P0-1（拆 FxPatchEngine）+ P1-6（Shared 去 UI） | 跟下一次补丁功能一起做，不要单独开重构窗口 |
| 第 4 步 | P1-1（MVVM）随新模块渐进；P1-4（Sdk 定位）先做决策 | 长期 |
| 第 5 步 | P2 项随时夹带 | — |

**原则**：第 2 步（测试）必须早于第 3 步（重构）。先有网再拆引擎。

---

## 附录 A：测量命令

```bash
# 规模
find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -exec cat {} + | wc -l
find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -exec wc -l {} + | sort -rn | head -18

# 测试
dotnet test tests/PoEToolbox.Tests -c Release          # 176 passed / 3s
grep -c -E "^\s*\[(Fact|Theory)\]" tests/PoEToolbox.Tests/*.cs   # 逐文件用例数

# 质量信号
grep -rn --include=*.cs -E "TODO|FIXME|HACK|XXX" src      # 3
grep -rn --include=*.cs "async void" src                  # 31
grep -rn --include=*.cs -E "catch\s*(\([^)]*\))?\s*\{\s*\}" src   # 18
grep -rn --include=*.cs "Thread.Sleep" src                # 0
grep -rn --include=*.cs -E "\.Wait\(\)|\.Result\b" src    # 1
grep -rn --include=*.cs "InfiniteTimeSpan" src            # 2

# 内存回收配对（P0-2 核查用；注意：计数法会因注释与分支产生噪声）
for f in $(grep -rl -E "OpenReadOnlyMapped|GameDataAccess\.Open" src --include=*.cs | grep -v '/obj/'); do
  echo "$(grep -c -E 'OpenReadOnlyMapped|GameDataAccess\.Open' $f) / $(grep -c 'MemoryReclaimer\.Reclaim' $f)  $f"
done
```

## 附录 B：本报告未覆盖 / 未验证的部分

- **未做运行时性能剖析**（3.1M 索引常驻 ~1.1 GB、回收后 ~177 MB 的数据来自以往手工测量，未进代码）
- **未逐个审阅** `LibGGPK3` / `LibDat2` / `LibBundle3` 的第三方改造正确性
- **未审查** `PriceTagger` 的外部 API 契约（poe.ninja）与失败降级路径
- **未评估** `TermTranslator` / `EdgeTranslationClient` 的网络异常处理
- **未做依赖漏洞扫描**（仅 2 个 NuGet 包，风险面小）
- **P2-4 分支保护为假设**，未实际验证 GitHub 仓库设置
- **P2-8 的 `Index.cs:1329` 行号来自项目记忆，未复核**
- **「结构性缺陷必然再漏」属合理推断而非证明**：本次核查只确认 1 处漏接 + 1 处待确认，样本不足以量化漏接率
