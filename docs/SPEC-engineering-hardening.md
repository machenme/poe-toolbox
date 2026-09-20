# SPEC：工程加固技术方案

> 本文是**历史计划**：记的是「为什么这样拆、当时怎么决定、每一步实测到什么」。当前状态（谁能引用谁、哪几条不变式）以 `docs/ARCHITECTURE.md` 为准，两者冲突时以它为准。
>
> 配套文档：`docs/PRD-engineering-hardening.md`（需求与验收，含 D1~D5 拍板表）、`docs/REVIEW-software-engineering.md`（体检报告，证据来源）、`docs/REVIEW-engineering-hardening-before-after.md`（本轮改动前后逐面对照）、`CHANGELOG.md`（对外变更记录）
>
> 本文不含机器相关信息：游戏客户端绝对路径、账号、机器名一律不写。

| 项 | 内容 |
|---|---|
| 文档版本 | v3（2026-09-20 收尾回写：写路径实测 §9.1、D3/D5/P1-5 关闭） |
| 日期 | 2026-09-19 立项 / 2026-09-20 收尾 |
| 基线 | `cb7bc4791`，`version.json` = 0.2.3 |
| 前置阅读 | PRD §4 分批、§6 验收标准、§7 约束 |

**阅读指引**：§1~§8 按 W1~W8 各一节（§2 引擎拆分、§3 性质测试、§7/§8 是分层的两半）。批次归属看 §10 的路线图：B1 = W1/W4/W5/W6，B2 = W3，B3 = W2 + W7a，B4 = W7b + W8。§9 是验证矩阵，§10.5 是实际提交清单，§11~§12 是排除项与遗留待办。

> **行号使用说明**：本文所有行号以基线 `cb7bc4791` 为准。**实现时若行号对不上，以「类型名 / 方法名 / 唯一字符串」为准，不要硬套行号**——本项目的行号已在一次提交内漂移过一次。

## 0. 实施状态（v2，2026-09-20 回写）

| 项 | 状态 | 说明 |
|---|---|---|
| W1 回收下沉 | ✅ 完成 | 见 §1；删除 15 处手工调用点 |
| W3 性质测试 | ✅ 完成 | 9 条（原计划 3 条；S7 补 P-4），见 §3 |
| W4 超时 | ✅ 完成 | 超时值统一 + §4.3 调用点降级与可读提示已落地（见 §4.3） |
| W5 异常兜底 | ✅ 完成 | App + Cli |
| W6 build.bat | ✅ 完成 | `-m:1` |
| W2 引擎拆分 | ✅ 完成 | S1~S8 全部落地：`FxPatchEngine.cs` **1754 → 211 行**（只剩入口门面 + 日志出口），逻辑分散到 `Shared/Fx/` 九个模块。见 §2.1.1 |
| W7a ThemeManager 搬家 | ✅ 完成 | 见 §7.2；~~剩一次人工视觉确认（light/dark/跟随系统）~~ → 该视觉确认**已按 PRD D6 移出验收范围**（主人判定性价比低），搬家本身已完成 |
| W7b `PoEToolbox.Ui` | ✅ 完成 | 见 §7.2；`OutputPanel`/`UiStatus`/`FxEngineRunner` 已移出 Shared，Shared 不再引用 WPF |
| W8 Sdk 定位 | ✅ 完成（PRD D2 选 b2） | `Sdk` → `Abstractions`；`IPlugin` 去掉 `CreateView()`，界面插件改实现 `PoEToolbox.Ui.IUiPlugin`，Abstractions 不再引用 WPF。见 §8 |

**实测结果**：Release **195 通过 / 0 失败**（基线 176 → S1 后 185 → 补 ptr 用例 188 → S7 补 P-4 后 189 → §4.3 网络提示 3 条后 192 → C14 补 config 分支 3 条后 **195**；S3~**S8**、W7b/W8 每步复测均绿）；**Debug 也 195 全绿**（C21 关掉了那条长期挂着的 `DisposeWithoutSave_*`，见 §12 的 D4 一行——本项目 Debug 首次全绿）。**HEAD `060fb453b`（C28，PRD D3 / D5 / P1-5 三项全部落地之后）中两个配置各复测一次，均 195 绿**（Release 34s / Debug 34s）；`dotnet publish -c Release -m:1` 出包成功且 publish 无 loose json（重测：单 `PoEToolbox.exe` 6,294,804 字节，`PoEToolbox.Ui.dll` / `Abstractions` / `Shared` / `Core` 均以单文件形式包在 exe 内，发布目录只剩 pdb/xml）。

> **CI 原本只跑 Release**（`.github/workflows` 里 `--configuration Release`），Debug 全绿只有本地跑得到，`#if DEBUG` 里的断言在 CI 上等于不执行。**PRD D5 已拍板并落地**（`7e07f81c5`）：CI 现在是 Release + Debug 两条测试腿，多花一次 34~36s。

**本轮修正的四处 SPEC 错误**（实施时发现原方案不可行或不准）：

1. §4 —— `NetworkDefaults` **不能**用在 `LibDat2` / `LibGGPK3`（这两个库不引用 Shared）
2. §1.5 —— 删除 `finally { Reclaim(); }` 会留下无 catch/finally 的 `try`，编译不过（CS1524）
3. §2.1 —— `PatchDef` / `PatchOp` / `PatchState` 是 `FxPatchEngine` 的**嵌套类型**，不是命名空间级类型
4. §7 —— Shared 去掉 `UseWPF` 后 `Core` 编译失败（CS1069 `System.Drawing.Bitmap`）。原因：`Core` 的 `<UseSystemDrawing>true</UseSystemDrawing>` 在 .NET 10 SDK（10.0.400）上是**空转属性**，它一直能编译是靠 `Shared` 的 `UseWPF` 把 `Microsoft.WindowsDesktop.App` 框架引用传递过来。已改为显式 `<FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />`（`System.Drawing.Common` 属于桌面框架包的 WindowsForms 分组）。原 SPEC 把 W7b 判成「纯搬家、零行为影响」，低估了这一层隐含依赖

---

## 1. W1：内存回收下沉到 Dispose

### 1.1 现状

| 事实 | 位置 |
|---|---|
| `GameDataAccess` 已维护静态 `_openInstanceCount` / `_openSequence`，并有 `HasOpenLocks`、`LocksChanged`、`CreateAbortCheck()` | `GameDataAccess.cs:18-51` |
| `CreateAbortCheck()` 返回 `() => HasOpenLocks && _openSequence == 捕获时值` —— 「还有别人持锁」或「期间有人新开」就不回收 | `GameDataAccess.cs:47-51` |
| `Dispose()` 已做原子幂等（`Interlocked.Exchange(ref _disposed, 1)`）并在计数归零时触发 `LocksChanged(false)` | `GameDataAccess.cs:522-538` |
| `MemoryReclaimer.Reclaim` 本身是后台任务 + 合并（coalescing），不阻塞调用方；有内部计数 `ChainStarts` / `Passes` | `MemoryReclaimer.cs:77-98`、`:55-59` |
| 回收必须**手工**在 `using` 块外调用 | 11 处，散落 8 个文件 |

| 已确认的漏接 | ✅ 已修。`FxPatchEngine` Pass 1 只读预检块，`using (var gd = ...)` 内部在冲突时 `return 1;` 提前退出，块外的手工回收被跳过。下沉后这条路径自动覆盖 |
| 额外收益 | `AffixDataService._gd?.Dispose()`（连接态断开时）**原先也不回收**——下沉后自动获得回收。PRD D1 因此不必再纠结：常驻是有意的，释放时的回收本来就该有 |

### 1.2 设计目标

把「谁负责回收」从**调用方**移到**生命周期所有者**（`GameDataAccess.Dispose`），使新增调用点**不可能**漏——因为不再需要调用方做任何事。

### 1.3 接口变更

**已实装**：`ReclaimOnDispose`、`OpenInstanceCount`、`TotalOpens`、`TotalDisposes`、`ReclaimsScheduled`、`MemoryReclaimer.Requests`。

```csharp
// GameDataAccess 新增
private bool _reclaimOnDispose = true;

/// <summary>
/// 关闭时是否自动排一次内存回收。默认开——调用方「用完即回收」不需要做任何事。
/// 关掉用于两类场景：(1) 长期持有的连接态（如词缀工作台的会话）；
/// (2) 测试里需要精确控制回收时机。
/// </summary>
public bool ReclaimOnDispose
{
    get => _reclaimOnDispose;
    set => _reclaimOnDispose = value;
}

// 诊断出口（对齐已有 HasOpenLocks 风格）
public static int  OpenInstanceCount   => Volatile.Read(ref _openInstanceCount);
public static long TotalOpens          => Volatile.Read(ref _openSequence);
public static long TotalDisposes       { get; }   // 新增，Dispose 成功时 ++
public static long ReclaimsScheduled   { get; }   // 新增，调度回收时 ++
```

```csharp
// MemoryReclaimer 新增（诊断；与既有 ChainStarts / Passes 同级）
internal static int Requests;   // Reclaim() 被调用的次数，含合并进来的
```

### 1.4 Dispose 实现（顺序是关键）

```csharp
public void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
    _ggpk?.Dispose();
    _index?.Dispose();
    _mappedIndex?.Dispose();
    _ggpk = null; _index = null; _mappedIndex = null;

    if (_registeredOpen)
    {
        Interlocked.Increment(ref _totalDisposes);
        if (Interlocked.Decrement(ref _openInstanceCount) == 0)
        {
            LocksChanged?.Invoke(false);
            FileLogger.App.Info("Game data locks released (no open instances).");
        }
    }

    // ⚠️ 必须在计数递减之后：CreateAbortCheck 的判据是 HasOpenLocks，
    // 若在递减之前调用，自己会让 HasOpenLocks 为 true，回收会被自己的 abort 检查挡掉。
    if (_reclaimOnDispose)
    {
        Interlocked.Increment(ref _reclaimsScheduled);
        MemoryReclaimer.Reclaim(CreateAbortCheck());
    }
}
```

**语义等价性论证**：现有调用点的模式是

```csharp
using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved)) { ... }
MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());   // 块外，此时计数已减
```

下沉后执行点从「块外紧接着」变成「块内 Dispose 末尾、计数已减之后」，`_openSequence` 与 `_openInstanceCount` 的取值完全一致 ⇒ **`CreateAbortCheck()` 的行为不变**。

**唯一行为差异**：`using` 块内提前 `return` 的路径现在也会回收（这正是我们要修的那类 bug）。

### 1.5 调用点迁移清单

实际删掉的 **15 个调用点**（基线 `cb7bc4791`，`grep -rn "MemoryReclaimer.Reclaim" src` 的全部命中）：

| 文件 | 处数 | 处理 |
|---|---:|---|
| `Shared/FxPatchEngine.cs` | 4 | 删除；Pass 1 预检（`:1141`）由下沉自动覆盖 |
| `Shared/IndexBackupService.cs` | 2 | 删除 |
| `Shared/FxDiff.cs`（`:104` 覆盖 `:75-76`） | 1 | 删除 |
| `Shared/GameDataPathPreference.cs` | 1 | 删除 |
| `Shared/PatchBundleRepair.cs` | 1 | 删除 |
| `Shared/GameDataLoader.cs` | 1 | 删除 `RequestReclaim()` 私有方法及其 4 个调用点（4 个 `Use*` 重载的 `finally`） |
| `Plugins.DataBrowser/DataBrowserView.xaml.cs` | 1 | 删除 |
| `Plugins.DataBrowser/MapNumberView.xaml.cs` | 1 | 删除 |
| `Plugins.DataBrowser/Services/MapNumberReplacementService.cs` | 1 | 删除 |
| `Plugins.PriceTagger/PriceTaggerPlugin.cs` | 1 | 删除（`ReleaseView()` 里的收尾调用，本身不 Open） |
| `Plugins.PriceTagger/PriceTaggerView.xaml.cs` | 1 | 删除（连带说明该手工调用的两行注释） |

