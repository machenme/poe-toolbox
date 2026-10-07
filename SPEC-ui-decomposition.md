# UI 逻辑下沉 — 技术方案（Focused mini-spec）

- 日期：2026-09-24
- 代码基线：`6f580ffa7`（main，v0.2.5）
- 上游：[`PRD-ui-decomposition.md`](PRD-ui-decomposition.md)
- 模式：Focused mini-spec（跨模块行为 + 现有应用中的新组件；不是新子系统，无持久化/API 契约变更）
- **冲突以 `docs/ARCHITECTURE.md` 为准**

---

## 1. Technical scope

把 3 个超过 1000 行的 View 里的**领域逻辑**与**索引句柄所有权**搬到可单测的类型里，View 只留绑定与事件转发。

| 工作项 | 目标 | PRD 优先级 | 本次是否做 |
|---|---|---|---|
| W1 `AffixRampService` | 色阶（Ramp）生成、legacy 色值升级、`TierMapped` 映射 | P0 | ✅ 已完成（`046f030fc`） |
| W2 `AffixPreviewBuilder` | 预览行构建（`BuildCore` / `BuildFromPlainText` / `OverlayPerLine`） | P0 | 是 |
| W3 `AffixSchemeStore` | 配色方案的保存 / 删除 / 导入 / 导出 | P0 | 是 |
| W4 `IDialogService` | 收敛 59 处 `MessageBox.Show` | 贯穿 | 是 |
| W5 `DataBrowserService` | `GameDataAccess` 句柄所有权从 View 迁出 | P1 | 是（紧跟 W1~W4） |
| W6 `FxPatchView` 流程抽出 | 启停 / 创建补丁编排 | P2 | **否**，见 §7 Q3 |

不在本次范围：过滤与正则（`ApplyFilter` / `ApplyRegex`）留在 View——它操作的是 `ObservableCollection` 与 `ListBox.ItemsSource`，抽出去要引入新的筛选抽象，收益不抵成本。

---

## 2. Current architecture and affected seam

### 2.1 受影响的位置（实测）

| 文件 | 实测 | 说明 |
|---|---|---|
| `src/PoEToolbox.Plugins.AffixWorkbench/AffixWorkbenchView.xaml.cs` | 1388 行 / 76 方法 | 含 `CsdDocument`×4、`AffixDataService`×10、`FxEngineRunner`×5；已嵌套 5 个 VM 类（`EntryVm` / `LineVm` / `AffixSourceVm` / `AffixSourceGroupVm` / `TierPreviewVm`） |
| `src/PoEToolbox.Plugins.AffixWorkbench/AffixDataService.cs` | 694 行 / 30+ 公开成员 | **已承担**连接、补丁计算、预览、校验。见 §3.1 的关键判定 |
| `src/PoEToolbox.Plugins.DataBrowser/DataBrowserView.xaml.cs` | 1350 行 | `_gd` 字段持有 `GameDataAccess`（第 21 行），`OpenReadOnlyMapped` 在第 234 行，`Index` 引用 10 处 |
| `src/PoEToolbox.Ui/IUiPlugin.cs` | 14 行 | `UserControl CreateView()`，`Ui` 引用 WPF |
| `src/PoEToolbox.Abstractions/IPlugin.cs` | 15 行 | 纯契约，不含视图创建 |

### 2.2 最高价值的现有测试 seam

`tests/PoEToolbox.Tests` 已引用 **`PoEToolbox.Plugins.AffixWorkbench`** 与 **`PoEToolbox.Plugins.DataBrowser`**（实测 csproj）——W1~W5 抽出的类型**无需新增任何工程引用就能被测**。这是本次方案能立住的前提。

其余可用 seam：`ConfigService.DataDirectoryOverride`（路径注入根缝，已有 `ConfigPathTestCollection` 串行组）、`CapturedDebugFail`（断言 `Debug.Fail` 守卫）。

### 2.3 不可破的架构约束（`docs/ARCHITECTURE.md` §1）

