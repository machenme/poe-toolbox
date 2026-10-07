# DEBUG：词缀补丁指向不存在的 bin 文件（恢复默认游戏数据之后）

- 日期：2026-09-16
- 状态：根因已定位，修复进行中（见文末清单）
- 症状：

  ```
  [错误] Could not find a part of the path
  'C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\Bundles2\PATCHED\affix-workbench_v12.bundle.bin'.
  ```

  「总是容易」复现：连接词缀上色 / 点应用时报错，重连也修不好。

---

## 1. 背景机制（阅读前置）

| 概念 | 位置 | 作用 |
|---|---|---|
| 索引 | `Bundles2/_.index.bin` | 记录每个游戏文件落在哪个 bundle、偏移、大小 |
| 补丁 bundle | `Bundles2/PATCHED/affix-workbench_vN.bundle.bin` | 词缀上色应用时，被改过的 csd / uisettings.xml 重定向进这里；每次重新生成 Version+1（v12 = 第 12 次应用），旧 bundle 腾空后由 `Index.Save()` 孤儿清理删除 |
| 原版基线 | `Bundles2/backup/_.index.bin` | 首次改动前留存的官方索引；一切「恢复原版」都整体回写它 |
| 补丁账本 | 工具数据目录 json | 记录打了哪些补丁，仅供界面展示 |

- **悬空（dangling）**：索引仍引用 `PATCHED/x.bundle.bin`，但物理文件已不存在。此后读这些文件 → `FileNotFoundException`（文件级丢失）或 `DirectoryNotFoundException`（整个 PATCHED 目录被删，即本例报错「找不到路径的一部分」）。
- 应用链路：`AffixWorkbenchView.Apply_Click` → `AffixPatchBuilder.Build`（生成补丁 json + assets，Version 递增）→ `FxPatchEngine.CmdApplyOrRevert(apply)`（`PinnedWriteBundlePath = PATCHED/affix-workbench_vN`，文件 Redirect 进新 bundle，`Index.Save()` 落盘并删空旧 bundle）。

## 2. 故障链（四个环节互相咬合）

### 环节 A —— 基线被污染（根本原因）

`IndexBackupService` 有两个口子会把「已打补丁的索引」固化成所谓「原版基线」：

1. **`Begin()` 无条件补建基线**（`FxPatchEngine` 每次 apply/revert 都调）：
   基线文件缺失时（用户清理过 `backup/` 目录、磁盘清理工具、旧版本残留布局），
   直接用**当前索引**创建基线。若此时补丁已应用，基线从此指向 `PATCHED/affix-workbench_v12.bundle.bin`。
2. **`RefreshBaselineIfStale()` 的 IsClientClean 只看磁盘证据**（选游戏数据时调用）：
   判定「干净」= 账本 json 为空 且 `PATCHED/` 目录里没有 `*.bundle.bin`。
   悬空状态（PATCHED 目录被删、索引仍引用）下这两个条件都可能满足 → 把悬空索引刷成基线。

### 环节 B —— 「恢复默认游戏数据」删文件、不修索引

用户视角的「恢复默认游戏数据」对应多个入口，都会删掉 `PATCHED/` 物理文件：

- Steam 启动器「验证/修复游戏文件」（外部，删除全部非官方文件）；
- 价格标签页「还原游戏数据」（`PriceTaggerView.Restore_Click`：只回写基线，不清理孤儿 PATCHED）；
- 特效补丁页 / 主窗口「恢复游戏原版」（`FxPatchEngine.CmdRestoreBaselineFull`：回写基线 + 删 PATCHED 目录全部文件）。

若基线是干净的，回写后索引与磁盘一致，没有问题；**若基线被污染（环节 A），回写出来的索引仍引用 v12 bundle，而该文件随即被删 → 必然悬空**。
另：`CmdRestoreBaselineFull` 在基线缺失时直接抛英文 `FileNotFoundException`，无指引。

### 环节 C —— 假修复死循环（「总是修不好」的直接机制）

连接时的自愈逻辑（上个提交 10d36396c 引入）：`AffixDataService.ConnectCore`
捕获丢失 bundle 异常 → `PatchBundleRepair.RepairIfBroken` 按基线把文件 `Redirect` 回原位置。

但基线被污染时，基线里这些文件的「原位置」同样是 `PATCHED/affix-workbench_v12`：

- 修复把文件重定向回**同一个丢失的 bundle**，报告「已修复 N 个文件」；
- bundle 里仍有文件记录 → 孤儿清理不删它 → 悬空原样保留；
- 每次重连重复同一轮假修复，永不收敛。这就是「总是容易指向一个不存在的 bin」。

### 环节 D —— 连接后失效无自愈、报错不可读

- `Apply_Click` 的 `VerifyUnchanged()` 直接用连接时的内存索引读文件；连接期间 PATCHED 被删时，把英文 `DirectoryNotFoundException` 原样甩给界面（即用户看到的报错），没有走修复路径。

## 3. 修复方案（对应环节逐一封堵）