> **SPEC 原清单有漏**：漏了 `PriceTaggerPlugin` 与 `PriceTaggerView` 两处（它们不自己 `Open*`，靠 `GameDataLoader`，所以按「Open 计数配对」扫不出来）。**教训：只扫 `Open*` 与 `Reclaim` 的配对会漏掉不自己打开的调用点，迁移要以 `grep MemoryReclaimer` 为准。**

`AffixDataService.cs:205` **未改**：连接态常驻，不 dispose 就不触发回收，与改动前一致；其 `_gd?.Dispose()` 在断开连接时现在会自动回收（额外收益）。

> ⚠️ **实现时踩到的坑（CS1524）**：这些 `finally` 里往往只有一句 `Reclaim`，删掉它之后 `try { ... }` 变成没有 catch/finally 的非法结构。
> 但**不能把整个 try 块删掉**——它同时是 `using var gd = ...` 的作用域，删掉会让 `gd` 一直活到方法结束（语义改变：数据晚释放、回收也晚）。
> 正确做法：把 `try { using var gd = ...; <body> }` 改写成 `using (var gd = ...) { <body> }`。作用域不变，缩进不变，`Reclaim` 由 Dispose 接手。
> 本轮按此改了 6 处：`FxPatchEngine` ×4、`IndexBackupService`、`MapNumberReplacementService`。

> **迁移后的验证点**：`grep -rn "MemoryReclaimer.Reclaim" src --include=*.cs | grep -v '/obj/'` 应只剩 `GameDataAccess.cs`（新的下沉点）与两处 XML 文档注释。本轮实测符合。

### 1.6 测试设计

**已落地**：`tests/PoEToolbox.Tests/GameDataDisposalTests.cs`，4 条用例（配对计数 / 重复 Dispose 只排一次 / `ReclaimOnDispose=false` 不排 / `using` 内提前 return 仍排）。

```csharp
[Fact]
public async Task Dispose_SchedulesReclaimAndPairsCounts()
{
    var opensBefore    = GameDataAccess.TotalOpens;
    var disposesBefore = GameDataAccess.TotalDisposes;
    var reclaimsBefore = GameDataAccess.ReclaimsScheduled;

    using (var gd = GameDataAccess.OpenReadOnlyMapped(seed.IndexPath))
        Assert.Equal(1, GameDataAccess.OpenInstanceCount);

    Assert.Equal(0, GameDataAccess.OpenInstanceCount);
    Assert.Equal(1, GameDataAccess.TotalOpens        - opensBefore);
    Assert.Equal(1, GameDataAccess.TotalDisposes     - disposesBefore);
    Assert.Equal(1, GameDataAccess.ReclaimsScheduled - reclaimsBefore);
}

[Fact]
public void Dispose_Twice_ReclaimsOnce()        // 幂等（复用已释放路径）

[Fact]
public void ReclaimOnDispose_False_DoesNotSchedule()

[Fact]
public void NestedOpen_InnerDispose_DoesNotReclaim()   // abort 语义保持
```

**反向有效性验证（必做，写入验收 A3）**：临时注释掉 `Dispose` 里的 `Reclaim` 调用 → `Dispose_SchedulesReclaimAndPairsCounts` 必须失败 → 恢复。

> ✅ **已执行**：禁用后 4 条用例红 3 条（第 4 条是 `ReclaimOnDispose=false` 用例，本就不该排回收，保持绿，符合预期）。

> 测试环境注意：`MemoryReclaimerTests` 已通过 `FirstPassDelay` / `PassInterval`（internal static）缩短延迟，新用例沿用同一手法，避免真等 5 秒。

> **新增测试约定**：涉及进程级游戏数据/回收状态的测试类必须进 `[Collection(GameDataTestCollection.Name)]`；涉及 `AffixColorScheme.StorageDirectoryOverride` 的必须进 `[Collection(AffixSchemeTestCollection.Name)]`（本轮新增）。
> 后者是**本轮暴露的既有竞态**：`AffixColorSchemeTests` 与 `AffixOfficialRestoreTests` 共用那个静态字段却并行跑，加入新测试类改变调度后首次打成红灯（表现为 `ListSchemeNames()` 多出一条「官方原版」）。已用 collection 串起来。

### 1.7 风险

| 风险 | 缓解 |
|---|---|
| 自动回收在 UI 线程路径上卡顿 | `Reclaim` 是后台 Task；`Dispose` 只做调度不做 GC |
| 某些调用点**故意**不回收 | `ReclaimOnDispose` 逃生口 |
| 回收链被高频 Dispose 刷爆 | 已有 coalescing（合并 + 每请求最多加 1 轮）；`Requests` 计数用于观察 |

---

## 2. W2：`FxPatchEngine.cs` 拆分

### 2.1 为什么是这 9 个文件

按**真实方法归属**划分（不是按感觉）。以下方法清单由 grep 提取，方法名以实际为准（`grep` 可能漏掉个别方法）：

| 目标文件 | 职责 | 迁移的方法（基线行号） | 预估行数 |
|---|---|---|---|
| `Fx/FxPatchEngine.cs` | 门面：命令行入口 + 动作分发 | `Run`(:174)、`RunBuiltIn`(:264)、`Usage`(:535) | ~180 |
| `Fx/FxPatchCommands.cs` | 命令实现 | `CmdStatus`(:1108)、`CmdStatusList`(:1004)、`CmdCleanup`(:542)、`CmdPurge`(:659)、`CmdRestoreBaselineFull`(:561) | ~420 |
| `Fx/FxPatchPackage.cs` | 补丁包解析 / 解压 / 校验 / 临时目录 | `ParsePatchJson`(:387)、`LoadPatchFile`(:397)、`ExtractZipPatch`(:421)、`TryGetSafeChildPath`(:489)、`ResolvePatchFilePath`(:511)、`CleanStalePatchTempDirs`(:520)、`ValidatePatch`(:702)、`IsSha256`(:1773) | ~260 |
| `Fx/FxBuiltInPatches.cs` | 内嵌资源释放与状态查询 | `BuiltInPatchDef`(:45)、`ReadEmbeddedPatchJson`(:331)、`MaterializeBuiltInPatch`(:352)、`TryResolveBuiltInPatchPath`(:374)、`LoadBuiltInPatch`(:313)、`BuiltInPatchStatus`(:1062)、`QueryBuiltInStatus`(:1066) | ~200 |
| `Fx/FxRawPackPatch.cs` | 整包替换型补丁 | `RawPackFile`(:738)、`RawPack`(:740)、`TryDetectRawPack`(:749)、`RawNoBundleOperation`(:789)、`RawRevert`(:862)、`RevertRawPackFromLedger`(:877)、`RevertRawPackFiles`(:895)、`RawStatus`(:952)、`ReadRawManifest`(:975)、`SameContent`(:993) | ~280 |
| `Fx/FxPatchState.cs` | 状态判定（纯函数，无 IO） | `OpState`(:171)、`ComputeStates`、`EffectiveStates`(:1035)、`OverallOf`(:1041)、`DetailOf`(:1099)、`CountUnresolved`(:1768)、`RecordRemoved`(:1303) | ~200 |
| `Fx/FxPatchOperations.cs` | 单条 op 的执行 | `ExecuteOp`(:1412)、`BuildDerivedContent`(:1543)、`ReplaceFirstN`(:1719)、`CountOccurrences`(:1737)、`Truncate`(:1740) | ~250 |
| `Fx/FxDatc64Pointers.cs` | datc64 指针定位与改写（纯字节运算） | `DatLayout`(:1563)、`PtrField`(:1565)、`ParseLayout`(:1567)、`DecodeUtf16At`(:1593)、`FindExactString`(:1602)、`PointerAt`(:1618)、`LocatePtrField`(:1638)、`RepointString`(:1682) | ~180 |
| `Fx/FxPatchIdentity.cs` | bundle 命名与归属判定 | `BundlePathOf`(:117)、`BundlePrefixOf`(:124)、`IsBundleOwnedBy`(:129)、`IsPatchOwned`(:141)、`OwnedPathsOf`(:149)、`DisplayNameOf`(:1307)、`KindOf`(:1311) | ~80 |

合计约 2050 行（原单文件 1783 行；差值来自每个文件新增的 `using` / `namespace` / 头注释）。

**唯一新增的「设计」内容只有目录划分**。所有方法体**原样搬迁**，禁止顺手改写。

### 2.1.1 进度（2026-09-19）

| 步骤 | 状态 | 结果 |
|---|---|---|
| S0 性质测试 | ✅ | 见 §3 |
| **S1 `FxPatchIdentity`** | ✅ | 已抽到 `Shared/Fx/FxPatchIdentity.cs`（5 个方法，命名空间仍为 `PoEToolbox.Shared`）。`FxPatchBundleNamingTests` 的 22 处引用改为 `FxPatchIdentity.`；测试全绿 |
| **S1.5 提取数据模型** | ✅ | `PatchDef` / `PatchOp` / `TextReplace` / `PatchState` / `OpState` 提到 `Shared/Fx/FxPatchModel.cs`（命名空间级，字段与语义未改）。后续新文件可直接写 `PatchDef` |
| **S2 `FxDatc64Pointers`** | ✅ | 抽到 `Shared/Fx/FxDatc64Pointers.cs`：`DatLayout` / `PtrField` 成为其嵌套 record；`Log` 仍回调 `FxPatchEngine.Log`。`FxPatchEngine.cs` → **1489 行**。缺口（ptr 性质测试）见 §3，已在本步补上 |
| **S3 文本编解码上移** | ✅ | `DecodeText`/`EncodeText`/`DetectEncoding` 合入 `Shared/TextEncodingDetector.cs`，改名 `Decode`/`Encode`（与已有 `Detect` 对齐）；可见性仍 `internal`（同程序集）。调用点：`FxPatchEngine` 4 处 + `FxDiff` 2 处 |
| **S4 `FxPatchPackage`** | ✅ | `Shared/Fx/FxPatchPackage.cs`（198 行）：`PatchJsonOptions` / `ParsePatchJson` / `LoadPatchFile` / `ExtractZipPatch`×2 / `TryGetSafeChildPath` / `ResolvePatchFilePath` / `CleanStalePatchTempDirs` / `ValidatePatch` / `IsSha256`。测试侧 5 处调用改指新类（`FxDiffPackagingTests` / `FxPatchSecurityTests` / `FxRawPackPatchTests`） |
| **S5 `FxBuiltInPatches`** | ✅ | `Shared/Fx/FxBuiltInPatches.cs`（120 行）：注册表 `BuiltIns` + `LoadBuiltInPatch` / `ReadEmbeddedPatchJson` / `MaterializeBuiltInPatch` / `TryResolveBuiltInPatchPath` / `QueryBuiltInStatus`。`BuiltInAll` 与 `RunBuiltIn` 留在门面。两个 record（`BuiltInPatchDef` / `BuiltInPatchStatus`）按 §2.6 的口径进 `FxPatchModel.cs`；`typeof(FxPatchEngine).Assembly` 改 `typeof(FxBuiltInPatches).Assembly`（同一程序集，等价）。调用点：`FxPatchView` 4 处 + `FxBuiltInPatchTests` 4 处 |
| **S6 `FxRawPackPatch`** | ✅ | `Shared/Fx/FxRawPackPatch.cs`（281 行）：`RawPackFile` / `RawPack` / `RawManifestFileName` / `TryDetectRawPack` / `RunRawPack` / `RawNoBundleOperation` / `RawApply` / `RawRevert` / `RevertRawPackFromLedger` / `RevertRawPackFiles` / `RawStatus` / `ReadRawManifest` / `SameContent`。`FxRawPackPatchTests` 15 处改指新类 |
| **S7 状态 + 操作** | ✅ | `Shared/Fx/FxPatchState.cs`（158 行）：`EffectiveStates`(private) / `OverallOf` / `MarkOf` / `DetailOf` / `ComputeStates` / `ComputeState`(private) / `CountUnresolved` / `Label`。<br>`Shared/Fx/FxPatchOperations.cs`：`ExecuteOp` + `BuildDerivedContent` / `ReplaceFirstN`(private) / `CountOccurrences`(private) / `Truncate`(private)。**`ExecuteOp` 按 §2.2 拆成 4 个私有方法**（`ExecuteAsset` / `ExecuteDerived` / `ExecutePtrById` / `ExecuteEditText`），`default` 分支变成 switch 的 `_ => throw`。可见性按「谁调用」定：状态层 `internal`（引擎 + `FxBuiltInPatches` 跨文件调用），执行层只有 `ExecuteOp` / `BuildDerivedContent` 需要 `internal`。归一化逐行比对：除 S1~S3 已抽走的 `FxDatc64Pointers.*` / `TextEncodingDetector.*` / `FxPatchIdentity.*` 调用点与新增的 5 个方法签名外，**0 处逻辑漂移** |
| **S8 命令与门面** | ✅ | `Shared/Fx/FxPatchCommands.cs`（420 行）：`CmdCleanup` / `CmdRestoreBaselineFull` / `CmdPurge` / `CmdStatusList` / `CmdStatus` / `CmdApplyOrRevert` / `RecordApplied`(private) / `RecordRemoved`(private) / `DisplayNameOf`(private) / `KindOf`(private)。<br>顺手兑现 §2.2 的两处大方法拆分：`CmdApplyOrRevert`(160) → `Precheck` / `ApplyWrites` / `Verify` 三个私有 pass（**预检只读、写入事务、重开校验的 `saveIndex` 语义各自独立，没有混拆**）；`CmdRestoreBaselineFull`(98) → 取 `RevertAllRawPacks`。<br>日志走文件顶上的两行私有转发（`private static void Log(...) => FxPatchEngine.Log(...)`），这样 60 处调用点保持逐字节不变，归一化比对才能当真。<br>门面 `FxPatchEngine.cs` 只剩 **211 行**：`LogSink` / `Log` / `LogErr` / `BuiltInAll` / `PatchBundleDirectory` / `Run` / `RunBuiltIn` / `DispatchPatch` / `Usage` / `Sha`。 |