- `Shared` 不能引用 `Core`
- 插件工程之间不得互引（有 `PluginReferenceGuardTests` 守）
- `Ui` 只引用 `Shared`
- 测试工程不引用 `App` / `Ui` / `Cli` → **`Ui` 里新增的任何东西自动化测试都碰不到**

---

## 3. Design decision

### 3.1 关键判定：不往 `AffixDataService` 里塞（推翻 PRD 假设 A1）

PRD 写「`AffixDataService` 是 P0 的天然承接点」。实测它已有 694 行 / 30+ 公开成员（`ConnectAsync`、`ComputeChanges`、`ComputePreview`、`EffectiveMatch`、`TierRangesOf`、`VerifyUnchanged`…），职责是**游戏数据访问 + 补丁计算**。色阶生成与方案存取是另一类职责，塞进去会造出第二个上帝类。

**决定**：新建三个专职类型，`AffixDataService` 一个字节不改。三者的共同特征是**输入确定、输出确定、不碰 WPF**——这正是可测性的来源。

### 3.2 三个新类型放哪

插件工程自己的 `Services/` 目录（`src/PoEToolbox.Plugins.AffixWorkbench/Services/`）。

- 不下沉 `Shared`/`Core`：无跨插件复用需求（色阶只服务词缀工作台）
- 不下沉的理由要写进注释，避免下一个人以为「漏了」

### 3.3 Q1 定案：`IDialogService` 落 `PoEToolbox.Ui`

| 选项 | 判定 |
|---|---|
| `Abstractions` | ❌ 该层立身之本是不引用 WPF，`IHotkeyService` 放那儿是因为**两个插件要复用同一实现**才做工厂倒置；对话框无此需求 |
| `Ui` | ✅ **选定**。它就是为 UI 关注点新建的（`OutputPanel` / `UiStatus` 已在此）；**全部 9 个插件都已引用 `Ui`**，零新增引用边 |

**不做工厂倒置**：对话框无状态、无生命周期、不需要独占，View 构造时 `IDialogService? dialogs = null` → `??= new MessageBoxDialogService()`，测试直接传 stub。

**已知代价**：`Ui` 不被测试工程引用 ⇒ `MessageBoxDialogService` 无自动化覆盖。可接受——它只包裹 `MessageBox.Show`，逻辑为空。

### 3.4 W5 的句柄所有权（本次唯一有资源风险的一项）

`DataBrowserView._gd` 目前由 View 持有，与**不变式 2**（引擎跑之前必须广播释放句柄，否则 `_.index.bin` 整文件替换会 `ERROR_USER_MAPPED_FILE`）正面相撞。

**决定**：`DataBrowserService` 持有 `GameDataAccess`，实现 `IDisposable` 与 `ReleaseFileLocks()`；View 只拿 `service.Snapshot`（文件树快照等纯数据），不碰句柄。

---

## 4. Data/control flow

### 4.1 W1~W3 抽出后的调用链

```
AffixWorkbenchView（只剩绑定与事件转发）
   ├─ AffixRampService.BuildRamp(tierCount, scheme) → IReadOnlyList<AffixColorDef>   [纯函数]
   ├─ AffixRampService.UpgradeLegacy(colors)         → 升级后的色值                    [纯函数]
   ├─ AffixPreviewBuilder.Build(doc, scheme)         → IReadOnlyList<IReadOnlyList<CsdTextSegment>>  [纯函数]
   └─ AffixSchemeStore.Save/Delete/Import/Export(scheme) → 结果 + 失败原因              [文件 IO]
                ↓（不变）
        AffixDataService（连接 / 补丁计算 / 应用）  ← 不动
                ↓
        FxEngineRunner（进程级互斥）
```

### 4.2 W5 抽出后的生命周期

```
DataBrowserView.Construct → new DataBrowserService()
   Open(path, ct)  → Task.Run(GameDataAccess.OpenReadOnlyMapped) → 缓存文件树快照
   Browse()        → 读快照（不再持有 Index 引用）
   ReleaseFileLocks() → 供 FxEngineRunner.ReleaseExternalLocks 广播调用
   Dispose()       → 释放 GameDataAccess（回收由 GameDataAccess.Dispose 自动排，不变式 3）
```