| # | 环节 | 文件 | 改动 |
|---|---|---|---|
| 1 | C | `PatchBundleRepair.cs` | 构建 `danglingPaths` 悬空集合；重定向目标也在集合内 = 基线被污染，视为不可恢复（`poisonedBaselineFiles` 计数），**不再假修复**；日志区分「补丁新增文件无原版」与「基线被污染」，后者指引用启动器「验证/修复游戏文件」。另加 GGPK 守卫（非 `_.index.bin` 直接返回 0，避免空转全量加载） |
| 2 | C/D | `AffixDataService.cs` | `ConnectCore` 修复失败/重连再失败时抛中文 `InvalidOperationException`（成因 + 出路），不再透传英文 IO 异常；`IsMissingPatchBundle` 改 `internal` 供视图复用 |
| 3 | D | `AffixWorkbenchView.xaml.cs` | `Apply_Click` 捕获 `VerifyUnchanged` 的丢失 bundle 异常 → 新增 `RecoverBrokenPatchBundleAsync`：释放句柄 → `RepairIfBroken` → 重新连接，提示重按「应用」；修不动时给下一步指引 |
| 4 | A | `IndexBackupService.cs` | `Begin()`：索引含任何 `PATCHED/` bundle 引用时**拒绝创建基线**（宁缺毋滥），Warn 日志说明原因与出路 |
| 5 | A | `IndexBackupService.cs` | `RefreshBaselineIfStale()`：新增 `IndexHasNoPatchBundleRecords`（开只读索引查 bundle 记录），悬空状态下不刷新基线 |
| 6 | B | `FxPatchEngine.cs` | `CmdRestoreBaselineFull`：基线缺失时优雅中止（中文提示用启动器验证，退出码 1），不再抛英文异常；恢复后调 `RepairIfBroken` 自检悬空引用（与 `RawRevert` 同口径） |
| 7 | B | `PriceTaggerView.xaml.cs` | 「还原游戏数据」后：`CleanupOrphanCustomBundles` 清理无引用 PATCHED 文件 + `RepairIfBroken` 自检悬空引用 |

## 4. 测试

新增 `tests/PoEToolbox.Tests/PatchBundleRepairTests.cs`（SeedIndex 造最小 Bundles2 → 引擎 apply → 删 PATCHED 模拟恢复默认）：

- `CleanBaseline_RestoresRedirectedFilesAndDropsDanglingBundle`：干净基线下自动修复有效（防回归）；
- `PoisonedBaseline_DoesNotFakeRepair`：污染基线不再假修复（本 bug 核心回归测试）；
- `Begin_RefusesToBaselineAPatchedIndex`：`Begin` 拒绝把已打补丁的索引固化为基线。

## 5. 用户侧自救指引（发版说明可用）

已处于悬空状态的用户：

1. 退出游戏；
2. Steam 启动器 →「验证游戏文件的完整性」恢复官方索引（或删除 `Bundles2/_.index.bin` 与 `PATCHED/` 后验证）；
3. 删除 `Bundles2/backup/` 目录（丢弃污染基线）；
4. 重新打开工具箱选择游戏数据 → 干净索引会自动重建基线 → 重新应用词缀补丁。

## 6. 进度

- [x] 环节 A~D 代码修复（上表 #1~#7）
- [x] 回归测试编写
- [x] `DriveBundleFactory.DeleteBundle` 对「文件已不存在」的容错确认：文件不存在返回 false（DriveBundleFactory.cs:37-38），GGPK 工厂同理（GGPKBundleFactory.cs:38-39），孤儿清理路径安全
- [x] `dotnet build` + `dotnet test` 全量验证（Release，139/139 通过）
- [x] CHANGELOG 更新（v0.2.1 条目）

## 7. 测试过程中挖出的第二个 bug（已修）

首跑测试 `CleanBaseline_RestoresRedirectedFilesAndDropsDanglingBundle` 失败：修复后文件引用已归位，
但腾空的 `PATCHED/affix-workbench_v1.bundle.bin` 空记录仍留在索引里。

- 根因：`Index.Save()` 的孤儿清理里 `_Bundles.RemoveAt(bundleIndex)`（Index.cs:629）把
  SystemExtensions 的 `Array.RemoveAt` 当 void 用——该扩展是**函数式的，返回新数组、不改原数组**
  （非 ref，返回值被丢弃）。`CustomBundles` 真移除了、物理文件也删了，但 `_Bundles` 数组从未缩短，
  空 bundle 记录每次 Save 都原样写回索引 → 随每次应用/还原无限堆积（也是 v12 的推手之一）。
- 修复：`_Bundles = _Bundles.RemoveAt(bundleIndex);`（Index.cs:629-634，带修订注释）。
- 历史残留：加载时 `IsCustomBundlePath` 会把残留空记录重新加进 `CustomBundles`（Index.cs:507），
  因此修复后的**下一次任意 Save 会自动清掉历史空记录**，无需迁移脚本。
- 排查过全项目其余 `RemoveAt` 调用点：GGPK.cs / FreeRecord.cs 收者均为 List，DirectoryRecord.cs:327-328
  是正确用法（带赋值），无同类误用。