**引擎行数轨迹**：1754（基线）→ S1 1699 → S2 1489 → S3 1464 → S4 1282 → S5 1173 → S6 907 → S7 586 → **S8 211**。
`Fx/` 九个模块合计 1684 行，最大单文件 420 行（命令层）。

> **S4/S5 的两处经验**（写下来避免后续重复踩）：
> 1. **机械搬迁要用脚本 + 归一化逐行比对**：把新文件每一行（去缩进、去 `Fx*.` 前缀、`private/internal/public static` 归一）与基线原文比对，实测 `FxPatchPackage` / `FxRawPackPatch` 的搬迁差异行数为 **0**，`FxBuiltInPatches` 仅 1 行（上面那条 `typeof`）。手改缩进曾把 `FxPatchPackage` 搞成混合缩进，靠这个检查兜住。
> 2. **`private` 不得因为搬文件就顺手升成 `internal`**：搬完后要按「谁调用」回收可见性（`FxPatchPackage` 的 `PatchJsonOptions` / `ValidatePatch` / `IsSha256` / `CleanStalePatchTempDirs` 已改回 `private`，与基线一致）。
>    顺带澄清一个虚惊：`CleanStalePatchTempDirs` 在基线就只有 `ExtractZipPatch` 一个调用点，`cleanup` 子命令走的是已删除的 `CmdCleanup`，14 天临时目录清理并不是新近才失去入口的。

> ✅ **S7 已兑现的两条约束**：
> 1. S5 为搬 `QueryBuiltInStatus` 把 `ComputeStates` / `OverallOf` / `DetailOf` 从引擎的 `private` 临时升成 `internal`。S7 把它们移进 `FxPatchState.cs` 后，这层放宽变成模块自身的 API，引擎侧不再有被放宽的成员（`EffectiveStates` / `ComputeState` 回到 `private`）。
> 2. 破坏性自检（证明搬走的代码是活代码，不是影子副本）：把 `FxPatchOperations.ExecuteEditText` 的方向判断改成恒定 → 性质测试红 3 / 绿 5；把 `FxPatchState.ComputeState` 的 `edittext`「已应用」分支改成 `Incompatible` → 同样红 3。两次均已还原并 diff 确认逐字节回到原样。
>    ⚠️ **顺带暴露的覆盖缺口**：把 `OverallOf` 的冲突归约整块删掉，8 条性质测试**全绿**——即 `OverallOf` / `DetailOf` / `MarkOf` 这一层「状态归约 + 展示文案」不在性质测试的射程内，只有 `FxPatchBundleNamingTests` / `FxBuiltInPatchTests` 间接经过。这层驱动 UI 勾选默认值与 `status` 输出，见 §12 待办。

### 2.2 超大方法处理

拆分后仍有三个方法偏大，建议在同一提交内**仅拆方法、不改逻辑**：

| 方法 | 行数（S6 后实测） | 拆法 |
|---|---:|---|
| `CmdApplyOrRevert` | ~~160~~ **已拆** | ✅ S8 内完成：`Precheck`(26) / `ApplyWrites`(85，含事务回滚) / `Verify`(31)，编排层剩 34 行。预检只读与写入事务分开，未混拆 |
| `ExecuteOp` | ~~131~~ **已拆** | ✅ S7 内完成：`ExecuteAsset`(53) / `ExecuteDerived`(37) / `ExecutePtrById`(19) / `ExecuteEditText`(25)，`ExecuteOp` 只剩 9 行分派 |
| `CmdRestoreBaselineFull` | ~~98~~ **已拆** | ✅ S8 内完成：取 `RevertAllRawPacks`(28) 后剩 78 行（基线回写 + PATCHED 清理 + 恢复后自检，三段语义不同，没继续拆） |
| `CmdStatus` | 13 | ~~原表写 ~195~~ 已在上一轮收缩，无需再拆 |
| `ComputeState` | 92 | 每个 op 一个 `case`，已在 S7 的目标文件里；如仍嫌大可按 op 拆 4 个 `State*` 纯函数 |

### 2.3 迁移顺序（每一步都必须可编译 + 测试全绿）

| 步 | 动作 | 风险 | 说明 |
|---|---|---|---|
| S0 | 落 W3 性质测试 | — | **准入条件。没有 S0 不许开工** |
| S1 | 抽 `FxPatchIdentity` | 极低 | 纯静态、无状态、无 IO |
| S2 | 抽 `FxDatc64Pointers` | 极低 | 纯字节运算；`internal` 可见性不变 |
| S3 | `DecodeText`/`EncodeText`/`DetectEncoding` 上移到 `TextEncodingDetector` | 低 | 见 §2.4 |
| S4 | 抽 `FxPatchPackage` | 低 | 只做路径与 JSON 处理 |
| S5 | 抽 `FxBuiltInPatches` | 中 | 涉及内嵌资源释放，`EmbeddedRuntimeAssetsTests` / `FxBuiltInPatchTests` 会覆盖 |
| S6 | 抽 `FxRawPackPatch` | 中 | 最大独立块，与主引擎几乎无耦合；`FxRawPackPatchTests` 覆盖 |
| S7 | 抽 `FxPatchState` + `FxPatchOperations` | 中高 | 引擎核心；依赖 S0 的性质测试 |
| S8 | 抽 `FxPatchCommands`，`FxPatchEngine` 收尾为门面 | ✅ 完成 | 门面 211 行，只留 `Run` / `RunBuiltIn` / `Usage` / `DispatchPatch` / 日志出口 |

**每步的完成判据**：`dotnet test -c Release` 全绿 → 提交（一个步骤一个提交，便于二分定位）。

### 2.4 依赖方向约束（重要）

`Core → Shared`，因此 **`Shared` 不能引用 `Core`**。`Core/Binary/Datc64/` 目录虽已有 datc64 代码，但 `FxDatc64Pointers` **不能移到 Core**（会形成循环依赖）。

本次处理：留在 `Shared/Fx/`。后续可选（记入待办，不在本次范围）：把纯 datc64 字节工具下沉到 `LibDat2` 或新建 `PoEToolbox.Datc64` 基础库，供 Core 与 Shared 共用。

`DecodeText` / `EncodeText`（携带 BOM 写回）是**通用文本编解码**，不属于补丁引擎。上移到 `Shared/TextEncodingDetector.cs`（该类已在 Shared，且已含 `Detect` / `LooksLikeUtf16Le`），命名保持 `internal` 可见性不变。

### 2.5 不变式（拆分过程中必须成立）

1. **可见性不变**：所有被测试与插件访问的 `internal` 成员保持 `internal`（同程序集，`InternalsVisibleTo` 5 条不受影响）。
2. **数据模型暂不移动**——见 §2.6：它们目前是 `FxPatchEngine` 的嵌套类型，被 `InternalsVisibleTo` 的插件与测试直接引用。**建议先做一次「只提取类型、不改逻辑」的提交再继续拆**。
3. **落盘语义不变**：`saveIndex: false` 收集 + 末尾显式 `gd.Save()` 的模式不得改变（`Index.CleanupOrphanCustomBundles` 自己不落盘）。
4. **文件头注释随方法一起搬**——项目里的注释承载了大量「为什么」（如落盘陷阱、多行引用约束），丢了就是净损失。

### 2.6 嵌套类型约束（实施时发现，原 SPEC 未写）

`FxPatchEngine` 是一个静态类（基线 `:16`–`:1754`），**`PatchDef` / `PatchOp` / `PatchState` / `OpState` / `BuiltInPatchDef` 全是它的嵌套类型**，不是命名空间级类型。后果：

- 新文件里引用要写 `FxPatchEngine.PatchDef`（本轮 `FxPatchIdentity.cs` 就是这么写的）；
- 测试里已有的 `FxPatchEngine.BundlePathOf(...)` 这类调用，拆分后**必须同步改类名**——本轮 `FxPatchBundleNamingTests` 有 22 处；
- 好处：`internal` 在同一程序集内跨命名空间可见，测试与 `InternalsVisibleTo` 的插件不受影响。

**建议的插入步骤（S1 与 S2 之间）**：

> **S1.5 提取数据模型**：把 `PatchDef` / `PatchOp` / `TextReplace` / `PatchState` / `OpState` 提到命名空间级 `Shared/Fx/FxPatchModel.cs`。只搬类型定义，不改任何逻辑。之后 S2~S8 的新文件都能直接写 `PatchDef`。

---

## 3. W3：引擎性质测试（B2，B3 的准入条件）— ✅ 已完成

> **已落地**：`tests/PoEToolbox.Tests/FxPatchPropertyTests.cs`，**9 条**（原计划 3 条，落地时按 op 类型拆细了）。

| 用例 | 覆盖 | 结果 |
|---|---|---|
| `EditText_ApplyThenRevert_RestoresBytesExactly` | P-1 / `edittext` | ✅ |
| `AddFileAsset_ApplyThenRevert_RestoresBytesExactly` | P-1 / `addfile-asset` | ✅ |
| `AddFileDerived_ApplyThenRevert_LeavesTheSourceUntouched` | P-1 / `addfile-derived` | ✅ |
| `ApplyTwice_LeavesTheTargetIdentical` | P-2 | ✅ |
| `TwoIndependentPatches_RevertedInApplyOrder_StillRestoreBoth` | P-3 | ✅ |
| `PatchPtr_ApplyThenRevert_RestoresBytesExactly` | P-1 / `patchptr-byid` | ✅ S2 补 |
| `PatchPtr_ApplyTwice_LeavesTheTableIdentical` | P-2 / `patchptr-byid` | ✅ S2 补 |
| `PatchPtr_AppendingMissingTarget_OnlyTheLocatedPointerChanges` | 追加语义 / `patchptr-byid` | ✅ S2 补 |

**`patchptr-byid` 的实现要点**（S2 补，原「已知缺口」已关闭）：