---

## 5. Interfaces and failure lifecycle

| 类型 | 签名要点 | 失败行为 |
|---|---|---|
| `AffixRampService` | `static bool IsTierId(string id)`<br>`static IReadOnlyList<AffixColorDef> BuildTierGradient(AffixColorDef? anchor, int count)`<br>`static bool TryUpgradeLegacy(IList<AffixColorDef> colors)`<br>`static bool EnsureDefaultTierRamp(IList<AffixColorDef> colors)`<br>`static AffixColorDef TierMapped(string tierId, string aliasId)` | 纯函数，无失败路径。`count < 1` → 抛 `ArgumentOutOfRangeException`；`TierMapped` 的档位不存在 → 抛 `ArgumentException`。**实施时按代码修正了草案签名**：原写的 `BuildRamp(int, AffixColorScheme)` 在真实代码里并不存在，实际是从 `RegenerateTierGradient` 里的线性插值抽出的 `BuildTierGradient(anchor, count)` |
| `AffixPreviewBuilder` | `static IReadOnlyList<IReadOnlyList<CsdTextSegment>> Build(CsdDocument doc, AffixColorScheme scheme)` | `doc` 为 null → 抛 `ArgumentNullException`；文档无匹配行 → 返回空集合（不抛） |
| `AffixSchemeStore` | `SaveResult Save(AffixColorScheme scheme)`<br>`ImportResult Import(string path)`（返回失败原因，不抛） | **导入失败绝不破坏现有方案**：先解析到临时对象，校验通过才替换。IO 异常 → 返回结果对象 + 记 `FileLogger`，**不吞** |
| `IDialogService`（`Ui`） | `bool Confirm(string message, string title)`<br>`void Info(string message, string title)`<br>`void Warn(string message, string title)` | 无失败路径 |
| `DataBrowserService` | `Task OpenAsync(string path, CancellationToken ct)`<br>`void ReleaseFileLocks()`<br>`void Dispose()` | 取消（`ct.IsCancellationRequested`）→ **立刻上抛**，不按超时降级（ARCHITECTURE §3.C 的要求）；打开失败 → 记日志 + 抛出，View 出中文提示；`Dispose` 中的释放异常 → 记日志不抛（进程退出竞态） |

**并发与有界性**：`OpenAsync` 沿用现有 `Task.Run` + `cts.Token`，不加新并发原语。文件树沿用现有 `cachedTree`，不引入新缓存层。

---

## 6. Verification plan

### 6.1 每一步之后

```bash
dotnet test tests/PoEToolbox.Tests -c Release -m:1
dotnet test tests/PoEToolbox.Tests -c Debug   -m:1   # Debug 腿守 #if DEBUG 断言
```

**门禁**：两条腿均 **221+ 通过 / 0 失败**，用例数只增不减。

**W1 实测**：Release 与 Debug 均 **238 通过 / 0 失败**（221 + 新增 17 条）。反向有效性自检：把 `IsLegacyDefaultGreen` 短路为 `false` → 红 2 条（正是依赖 legacy 判定的两个用例），还原后 17 条全绿——证明用例守的是逻辑而非代码形状。

### 6.2 新增用例（下限）

| 工作项 | 用例 | 断言什么 |
|---|---|---|
| W1 | ≥3 | 色阶生成的颜色数量与档位；legacy 升级后色值正确；`tierCount` 越界抛异常 |
| W2 | ≥3 | 有标签文档的行构建；无标签文档返回空；`null` 抛异常 |
| W3 | ≥3 | 保存→读取往返一致；导入非法文件返回失败且**原方案不变**；删除后列表少一项 |
| W5 | ≥2 | `Dispose` 后句柄计数归零（复用 `GameDataDisposalTests` 的既有断言方式）；取消时立刻抛而非降级 |

### 6.3 边界检查（grep，无需自动化）