- 夹具自带一个 `BuildDatc64`，按引擎实测的布局造最小表：行数(int32) + 定长行区（每行 `Id`/`AOFile` 两个 int64 指针）+ `0xBB×8` 分隔符 + UTF-16LE 字符串池，指针公式 `v = abs - dataOffset + 8`。**断言侧**也直接用 `FxDatc64Pointers.ParseLayout` / `DecodeUtf16At` 反解，不重复实现公式。
- **P-1 的字节级还原只在「目标串已在池中」时成立**：此时 apply/revert 都是严格的单字段改写。目标串缺失时引擎按设计**追加**（保留旧 EOF 作空串哨兵），表会变长——所以第三条用例断言的是「除被定位的那条指针外，原表前缀逐字节不变，且 revert 后前缀完整回来」，而不是错误的「长度也不变」。
- `SeedTable` 会重建 seed 索引以加入表文件（`GameDataAccess` 未打开时重写 `_.index.bin` + `seedbundle.bundle.bin` 是安全的）。

**实现要点**（与 §3.2 原计划的两处差异）：

1. 补丁 JSON 是**手写匿名对象序列化**的（`PatchId` / `BundleName` / `Operations`），不依赖 `AffixPatchBuilder`——后者只产 `addfile-asset` 一种 op，覆盖不到三种。
2. `addfile-derived` 的断言改为「**源文件字节不变**」而不是「副本被删」：派生副本 revert 时按设计豁免保留（引擎里 `!apply && s.Op.Op == "addfile-derived"` 那条），断言它消失会误报。

**已知缺口**：~~`patchptr-byid` 未覆盖~~ → **S2 时已补 3 条**（见上表）。四种 op 现已全覆盖。

### 3.1 数据准备

复用现有测试基础设施：`tests/PoEToolbox.Tests/SeedIndex.cs` 构造最小 Bundles2 索引；`Fixtures/**/*` 已在测试 csproj 里以 `Fixtures\**\*` 引入。

### 3.2 用例

```
P-1  Revert_RestoresTargetBytesExactly（对 4 种 op 各跑一遍）
     before = ReadFile(target)
     Apply(patch)   → 断言目标 op 状态为 Applied
     Revert(patch)  → 断言状态回到 NotApplied
     after  = ReadFile(target)
     断言 before.Span.SequenceEqual(after)      ← 逐字节，不比位置

P-2  Apply_Twice_IsNoOp
     Apply → 记录 bundle 内容哈希
     Apply → 哈希不变；状态仍为 Applied；新增文件数不增加

P-3  StackedRevert_IsOrderIndependent
     Apply(P1) → Apply(P2) → Apply(P3)
     Revert(P2) → Revert(P1) → Revert(P3)
     断言：三者状态均为 NotApplied；目标文件 == 初始内容；
           无残留 PATCHED bundle 引用（用 OwnedPathsOf + Index 查询验证）
```

**为什么是这三条**：它们对应产品的三条承诺——可回滚、重复执行安全、多补丁共存。而且它们覆盖**不变式**而非某个补丁的行为，新增 op 类型时自动生效。

### 3.3 有效性自检（写入验收 A3）

人为破坏一次 revert 逻辑（例如让 `RepointString` 少写一个字节）→ P-1 必须失败 → 恢复。**不做这一步的测试视为无效**。

> ✅ **已执行（两次）**：
> 1. `ExecuteOp` 的 `edittext` 分支把 revert 写入改成 `EncodeText(text + "SABOTAGE", ...)` → `EditText_*` 与 `TwoIndependentPatches_*` 红 2 条，`ApplyTwice_*` 保持绿（它只比 apply↔apply）→ 已恢复。
> 2. S2 补完 ptr 用例后再破两次 `FxDatc64Pointers.RepointString`：(a) 相对指针公式少减 8 → 3 条 ptr 用例全红、其余 5 条绿；(b) 去掉「目标串已存在则复用」的分支（强制追加）→ 同样红 3 条。两次均已恢复并复测全绿。
>
> 结论：ptr 用例对「定位公式」和「复用 vs 追加」两条不变式都敏感，不是只跑通的假绿。

---

## 4. W4：网络超时统一

### 4.1 现状

| 位置 | 现状 | 问题 |
|---|---|---|
| `DatContainer.cs:37` | `Timeout = Timeout.InfiniteTimeSpan` | 下载 dat 定义时永久挂起 |
| `PatchClient.cs:271` | `timeoutEachFile ?? Timeout.InfiniteTimeSpan` | 默认无超时 |
| `UpdateChecker.cs:96` | `Timeout = TimeSpan.FromSeconds(5)` + bounded read | ✅ 正确范式，作为参考 |

### 4.2 设计（已修正：第三方库用不上 `NetworkDefaults`）

> ⚠️ **修正（实施时发现）**：`LibDat2` 与 `LibGGPK3` **没有任何 `ProjectReference`**（刻意保持自包含，便于与上游比对），看不到 `PoEToolbox.Shared` 里的 `NetworkDefaults`。原方案「两处都改成 `NetworkDefaults.RequestTimeout`」不可行。
>
> **实际做法**：第三方库里各写本地常量 `TimeSpan.FromSeconds(30)` 并在注释里说明「本库不引用 PoEToolbox.Shared」；`Shared` / `Core` 侧的调用点才用 `NetworkDefaults`。

```csharp
// 新增 Shared/NetworkDefaults.cs（已落地）
public static class NetworkDefaults
{
    /// <summary>单次 HTTP 请求的整体超时。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>建立连接的超时（比整体超时短，便于快速失败）。</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
}
```

已改的调用点：

| 位置 | 改动 |
|---|---|
| `DatContainer.cs:37` | `Timeout.InfiniteTimeSpan` → `TimeSpan.FromSeconds(30)`（本地常量 + 注释） |
| `PatchClient.cs:271` | `timeoutEachFile ?? Timeout.InfiniteTimeSpan` → `timeoutEachFile ?? TimeSpan.FromSeconds(30)`（保留调用方覆盖能力） |
| `SchemaManager.cs` | `new HttpClient()` → `new() { Timeout = NetworkDefaults.RequestTimeout }` |
| `PoeNinjaFetcher.cs` | §4.2 当时未动（30s 与 `RequestTimeout` 等值）；**§4.3 这一轮换成了 `NetworkDefaults.RequestTimeout`**（两处字面量），因为失败提示本来就要动这个文件 |
| `EdgeTranslationClient.cs:16` | 同上，随 §4.3 一并换成 `NetworkDefaults.RequestTimeout`（超时值不变，只是取消「改默认值时漏掉这里」的隐患） |
| `UpdateChecker.cs:96` | 5s + bounded read，有意设计，未动（作为范式参考） |

### 4.3 调用点检查 — ✅ 完成

超时后抛的是 `TaskCanceledException` / `OperationCanceledException`。§4.2 只统一了超时值，
真正的问题在这里：框架给的是英文长句，而且**超时与用户主动取消是同一个异常类型**——不区分的后果是
「30 秒后抛异常冒泡」而不是「30 秒后这一类失败、其余照抓、并给用户一句话」。

- [x] 超时值统一（见 §4.2）
- [x] ~~`DatContainer.ReloadDefinitions*` 的联网路径~~ → **查无此路径**：`DatContainer.cs:36-66` 的 `DownloadSchemaMin()`
      是唯一联网口，只在 `SchemaMin` 为真时进入，而**全仓没有一处把 `SchemaMin` 置真**（`ReloadDefinitions()` 读本地文件→内嵌资源，不联网）。
      为一个不可达分支加提示 = 死代码。**行动项**：这属于「该删的没删」，已记入 §11 backlog，不在本轮做。
- [x] ~~`PatchClient` 的下载路径~~ → `UpdateNodeAsync`（`PatchClient.cs:255-272`）**全仓无调用方**，同上不加死代码。
- [x] `SchemaManager.EnsureSchemaAsync` 兜底**本来就有**：主 URL → 备份 URL → 本地缓存，三条都失败才抛
      `InvalidOperationException("Cannot download schema.min.json and no local cache available.")`（`SchemaManager.cs:28-79`）。
      不需要改，只是原来没核实。
- [x] `PoeNinjaFetcher` 的降级提示：**这一条是真 bug，已修**（见下）
- [x] `UpdateChecker` 复核：5s + `GetBoundedStringAsync` + 256 KiB 上限，外层 catch 落回 `GetCachedResult()`，已是范式，未动。

#### 实际改动

**1. `Shared/NetworkDefaults.cs` 新增 `DescribeFailure(Exception, CancellationToken)`**
把网络异常翻成一句中文：`已取消。` / `请求超时（30 秒内未响应），请检查网络后重试。` /
`服务器返回 HTTP 429（TooManyRequests）。` / `无法连接到服务器：{内层 SocketException}`；
非网络异常原样透传 `ex.Message`。

**2. `PoeNinjaFetcher.FetchAndSaveAsync` 的 catch 改成取消感知**（原来只有一行 `catch (OperationCanceledException) { throw; }`）：

```csharp
// HttpClient 的超时表现为 TaskCanceledException（OperationCanceledException 的子类），
// 不区分就会让一个分类超时中断整轮抓取，剩下十几个分类全部不再尝试。
// 只有用户真的按了取消才往外抛。
catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { RecordFailure(type, ex); }
catch (OperationCanceledException) { throw; }
catch (Exception ex) { RecordFailure(type, ex); }
```

`RecordFailure` 是提出来消掉两处重复的局部函数，失败原因同时进进度日志（UI 的 LogLine）和
`CategoryFetchResult.Error`。两处 `TimeSpan.FromSeconds(30)` 字面量换成 `NetworkDefaults.RequestTimeout`；
顺手把 `EdgeTranslationClient.cs:16` 的同一字面量也换掉（它在 Core 里，看得见 `NetworkDefaults`；
`LibDat2` / `LibGGPK3` 是自带工程、看不见，保持本地常量）。

**3. 新增 `tests/PoEToolbox.Tests/NetworkTimeoutMessageTests.cs`（3 条）**：注入桩 `HttpMessageHandler`
（不用真等 30 秒）——① 第一个分类超时 → 状态 `Partial`、第二个仍抓下来、进度里有「超时」；
② 真取消 → 冒泡 `OperationCanceledException` 且只发过 1 个请求；③ `DescribeFailure` 四种输入各说对话。

> 破坏性自检：删掉那条带 `when` 的 catch → ①变红且栈里就是原来那个 `TaskCanceledException` 冒泡（复现了旧 bug）；
> 把 `DescribeFailure` 的 `|| ex is OperationCanceledException` 去掉 → ①③同时变红。两次都已还原并复测。
> Release 全量 **192 通过 / 0 失败**（189 + 新增 3）。

> 单独提交：这是行为变更（一个分类超时的后果从「整轮停止」变成「这一类失败」），按 PRD §7 约束 1 不与机械改动混编。

---

## 5. W5：全局异常兜底

`App.xaml.cs:15` 已有 `DispatcherUnhandledException`（保持不动）。补两个：

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    // 现有 DispatcherUnhandledException 不动 ...

    // 31 处 async void 与 fire-and-forget Task 的异常目前会静默丢失
    TaskScheduler.UnobservedTaskException += (_, args) =>
    {
        FileLogger.WriteCritical("Unobserved task exception", args.Exception);
        args.SetObserved();
    };

    AppDomain.CurrentDomain.UnhandledException += (_, args) =>
    {
        FileLogger.WriteCritical("AppDomain unhandled exception",
            args.ExceptionObject as Exception);
    };
}
```

CLI 侧（`PoEToolbox.Cli/Program.cs`）同样补 `AppDomain.UnhandledException`，并把退出码规范为非 0（CLI 目前已有 `return 1/2` 约定，保持一致）。

---

## 6. W6：`build.bat` 补 `-m:1`

`build.bat` 的 `dotnet publish` 行加 `-m:1`，与记忆中的出包命令对齐，避免并行构建撞 `obj` 锁（MC1000 / Access denied）。

```
dotnet publish "src\PoEToolbox.App\PoEToolbox.App.csproj" -c Release -r win-x64 --self-contained false -o "publish" -m:1 -p:Version=%APP_VERSION% ...
```

---

## 7. W7：`Shared` 的 UI 关注点（**已修正原方案**）

### 7.1 事实核查结果

| 类型 | 引用者 | 能否移出 Shared |
|---|---|---|
| `ThemeManager.cs` | 只有 `PoEToolbox.App`（`App.xaml` / `App.xaml.cs` / `MainWindow.xaml.cs` / `Themes/light.xaml`） | ✅ **可以**，移到 `App/Themes/`，零插件影响 |
| `OutputPanel.xaml(.cs)` | `Plugin.AffixWorkbench`、`Plugin.FxPatch`（2 个视图）的 XAML | ❌ 不能移到 App——**插件不能引用 exe** |
| `UiStatus.cs` | **8 个插件**的 View（AffixWorkbench / DataBrowser ×2 / FxPatch ×2 / Poe2Font / PoeCnPatch / TermTranslator）+ `Shared/FxEngineRunner` | ❌ 同上 |
| `UILabels.cs` | `App`（DisclaimerPage / MainWindow）+ `Plugin.PriceTagger` | 纯字符串目录，可留在 Shared |

> **修正说明**：体检报告 P1-6 写的方案是「把 UI 关注点移到 App 或新建 `PoEToolbox.Ui`」。深入核查后确认：**「移到 App」对 `OutputPanel` / `UiStatus` 不可行**（8 个插件的编译依赖）。本 SPEC 据此调整范围。

### 7.2 调整后的范围

| 项 | 动作 | 风险 |
|---|---|---|
| **W7a** | ✅ 已做（`69645d931`）：`ThemeManager.cs` → `PoEToolbox.App/Themes/ThemeManager.cs`，命名空间随工程改为 `PoEToolbox.App`（引用者只有 App，两处调用点零改动） | 低 |
| **W7b** | ✅ 已做（`c8cabcbd4`）：新建 `PoEToolbox.Ui` 类库（`UseWPF`，引用 Shared），把 `OutputPanel.xaml(.cs)`、`UiStatus.cs`、`FxEngineRunner.cs` 三组移出 Shared；Shared 去掉 `UseWPF`。10 个工程加引用，9 个 `.cs` 加 `using PoEToolbox.Ui;`，3 个 `.xaml` 的命名空间前缀 `shared:` → `ui:` | 中（机械搬家，实测 0 行为变更） |

**W7b 实施时对原表的修正**：`FxEngineRunner.cs` 也必须一起搬。原 §7.1 只列了 `OutputPanel` / `UiStatus`，但 `FxEngineRunner` 的签名里带 `UiStatus.Kind`，而 `MainWindow.xaml.cs` 三处调用它——留在 Shared 里 Shared 就去不掉 WPF。

### 7.3 关于「Shared 混入 UI」的判断降级

需要如实说明：**这个批评比原报告写的弱**。

- `IPlugin.CreateView()` 返回 `UserControl`（`Sdk/IPlugin.cs`），**整个插件层本来就是 WPF**；
- `CLI` 自身的 TFM 是 `net10.0-windows`（不是 `net10.0`）。

所以「CLI 被迫带上 WPF」的实际代价很小。真正的收益是把 `ThemeManager`（纯展示、只有 App 用）与领域引擎分开，属于卫生问题而非架构缺陷。

> 若将来要彻底分离，做法是新建 `PoEToolbox.Ui` 类库供 App 与插件共同引用，而非移入 App。

**这条已经在 W7b/W8 里做了**（`c8cabcbd4` + `2463179fe`）。做下来对上面两句降级说明的校正：

- 「代价很小」在**部署**上成立（单文件 exe 里 WPF 程序集本来就在），但在**编译依赖**上不成立——`Shared` 引用 WPF 让 `Core` 白拿了一个它没声明的框架引用，搬家时立刻炸出 CS1069（见 §0 修正 4）。这类「靠上层顺手传下来的引用」正是分层要还的债。
- 分离后的可验证收益（二进制元数据里 `PresentationFramework` / `PresentationCore` / `WindowsBase` 三个字符串出现次数）：`Shared.dll` **2/2/2 → 0/0/0**；`Abstractions.dll` 在 `2463179fe` 前是 **1/0/0**（在探针 worktree 里 checkout `574404bd4` 实测），现在是 **0/0/0**；三者全部集中在 `Ui.dll`（2/2/2）。

---

## 8. W8：Sdk 定位 — ✅ 已执行（PRD D2 选 **b2**）

原推荐是 **(b) 改名 + 去 WPF**，其中「去 WPF」当时判为「收益不抵成本」而**排除**。最终落地时这一条被推翻，原因是：单看「拆 `CreateView()`」确实没有收益，但用户选 b2 后它与 W7b 天然配套——`PoEToolbox.Ui` 一旦存在，拆分只剩 11 个类名改一行的机械改动，边际成本近乎为零。

实际分三步、三个独立提交，顺序是 **U3 → U1 → U2**（先做纯改名，再搬 UI，最后拆契约；每步各自验证一次全量绿，出问题能定位到步）：

| 步 | 提交 | 动作 | 影响面 |
|---|---|---|---|
| U3 | `574404bd4` | `PoEToolbox.Sdk` → `PoEToolbox.Abstractions`（目录 / csproj / `RootNamespace` / `using` / `ProjectReference` 同步） | 35 文件，35 插 / 36 删；除 `IPlugin.cs` 少一行自引用 `using` 外，每一行改动都是这个标识符本身 |
| U1 | `c8cabcbd4` | 新建 `PoEToolbox.Ui`，`OutputPanel` / `UiStatus` / `FxEngineRunner` 移出 Shared（见 §7.2） | 4 文件搬走且全部被 `git diff -M` 识别为 rename；7 工程加引用，9 `.cs` 加 `using`，3 `.xaml` 前缀换名 |
| U2 | `2463179fe` | `IPlugin` 去掉 `CreateView()`（+ 移除 `UseWPF`）；新增 `PoEToolbox.Ui.IUiPlugin : IPlugin { UserControl CreateView(); }`；11 个插件类改实现 `IUiPlugin`；`MainWindow` 导航 `OfType<IUiPlugin>()`，`_activePlugin` 与 4 个辅助方法签名改类型 | 26 文件，64 插 / 28 删 |

契约分工保持原判断不变的两点：

| 项 | 结论 |
|---|---|
| `PluginManager` 仍按 `IPlugin` 持有与排序 | 无界面插件（后台同步、快捷键服务）可以只实现 `IPlugin`，注册后照样收生命周期回调，只是不进导航 |
| 在 `PluginManager` 注释里写明 | 「编译期静态注册」是有意选择（单文件 exe + 无版本地狱）。**这也是改名安全的根据**：程序集名不承担任何 ABI 含义 |

若选 (a) 补全契约（版本号 / 能力声明 / 生命周期），需先回答「谁来消费这个版本号」——在没有动态加载的前提下答案是没有，故不推荐。这条结论不变。

**`InternalsVisibleTo` 的一个坑**（U1 时踩到，CS0122）：`FxEngineRunner` 是 `internal`，而 `PoEToolbox.App` 工程的 `AssemblyName` 是 **`PoEToolbox`**（`PoEToolbox.App.csproj:21`）。授权项要写 `PoEToolbox` 而不是 `PoEToolbox.App`，工程里已就地注释说明，避免下一个人在这里加一行没用的。

---

## 9. 验证矩阵

| 批次 | 改动 | 自动化验证 | 手工验证 | 实测结果 |
|---|---|---|---|---|
| B1 | W1 | 4 条新用例 + 反向有效性自检 | — | ✅ 4 条绿；禁用后红 3 条 |
| B1 | W4 | 现有联网路径的异常用例（若有） | 断网下操作，确认快速失败且有提示，不挂起 | ✅ 值已统一（C3）；§4.3 调用点的降级与中文提示已落地（C7，3 条桩 handler 用例 + 两轮破坏自检）。**断网手工验证仍未执行**——要真拔网线才能测到，桩测试已覆盖同一路径 |
| B1 | W5 | — | — | ✅ App + Cli 均已挂 |
| B1 | W6 | — | `build.bat` 出包成功 | ✅ `dotnet publish -m:1` 出包 6.26 MB，publish 无 loose json |
| B2 | W3 | 8 条性质测试（含 3 条 ptr） | 破坏逻辑确认测试会红 | ✅ 8 条绿；破坏 edittext 红 2 条、破坏 RepointString 红 3 条（两种破法） |
| B3 | W2 S1 | 全量测试 | — | ✅ 185 绿（`FxPatchBundleNamingTests` 9 条覆盖抽取代码） |
| B3 | W2 S1.5 + S2 | 全量测试 | — | ✅ 185 绿（数据模型提到命名空间；datc64 指针逻辑进 `Shared/Fx/`） |
| B3 | W2 S2 的补齐 | 3 条 ptr 性质测试 | — | ✅ 188 绿；两次破坏均红 |
| B3 | W2 S3 | 全量测试 | — | ✅ 188 绿（文本编解码并入 `TextEncodingDetector`） |
| B3 | W2 S4~S6 | 全量测试 | 内置补丁 apply→查询→revert；整包替换型 apply→revert；purge；彻底还原；diff 生成 | ✅ 自动化全绿（S4/S5/S6 各一次）；**写入路径的手工验证已在本机真实客户端上跑完**（本人同意后执行，逐字节 SHA 对账，结果与三条新事实见 §9.1） |
| B3 | W2 S7 | 全量测试 + 三轮破坏自检 | 真实客户端只读 `status` | ✅ 189 绿；破坏 `ExecuteEditText` / `ComputeState` 各红 3 条（已还原）；删除 `OverallOf` 参半分支 → 只有新增的 P-4 红。**只读手工已过**：本轮对本机真实 `Path of Exile 2/Bundles2/_.index.bin` 跑 `fx-oilmod <gd> all status`，两个内置补丁共 9 条 op 三态判定正确，`patchptr-byid` 在真实 `miscanimated.datc64` 上定位到 ROW[8307/8311/8312/8313]。**写路径手工已跑完**（本人同意后执行，见 §9.1） |
| B3 | W2 S8 | 全量测试 + 活代码自检 | 真实客户端只读 `list` / `status` | ✅ 189 绿；让 `CmdApplyOrRevert` 直接返回 0（不写盘）→ 全量红 12 / 绿 177，证明命令层确为活代码而非影子副本（已还原）。真实客户端 `list` 输出 ☐/☐、`status` 单补丁三态正常。写路径手工已跑完，见 §9.1 |
| B3 | W7a | 全量测试 | 主题切换（light/dark）正常 | ✅ 自动化：192 绿，App 与 4 个插件工程均 0 警告 0 错误；`git mv` 被识别为 rename（98% 相似），改动只有命名空间与一行注释路径。**加载路径静态可证不变**：`pack://application:,,,/themes/*.xaml` 指向入口程序集，而 `light.xaml` / `dark.xaml` 搬家前就在 `PoEToolbox.App/Themes/`（`git show HEAD~1` 可查），搬的只是加载器。全仓已无 `Shared.ThemeManager` 残留引用。**剩下的视觉确认需要人开一次程序**：点主题按钮看 light/dark/跟随系统三态是否正常 |
| B4 | W7b（U1 新建 `PoEToolbox.Ui`） | 全量测试 + 搬家完整性自检 | 打开程序，逐插件看输出面板与状态栏是否正常 | ✅ 192 绿，0 警告 0 错误。4 个搬走的文件全部被 `git diff -M` 认成 rename（内容未改）；Shared/Abstractions/Ui 三个 dll 的 WPF 元数据计数见 §7.3。**UI 目视确认未执行**（需人开一次程序） |
| B4 | W8（U3 改名 + U2 拆契约） | 全量测试 + 差异比对 | 导航栏 10 项仍在、分组与顺序不变 | ✅ U3、U2 各测一次均 192 绿。U3 的差异比对：35 个改动行除 `IPlugin.cs` 删掉的一行自引用 `using` 外全是标识符本身。U2 的差异比对：11 个插件类各只 +1 行 `using` / 1 行基接口换名，`MainWindow` 只动 6 处类型与 1 处 `OfType`；`RegisterAll()` 注册的是 10 个插件（Voyager 未注册），10 个都已实现 `IUiPlugin`，所以 `OfType<IUiPlugin>()` 过滤后集合大小不变——**这是静态推断，不是目视确认**，开一次程序看导航栏仍与 W7a/W7b 的视觉确认合并成一次。`dotnet publish` 重测：单 exe 6,282,397 字节、无 loose 文件、`PoEToolbox.Ui.dll` 在包内 |