| 检查 | 命令 | 期望 |
|---|---|---|
| AC-2 | `grep -nE "GameDataAccess\|Index\|CsdDocument" src/PoEToolbox.Plugins.AffixWorkbench/AffixWorkbenchView.xaml.cs` | 0 命中 |
| AC-3 | `grep -nE "GameDataAccess\|Index" src/PoEToolbox.Plugins.DataBrowser/DataBrowserView.xaml.cs` | 0 命中 |
| AC-6 | `grep -rc "MessageBox.Show" src --include=*.cs` | 插件 View 内 0 |

> 可选：把 AC-2/AC-3 固化成 `ViewPurityGuardTests`（与既有 `PluginReferenceGuardTests` 同类）。**与报告 P0-2 被否掉的那条不同**——那次是计数 `Open*`/`Reclaim` 配对（会被注释与分支干扰），这里是精确字符串存在性判定，无计数误报。是否加由主人定。

### 6.4 人工审查边界（自动化测不到，必须人做）

1. **每步提交后开一次程序**，走「选择游戏数据 → 选词缀 → 上色 → 应用 → 还原」全链，确认观感与行为不变
2. **W3 之后**：导入一个非法配色方案文件，确认提示正确且当前方案没被破坏
3. **W5 之后**：在真实客户端上跑一次「打补丁」——这是验证不变式 2 唯一可靠的方式（先例见 `docs/SPEC-engineering-hardening.md` §9.1）
4. `IDialogService` 替换 59 处后，**按插件逐个点一遍**所有会弹窗的按钮

---

## 7. Assumptions, risks, and human review

| # | 类型 | 内容 | 验证 / 处置 |
|---|---|---|---|
| A1' | 假设（**修正 PRD 的 A1**） | `AffixDataService` 边界不够放色阶与方案存取 → 新建三类型，不改它 | 已由 §2.1 实测支撑 |
| A3' | 假设（**修正 PRD 的 A3**） | 5 个嵌套 VM 类可直接搬出 | 抽出时逐个确认不依赖 WPF 类型；`EntryVm` 含 `CsdTextSegment` 引用，若不纯则留原位 |
| R1 | 风险 | 改动的是活跃代码（词缀上色是主功能），无自动化覆盖 UI 交互 | 每步一提交 + 人工目视；禁止把 W1~W5 合成一个大提交 |
| R2 | 风险 | `AffixSchemeStore` 涉及文件 IO，可能踩 `ConfigService` 路径注入缝的既有约定 | 挂在既有 `ConfigPathTestCollection` 串行组下（先例：`FxBuiltInPatchTests` 曾污染用户 AppData） |
| R3 | 风险 | W5 改句柄所有权，若漏掉 `ReleaseFileLocks` 广播会让打补丁失败 | §6.4 第 3 项强制人工验证 |
| Q3 | 开放（**PRD 保留**） | W6（`FxPatchView`）是否值得做 | 做完 W1~W5 后按剩余收益重估；允许直接砍掉 |
| — | 不变 | 工程边界、XAML 视觉、主题令牌、领域类型本身 **一个都不动** | PRD 的 Out of scope 全部继承 |

### 实施顺序

```
W1 色阶（纯函数，最易测，先立信心）
 └→ W2 预览构建（纯函数）
     └→ W3 方案存取（有 IO，需串行组）
         └→ W4 IDialogService（机械替换 59 处）
             └→ W5 DataBrowser 句柄（唯一有资源风险，放最后）
```

每一步独立提交、独立跑 §6.1 的两条腿。W5 之前必须完成 §6.4 第 1 项的目视确认。

```
State: DESIGN → VERIFY
Mode: Focused mini-spec
Evidence: AffixWorkbenchView.xaml.cs / AffixDataService.cs / DataBrowserView.xaml.cs / IUiPlugin.cs / IPlugin.cs / tests csproj 引用表
Passed: scope, affected seam, design, lifecycle, verification plan
Pending: Q3（W6 是否做）、§6.3 可选护栏是否加
Handoff: ready for implementation
```