**全量基线**：Release **192 通过 / 0 失败**（改动前 176）。当时 Debug 184 通过 / **1 失败** = 既有 `DisposeWithoutSave_DoesNotPersistPendingMutation`（`Index.Dispose` 的 `Debug.Fail`）——**该条已于 C21 关闭**，现在两个配置都 195 全绿，见 §12 的 D4 一行。

### 9.1 补丁写入路径的本机实测（2026-09-20，本人同意后执行）

对象是本机 Steam 客户端的 `Bundles2\_.index.bin`（115,073,230 字节，SHA-256 前 8 位 `680a7dc1`）。每一步之后都重算整文件 SHA 并看 `PATCHED\` 的大小；下表所有 SHA 都只留前 8 位，不含任何机器可定位信息。

| # | 操作 | 索引 SHA / 字节 | 旁证 |
|---|---|---|---|
| 0 | 基线（Steam 完整性校验后的原版） | `680a7dc1` / 115,073,230 | 无 `backup\`、无 `PATCHED\` |
| 1 | 内置「手雷」补丁 apply（文本替换型） | `425477d7` / 120,667,626（+5.6 MB） | 新增 bundle 885 B；`backup\_.index.bin` 记下基线；账本 1 条 |
| 2 | 同一补丁再 apply 一次 | SHA 不变 | 幂等，不重复写 |
| 3 | revert | `e7153619` / 120,667,630 | bundle 885 → 990 B——**revert 之后文件不等于基线** |
| 4 | purge | — | 报「没有新增文件」，该补丁没有新增文件型 op，判定正确 |
| 5 | restore（彻底还原） | `680a7dc1` / 115,073,230 | 与基线**逐字节相同** |
| 6 | 内置「地面」补丁 apply（整包替换型） | `6aa406bc` / 122,403,351 | 10/10 条 op 成功，含 4 处 datc64 指针重定位（ROW[8307/8311/8312/8313] +8）；bundle 1,393,407 B；账本 1 条 |
| 7 | revert | `c696f876` | 替换型的文件副本按设计保留，bundle 涨到 2,786,283 B |
| 8 | purge | `9384098a` / 122,403,113（−235 B） | 只删无人引用的 bundle |
| 9 | cleanup | — | 报「没有孤儿补丁 Bundle」，正确：该 bundle 仍被引用 |
| 10 | restore | `680a7dc1` / 115,073,230 | 逐字节回到基线，`PATCHED\` 消失，`list` 两个补丁都回到 ☐ |

三条以前没写下来、且会影响后续判断的事实：

1. **`revert` 恢复的是语义不是字节**（第 3、7 步）。要逐字节回到原版只有 `restore`。UI 文案已经把「启用 / 还原 / 卸载 / 彻底还原游戏客户端」分开了，不用改；但文档和注释都不许再写「revert 可逆到原状」。
2. **打一个补丁会把 115 MB 的索引撑大 4.9%~6.4%**（第 1、6 步）。不是泄漏，是索引重写时新增记录与对齐的代价，但用户看到游戏目录凭空变大时会以为坏了。
3. **打过补丁的索引与 Steam 的记录不一致**，Steam 再做一次完整性校验就会把索引换回原版，而补丁账本还留着。所以三态判定读实际索引内容、不读账本是对的（W3 的性质测试正是为它服务），这条设计不许反过来「优化」成读账本。

**一个没能复现的分歧（开放项，不是结论）**：第一次验证用的是校验前的旧索引（mtime 早于同目录的 bundle 文件），`status` 报「未应用 / 原文存在」，而 `apply` 的预检对同一条 `edittext` 报「冲突：新旧文本同时缺失/存在」，于是中止且零写入——SHA、mtime、`backup\`、`PATCHED\` 四项都核过，确认盘没动。事后逐项排查：`edittext` 的判定与 `applying` 方向无关（`FxPatchState.ComputeStates` 只在 addfile 派生分支读 `applying`）、补丁 json 自 09-19 起未改、`Content`/`Folders` 无更新的文件——三条理论都没命中，而那块旧索引随后被 Steam 校验覆盖，再也取不到现场。**下次遇到同样组合，先复制一份 `_.index.bin` 留现场再排查**。

---

## 10. 实施顺序

```
B1 (半天) ✅           B2 (1天) ✅          B3 (1~2天) ✅ 全部完成       B4 ✅ 全部完成
┌──────────────┐     ┌──────────────┐    ┌──────────────────────┐    ┌────────────────┐
│ W1 回收下沉 ✅│────▶│ W3 性质测试 ✅│───▶│ S1 ✅  S1.5 ✅  S2 ✅ │───▶│ U3 改名    ✅  │
│ W4 超时 ✅    │     │ 9 条，三轮自检│    │ S3 ✅  S4 ✅  S5 ✅  │    │ U1 PoEToolbox.Ui ✅│
│ W5 异常兜底 ✅│     └──────────────┘    │ S6 ✅  S7 ✅  S8 ✅  │    │ U2 拆契约    ✅  │
│ W6 build.bat✅│                         │ W7a ThemeManager ✅   │    │ (= W7b + W8-b2) │
└──────────────┘                         └──────────────────────┘    └────────────────┘
```

SPEC 的**编码项已全部做完**（P2-2 架构文档、P2-5 可诊断性、ConfigService 注入缝也已完成，见 §12）。仍未关的只剩一类，见 §12：① 需要人开一次程序的目视确认（W7a 主题、W7b OutputPanel/状态栏、W8 导航 10 项）。已关的三类：② 补丁写入路径手工验证（本人同意后在真实 Steam 客户端上跑完 apply/revert/purge/restore 全链，见 §9 的写路径一行）；③ 三个拍板项全部落地——PRD **D3**（PRD/SPEC 移入 `docs/` 并入库，本文就是结果）、PRD **D5**（CI 加 Debug 测试腿，`7e07f81c5`）、**P1-5**（纯 Win32/GDI 下沉 `Core` + 热键契约倒置，`e21cd1059` + `5e73be5f2`）。PRD **D1/D4 已关闭**（`07fdfa49c` / `587f6a96a`）。

**硬约束**：`W3 完成` 是 `W2 开工` 的准入条件。先有网再拆引擎。（本轮已满足：S1 是在性质测试全绿之后才动的。）

---

## 10.5 提交切分方案 — ✅ 已执行（`cb7bc4791` → `77f830b81`：方案内代码提交 C1~C28，收尾后追加 C29~C32——文档回填、一项行为变更、一次界面布局调整）

工作区里堆着两轮的全部改动，未提交。回切成 6 个提交以恢复二分定位能力：

| # | 提交 | 文件 |
|---|---|---|
| C1 | W6 出包加 `-m:1` | `build.bat` |
| C2 | W5 全局异常兜底 | `App.xaml.cs`、`PoEToolbox.Cli/Program.cs` |
| C3 | W4 网络超时统一 | `NetworkDefaults.cs`(新)、`DatContainer.cs`、`PatchClient.cs`、`SchemaManager.cs` |
| C4 | W1 回收下沉 + 生命周期测试 | `MemoryReclaimer.cs`、`GameDataAccess.cs`、`GameDataLoader.cs`、`GameDataPathPreference.cs`、`IndexBackupService.cs`、`PatchBundleRepair.cs`、DataBrowser ×3、PriceTagger ×2、`GameDataDisposalTests.cs`(新)、`AffixColorSchemeTests.cs`、`AffixOfficialRestoreTests.cs` |
| C5 | W2 引擎拆分 S1~**S8** + W3 性质测试 | `Shared/Fx/` 全部 9 个新文件（含 S8 的 `FxPatchCommands.cs`）、`FxPatchEngine.cs`、`FxDiff.cs`、`TextEncodingDetector.cs`、`FxPatchView.xaml.cs`、`FxPatchPropertyTests.cs`(新)、`FxPatchBundleNamingTests.cs`、`FxPatchSecurityTests.cs`、`FxDiffPackagingTests.cs`、`FxRawPackPatchTests.cs`、`FxBuiltInPatchTests.cs` |
| C6 | docs | `docs/BUILT-IN-FX-PATCHES.txt`、`docs/REVIEW-software-engineering.md` |

**已知妥协**：`FxPatchEngine.cs` / `FxDiff.cs` / `FxPatchView.xaml.cs` 同时承载 W1 与 W2，按文件切不干净，故这三个文件里属于 W1 的那几处「删手工 Reclaim」随 C5 一起进。逐 hunk 强切的代价（每个中间提交单独验证可编译）不抵收益。

**依赖方向**：C4（下沉自动回收）先于 C5（删手工调用点）——反过来会让自动回收短暂失去配套。C1/C2/C3 与其余互相独立，顺序只是让机械改动先进。

**实际提交与可编译性**（`git worktree` 里逐个 checkout 后 build App + Cli + Tests，全部 0 错误 0 警告）：

| # | SHA | 提交标题 | 中间提交可编译 |
|---|---|---|---|
| C1 | `d6b3d46ba` | 构建：publish 加 `-m:1` | —（只动 `build.bat`） |
| C2 | `f154c507f` | 加固：App 与 CLI 挂上未观察异常兜底 | ✅ |
| C3 | `2327ae8c9` | 加固：对外 HTTP 调用统一加超时 | ✅ |
| C4 | `ddcedd5be` | 加固：内存回收下沉到 `GameDataAccess.Dispose` | ✅ |
| C5 | `fc8a84441` | 重构：拆分 1754 行的补丁引擎为门面 + 九个职责模块 | ✅（= 最终树，189 测试绿） |
| C6 | `21fac6601` | docs | —（只动文档） |
| C7 | `12bd84929` | 修复：物价抓取遇到超时按分类降级，并给出中文失败原因 | —（HEAD，192 测试绿） |

C7 不在原方案里：它是 §4.3 的后半段，按该节末尾的约定必须独立于 B1 的机械改动单独提交（行为变更）。

C5 之后 `FxPatchCommands.cs`（S8）也一并进了同一个提交——按「一步一提交」它本该独立，但 `FxPatchEngine.cs` 在 S1~S8 之间反复增删，逐 hunk 还原中间态的成本高于收益。

C8~C24 是后续几轮追加的，同样不在原方案里；C8~C12、C14、C16 每个提交后都跑过一次全量（C12 无行为分支变化，192 绿；C14 加了 3 条，195 绿；C16 是纯可测性重构，用例数不变仍 195 绿），C13/C15/C17/C18/C20 只动文档未重跑；C19 跑了 **4 次「强制重建 + 全量」全绿**（修复前同条件 3 次挂 3 次，见 §12）、C21 跑了 **Debug 与 Release 各一次全量，均 195 绿**、C22/C23/C24 只动文档（C23 单独 build 过 AffixWorkbench 插件，0 错误）：

| # | SHA | 提交标题 | 备注 |
|---|---|---|---|
| C8 | `69645d931` | 整理：ThemeManager 从 Shared 移到 App/Themes | W7a，见 §7.2 |
| C9 | `574404bd4` | 整理：PoEToolbox.Sdk 改名 PoEToolbox.Abstractions | W8-U3；`git diff -M` 全认成 rename |
| C10 | `c8cabcbd4` | 整理：新建 PoEToolbox.Ui，把 UI 关注点移出 Shared | W7b-U1 |
| C11 | `2463179fe` | 整理：IPlugin 契约去掉 CreateView，界面插件改实现 IUiPlugin | W8-U2；含 §0 修正 4 那个 `FrameworkReference` 补偿 |
| C12 | `9290f15a9` | 诊断性：空 catch 分流处理 | 报告 P2-5；分类结论见 §12 |
| C13 | `9398f1b20` | docs：新增 `docs/ARCHITECTURE.md` + README 项目结构重排 | 报告 P2-2；核实过程见 §12 首行 |
| C14 | `f83d2d7f6` | 可测性：ConfigService 数据根目录加测试注入缝 + 3 条分支测试 | §12 的「ConfigService 路径不可注入」；195 绿 |
| C15 | `5adb66ae0` | docs：清掉设计/指南文档里的失效定位 | 行号→符号、探针命令加⚠️、DESIGN 实施清单勾掉 |
| C16 | `e93260476` | 路径注入缝统一口径：一个根缝，不留快照例外 | §12 的「ConfigService 路径不可注入」两个边界清零；`AffixColorScheme.StorageDirectoryOverride` 删除 |
| C17 | `62816cf26` | docs：删重复的内置补丁 .txt + 纠正架构文档两条判定 | 报告 P2-7 关闭；「死掉的联网分支」判定被推翻，见 §12 |
| C18 | `9d4ddc619` | docs：体检报告补 §6.5 处置状态表 | 17 条逐项标状态；所引 13 个 SHA 逐个 `git log` 核过 |
| C19 | `34f50e849` | 修复测试偶发失败：测试程序集关并行 + 日志快照断言 | **C14/C16 注入缝的账**，见 §12「跨 collection 的并发」一行 |
| C20 | `82328cd91` | docs：体检报告的不变式计数跟到 10 条 | 一行更正 |
| C21 | `587f6a96a` | 修复：Debug 下唯一那条失败用例改为断言守卫本身 | **PRD D4 关闭**（用了第三种解法），见末行 |
| C22 | `b08b9445d` | docs：补不变式 11（`Debug.Fail` 类防线要断言不要跳过）+ 回填报告 P2-8 处置 | 配套 C21；计数一并跟到 11 条 |
| C23 | `07fdfa49c` | 文档化：AffixDataService 常驻索引连接是有意的 | **PRD D1 关闭**（注释即处置，不改生命周期），见 §12 |
| C24 | `f03d7b5de` | docs：报告里那条「待确认」改成「已确认为有意」 | 配套 C23，顺手去掉失效行号 |
| C25 | `e21cd1059` | P1-5 第一步：纯 Win32/GDI 的输入与屏幕网格类型从 BagCleaner 沉到 `Core` | 6 文件 `git mv`（rename 全部被识别）+ 约 50 处 using；两插件各加一条对 Core 的引用；195 绿 |
| C26 | `5e73be5f2` | P1-5 第二步：热键契约提到 Abstractions + 工厂倒置，删掉 `Voyager → BagCleaner` 工程引用 | **报告 P1-5 关闭**；ARCHITECTURE 立不变式 12（插件间不得互引）；插件之间已无直接依赖 |
| C27 | `7e07f81c5` | CI 加一条 Debug 测试腿 | **PRD D5 关闭**；Debug 腿专门跑 `#if DEBUG` 断言；ARCHITECTURE 不变式 11 的「CI 收不到」口径随之更正 |
| C28 | `060fb453b` | PRD/SPEC 定稿移入 `docs/` 并入库 + 三份文档互相指向写清 + `.gitignore` 通配换成两条锚定文件 | **PRD D3 关闭**；入库前逐行扫过机器相关信息（无绝对路径/账号/机器名） |
| C29 | `cd14c0c41` | 写路径实测结果回填（§9.1 + ARCHITECTURE 不变式 13）+ 用户要的「改动前后对比」文档 `docs/REVIEW-engineering-hardening-before-after.md` | 纯文档，无代码；两份基线头部随之下修到 `060fb453b`，实测在 C28 复测（Release 34s / Debug 34s，均 195 绿，publish 无 loose json） |
| C30 | `1f32e6e1d` | **行为变更（超出本 SPEC 范围，由主人直接要求）**：删掉游戏数据文件的自动探测，路径只来自用户亲手选过一次 | `PoeDetector` 少 105 行（注册表试探 + 硬编码安装目录全去）、`GameDataLoader.ResolvePath` 空路径改抛 `InvalidOperationException`、`GameDataPathPreference` 去掉 `GetOrDetect`；新增 3 条 `GameDataPathPreferenceTests`（**200 绿**，Release/Debug 各一次）；ARCHITECTURE 立不变式 14。理由见不变式 14 那行——静默猜中另一份客户端会把补丁和账本写错地方 |
| C31 | `77f830b81` | **界面布局（同样超出 SPEC 范围）**：游戏数据路径独占标题栏下方一整行、自动换行；主窗口默认 1220×800 → 1560×1000、最小 1280×800；导航栏 180 → 220 | 起因是「索引路径近 90 字符，在标题栏里被无声切掉」；只读文本框原来的 `ScrollToEnd()` 会滚到末尾只留半条路径，随换行一并删掉。纯 XAML + 一行 code-behind，**200 绿不变**；实际观感需主人开一次程序目视 |
| C32 | 本提交 | 文档口径刷新：PRD 追加 **D6（主题相关验收降级为不做）**、ARCHITECTURE 的 195 → 200、前后对照的不变式 13 → 14 条、本表补 C30~C32 | 纯文档；§9 与 §10.5 里既有的 195 是**当时**的实测快照，有意不改 |

---

## 11. 待办（本 SPEC 明确排除，但值得记下）

| 项 | 理由 |
|---|---|
| datc64 纯工具下沉到基础库 | 受「Shared 不能引用 Core」限制，本次只能留在 Shared/Fx/ |
| ~~`PoEToolbox.Ui` 类库（承接 `OutputPanel` / `UiStatus`）~~ | ✅ 已做（`c8cabcbd4`），见 §7.2。原理由「收益不抵新增一个项目的成本」在 PRD D2 选 b2 后不再成立——契约拆分必须先有这一层 |
| ~~18 处空 `catch {}` 加日志~~ | ✅ 已做（`9290f15a9`），见 §12 的 P2-5 行。**报告原方案「全部加 FileLogger」不成立**，实际按「是否会静默改变用户看到的结果」分流 |
| 59 处 `MessageBox.Show` 抽 `IDialogService` | 与 MVVM 渐进迁移同批做 |
| MVVM 渐进：新模块强制 ViewModel | 属开发规范，不属本次代码改动 |
| `IUiPlugin` 是否再拆（如 `ICreatableView` / 元数据分开） | 现在就拆是过度设计；等出现第一个「注册但不显示」的真实插件再说 |
| Cli 仍要桌面框架 | 改名 + 拆 UI 之后，**Cli 的编译闭包里已经没有 WPF**：`12bd84929` 时 `Shared.csproj` 有 `<UseWPF>`（CLI 只引用 `Core` 与 `LibDat2`，那行 WPF 是白拿的），现在没有，且 CLI 闭包不含 `PoEToolbox.Ui`。剩下的部分是硬的：`Core` 的 DDS 渲染用 GDI+（`System.Drawing.Bitmap`），而 CLI 的 TFM 本身是 `net10.0-windows`，所以 `Microsoft.WindowsDesktop.App` 仍然必须声明。要让 CLI 真正纯命令行，得先把 `DdsMapNumberRenderer` 那类 GDI+ 依赖隔离出去——独立议题 |

## 12. 本轮新增/更新的待办

| 项 | 来源 | 说明 |
|---|---|---|
| ~~P2-2 缺架构文档~~ | 报告 §风险清单 | ✅ 已做（`9398f1b20`）：`docs/ARCHITECTURE.md`（分层与三条规则 / 20 个工程清单 / 三条运行期数据流 / 不变式（现 13 条）/ 落盘位置 / 已知结构债），README「项目结构」同步重排（原树缺 4 个插件工程，也没有 W8 之后的 `Abstractions` 与 `Ui`）。**做法：只写机器可核对的条目，每条写完后回仓库指认**——由此改掉 5 处想当然：`IAppState` 在 `Shared/GameSessionState.cs` 不在 `Abstractions`；`oo2core.dll` 是 `EmbeddedResource` 启动时释放（不是随包外置）；路径注入的先例是 `AffixColorScheme.StorageDirectoryOverride`（该缝已在 C16 并入根缝、类型不再存在）；>1k 行的文件数已漂移，改成约数；依赖图用 ASCII 框在 CJK 宽度下对不齐，换成缩进列表。**本文不写行号**（教训见本节末「SPEC 自身的行号」一行） |
| ~~P2-5 空 `catch`~~ | 报告 §风险清单 | ✅ 已做（`9290f15a9`）。**实测口径与报告不同**：全仓 29 处，其中 10 处在 `tests/` 的 teardown 里（吞掉清理异常是对的），`src/` 下 19 处。逐处判定后分成两类——**补日志 4 处**（`ConfigService` 的 config 解析失败与插件段反序列化失败、`SchemaManager` 的 schema 下载失败、`FxPatchPackage` 的旧临时目录清理失败），**写明有意吞掉 15 处**（搜索/预览的取消 ×2、剪贴板 100ms 轮询、注册表逐个试探、进程退出竞态、WorkingSet 修剪、字体名非法已有视觉回显、`FileLogger` 自身 6 处、解压安全拒绝后的目录清理）。全加日志会让日志文件在正常使用下就被刷爆 |
| ~~`ConfigService` 的路径不可注入~~ | P2-5 实施 | ✅ 已做（`f83d2d7f6`）。加了 `internal static Func<string>? DataDirectoryOverride`，六个派生路径由 `static readonly` 字段改为即时求值属性（21 处调用点无需改动，字段→属性对读取端源码兼容）。P2-5 那两条 Warn 现在有了测试：损坏 → 返回空 + 原文件改名 `config.json.corrupt.<ts>` 可查 + `LastReadError` 有值；段形状不符 → 回落 `new T()` 并 Warn。**顺带修掉一处污染**：`FxBuiltInPatchTests` 原先真的往用户 AppData 写内置补丁描述，现在整类在临时目录跑；两类都动这条进程级静态，挂同一个 `ConfigPathTestCollection` 串行。**当时的两个边界已由 C16 清零**（`e93260476`）：① `SchemaManager.WorkDir`、`PriceTaggerView.PoeNinjaDir`/`WorkDir` 三处 `static readonly` 快照改成即时求值属性，现在跟着根缝走；② `AffixColorScheme.StorageDirectoryOverride` 整条删除，方案目录并入根缝，affix 两个测试类与 config、内置补丁三类同挂 `ConfigPathTestCollection`（声明收拢到 `AffixColorSchemeTests.cs` 一处）。缝的注释里现在只留**一个**例外：`FileLogger.App` 在首次被触碰时就按当时根目录建好句柄，之后改缝不影响它——要断言日志内容订阅 `FileLogger.EntryLogged`，不要去读日志文件。 |
| **注入缝的账：跨 collection 的并发把别的测试产物删了** | C14/C16 的回归 | ✅ 已修（`34f50e849`）。加完根缝后我只跑了「一次全量绿」就当收尾，实测是**偶发**：强制重建后连跑 3 次挂 3 次，每次挂的不是同一条（`FxDiffPackagingTests` 两次、`ConfigServiceTests` 一次），单跑任一条都绿。根因两条，都是缝带来的而非既有：① **xUnit 默认只串行化同一 collection 内的类，跨组仍并发**——`FxPatchPackage.ExtractZipPatch` 的解压根目录由 `ConfigService.PatchesDirectory` 派生，不在串行组里的 `FxDiffPackagingTests` 于是把补丁解压进了别的类正持有的临时树，对方 `Dispose` 的 `Directory.Delete(recursive)` 把它的产物一并删掉（表现就是 `File.Exists(extracted)` 为假）。上一轮我写「挂同一个 collection 就安全」，**漏了「谁间接读这条缝」根本数不过来**；② `FileLogger.EntryLogged` 是进程级静态事件，`ConfigServiceTests` 的处理器往 `List` 里加，而 `Assert.Contains` 正在枚举它 → `Collection was modified`。**取舍**：方案 A 是整个测试程序集 `DisableTestParallelization`，方案 B 是继续手工补 collection 归属清单。实测**并行 26~36s / 串行 34~37s**——这套测试是 I/O 受限的，并行几乎没换来时间，那 A 用一行属性换掉一张「漏一个就偶发挂」的清单。`[Collection]` 标注保留但降级为「谁共享哪个静态」的记录。教训写进 `docs/ARCHITECTURE.md` §4 不变式 9、10 |
| ~~**P1-5 的措辞要纠正**~~ | 报告 §风险清单 | ✅ 措辞已纠正**且这条已关闭**。报告写「`Voyager.csproj` → `BagCleaner.csproj`」像是误引，实测是**真依赖**：Voyager 有 8 处 `using PoEToolbox.Plugins.BagCleaner.*`。正确做法是跨插件下沉，不是删一行引用能了事的 |
| ~~**P1-5 下沉的可行性已摸清（但未做）**~~ | 上一条的下一步 | ✅ 已按摸清的方案做完（`e21cd1059` + `5e73be5f2`）。落地的形态：`NativeMethods`/`InputSimulator`/`IInputSimulator` 进 `Core/Input/`，`GridCalculator`/`FixedScreenGrid`/`GridConfig` 进 `Core/ScreenGrid/`（Core 已声明桌面框架引用，`System.Drawing` 放这里**不新增任何依赖边**，Shared 保持干净）；`HotkeyService` 是 WPF（`HwndSource.AddHook`）留在 BagCleaner，契约 `IHotkeyService`（补 `IsInitialized`、继承 `IDisposable`）与 `HotkeyRegistrationException` 提到 Abstractions，另加 `IHotkeyServiceFactory.Create(baseIdOffset)` 由消费者按偏移取实例。`Voyager → BagCleaner` 工程引用已删，插件之间不再有直接依赖，`docs/ARCHITECTURE.md` §4 据此立了不变式 12。**没走的第三条路**：`Ui → Core`（新增跨层边）、`Shared` 收 `System.Drawing`（把 W7b/W8 的成果退回去） |
| ~~文档里的失效定位（P2-2 的余波）~~ | 报告 §风险清单 | ✅ 已做（`5adb66ae0`）。ARCHITECTURE 立的「不写行号」规矩其他文档没跟上：`DESIGN-bundles2-storage.md` 引的 `Index.cs` / `FileRecord.cs` 行号在引擎拆分后全部对不上（逐个换成符号名，并回代码确认 `EnsureWriteBundle`/`GetBundleToWrite`/`Redirect`/`Serialize`/`FlushWriteBundle`/`DeleteBundle` 都还在）；`DESIGN-fx-patch-engine.md` §8 九条实施清单做完了还挂着未勾；`GUIDE-fx-isolation-modding.md` 教的 `datrow`/`datgrep` 是当年 `.scratch/` 一次性探针的命令，从未入库、本地已不存在——顶部加了「结论可用、命令不可用」的 ⚠️ 并指回可用路径。顺带改掉两处**内容**不准：`GetBundleToWrite` 不是「没钉扎就新建」（会先复用没涨过 `MaxBundleSize` 的自定义 bundle）、「绝不能往已有 bundle 塞东西」应为「原生 bundle」，否则与 §3 自相矛盾 |
| ~~W7b + W8（PRD D2 选 b2）~~ | §7.2 / §8 | ✅ 已落地：U3 改名 `574404bd4`、U1 新建 `PoEToolbox.Ui` `c8cabcbd4`、U2 拆契约 `2463179fe`，每步 192 绿。**报告 P1-6 至此关闭** |
| **一次开程序的目视确认** | §9 B3/B4 | 待看的是 **W7b 输出面板/状态栏**、**W8 导航栏 10 项**，再加 C31 的界面调整（游戏数据条独占整行 + 放大后的默认窗口尺寸）。**W7a 主题三态已按 PRD D6 移出验收范围**（主人判定性价比低、优先级低，主题代码保留但默认深色、切换按钮仍是隐藏态）。自动化测不到，起 GUI 属于交互验证，需本人做一次 |
| ~~W4 §4.3 调用点提示~~ | 实施时自行砍半 | ✅ 已补齐并单独提交：`NetworkDefaults.DescribeFailure` + `PoeNinjaFetcher` 超时/取消区分 + 3 条桩 handler 测试，见 §4.3 |
| ~~死掉的联网分支~~ | §4.3 核查 | ✅ **判定收回**（`62816cf26`）。这条写成了「要么删要么接，挂着最糟」，但没先问代码是谁的：`DatContainer.DownloadSchemaMin` 与 `PatchClient.UpdateNodeAsync` 都随 `src/Lib*` 从上游 vendored 进来，`git show 02ef81c47` （初版提交）里两个都已经在，是库对外的公共 API，**不是本项目的死代码**。本项目为了修索引空记录 bug 已经在 `LibBundle3/Index.cs` 带着注释动过上游，分歧额度要省着用——为「整洁」删上游 API 是净亏。真正剩下的风险只是下一个人拿它们当可用路径，所以处理方式从「删」改成在 `docs/ARCHITECTURE.md` §6 标注 |
| ~~报告 P2-7：`BUILT-IN-FX-PATCHES.txt` 与 .md 重复~~ | 报告 §风险清单 | ✅ 已删（`62816cf26`）。**这条是我在 C6 里做坏的**：报告当时写的是「未入库且与 .md 重复 → 删除 .txt」，我把两个文件一起提交了。重删前逐行比对确认 `.txt` 是 `.md` 的严格子集（10 个 op 一条不少），而且 `.md` 之后又更正过两轮（内嵌资源 + 释放到 AppData），`.txt` 停在旧口径——双份必然再次分叉。**顺带查清一个隐性陷阱**并写进 `docs/ARCHITECTURE.md` §5：`.gitignore` 忽略 `*.dll`、只给仓库根的 `oo2core.dll` 留 `!` 例外，所以源码树里多出一个 dll 永远不会出现在 `git status`（`src/PoEToolbox.Core/LibDat2.dll`，2026-07-27 的构建残留，无任何工程引用、`dotnet build` 移除后仍 0 错 0 警，已移到 `.scratch/` 备查而不是直接删——它没入库，删了找不回来） |
| ~~S1.5 提取数据模型~~ | §2.6 | ✅ 已完成，见 §2.1.1 |
| **PRD D4 已关闭，但用的是第三种解法** | 报告 P2-8 | ✅ 已做（`587f6a96a`）。D4 原方案只有两选项：(a) 改 `Index` 让 Debug 下不 `Fail`、(b) 给用例加 Debug 跳过条件，PRD 选 (b) 并写明理由「`Debug.Fail` 是有意的防线，不该为测试让步」——**这个二选一漏了第三条路**：`Debug.Fail` 在 .NET 里是走 `Trace.Listeners` 派发的，测试可以在自己这段窗口里换上自己的监听器接管它，于是 (b) 的两个代价都没有：既不用跳过（Debug/Release 跑同一套断言），也没为测试改产品防线，反而**第一次给守卫本身上了回归保护**（把 `Debug.Fail` 删掉 → 该用例红，已做破坏自检）。**前提**是这条接管窗口安全——`Trace.Listeners` 是进程级静态，靠的正是 C19 的整体关并行；重新开启并行就得给这类用例挂串行组。**遗留已清**：当时 CI 只跑 Release，`#if DEBUG` 里的这条断言在 CI 上等于不执行；PRD D5 已拍板加 Debug 测试腿并落地（`7e07f81c5`，见上两行） |
| ~~`patchptr-byid` 性质测试~~ | §3 | ✅ 已补 3 条，见 §3 |
| ~~拆分各步的提交切分~~ | §2.3 | ✅ 已执行，实际清单见 §10.5（不是原文写的 §13——那是个不存在的编号，已改）。三个跨 W1+W2 的文件按「已知妥协」随 C5 一起进，未逐 hunk 强切 |
| **测试隔离约定** | §1.6 | **自 C19 起整个测试程序集关并行**（`tests/.../AssemblyInfo.cs`），所以「忘了挂 collection」不再会偶发挂。`[Collection]` 标注保留，作用降级成「记录哪些类共享哪个静态」——当前有两组：`ConfigPathTestCollection`（路径注入缝，四个类）与 `GameDataTestCollection`（游戏数据/回收链，四个类）。若将来为跑速重新开启并行，必须先按这条把清单补全 |
| ~~**PRD D5：CI 要不要加一条 Debug 测试腿**~~ | D4 的遗留 | ✅ 已拍板「加」并落地（`7e07f81c5`）。实测 Debug **36s** / Release 34s，两条串跑约 70s，一次 CI 多花 36s 换 `#if DEBUG` 里的守卫断言（含 `CapturedDebugFail` 那条）真在 CI 上执行。落地形态不是 matrix：同一次 restore 后加一个 `Test (Debug)` 步骤，避免整条 job（checkout/setup/restore/publish/打包）复制一遍 |
| ~~**PRD D3：PRD/SPEC 是否入库**~~ | 本轮自决 | ✅ 已拍板「入库」并落地：两份定稿移到 `docs/`（原名不变，`docs/PRD-engineering-hardening.md` / `docs/SPEC-engineering-hardening.md`），`.gitignore` 的整条 `PRD-*.md`/`SPEC-*.md` 通配换成两条**锚在根目录**的具体文件（词缀上色那对属于已收口的旧特性，不在本轮范围）。同步把三份文档的互相指向写清：ARCHITECTURE = 现状、SPEC = 历史计划、PRD = 拍板记录，冲突以 ARCHITECTURE 为准。**入库前逐行扫过机器相关信息**：两份文档里没有任何客户端绝对路径、账号或机器名（本轮写路径验证的实测数字入文时同样只写「本机 Steam 客户端目录」）。剩余风险：以后新写的根目录草稿不再被自动忽略，会出现在 `git status` 里——这是有意的，草稿要么定稿入库要么删掉，不该长期隐形 |
| ~~PRD D1~~ | §1.1 | ✅ 已关闭（`07fdfa49c`）。**处置方式就是那条决策本身：不改生命周期，把它写清楚**——`AffixDataService._gd` 上加了注释，说明常驻到下次 Connect 或 Dispose 是有意的（列表要持续按行读 csd/uisettings，每次访问重开索引不现实），并写明重连前与 Dispose 时都会释放。回收问题早已由 W1 下沉解决，剩下的只是「下一个人会不会误判成漏回收」 |
| ~~补丁写入路径的本机手工验证~~ | §9 B3 三行 | ✅ 已做完（2026-09-20，本人同意后），见 §9.1：两个内置补丁各跑 apply → 幂等 re-apply → revert → purge → cleanup → restore 全链，每步重算整文件 SHA，最后逐字节回到基线。**产出了三条新事实**（revert 不承诺字节还原、打补丁会让索引撑大 4.9%~6.4%、打过补丁的索引与 Steam 记录不一致），已写进 `docs/ARCHITECTURE.md` 不变式 13。**留一个开放项**：校验前的旧索引上 `status` 与 `apply` 预检对同一条 `edittext` 判定不一致（未应用 / 冲突），三条理论逐一排查均未命中，那块索引随即被 Steam 校验覆盖无法复现——下次先留现场副本再看。 |
| ~~状态归约层的测试缺口~~ | S7 破坏自检 | 曾删掉 `OverallOf` 的参半分支后 8 条性质测试全绿。已补 P-4 `HalfAppliedPatch_OverallIsConflict`（用第二个补丁手工制造参半状态）封住：破坏该分支 → 红 1 / 绿 8，恰好只有新用例受影响 |
| **SPEC 自身的行号** | 全文 | 本文行号以 `cb7bc4791` 为准，**已全部失效**：`FxPatchEngine.cs` 从 1754 行缩到 211 行，逻辑分散到 `Shared/Fx/` 九个模块。引用本文行号前请以 C5 提交后的代码重新定位 |
