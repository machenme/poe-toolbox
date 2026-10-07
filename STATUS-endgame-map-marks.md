# 挖坟词缀（终局地图内容标记）— 阶段性总结与交接

> 2026-09-25 ｜ 状态：**暂停**，等一次人工目视确认颜色后决定 W2 去留
> 配套文档：`PRD-endgame-map-marks.md`（产品决策）、`SPEC-endgame-map-marks.md`（实施计划）
> 三份都是根目录草稿，已加 `.gitignore`，未入库。

## 一句话现状

可行性验证（W0）**已在真实 PoE2 客户端跑通**，文字部分的实施方案完全确定；**颜色部分卡在一个只能进游戏目视的实验上**，补丁已生成，等你应用后看一眼。

## 一、已完成

| 产物 | 位置 | 状态 |
|---|---|---|
| 产品需求 | `PRD-endgame-map-marks.md` | 决策 D1–D18，验收 A1–A17 |
| 实施计划 | `SPEC-endgame-map-marks.md` | W0/W1/W2 切分 + 验证矩阵 |
| **W0 闸门命令** | `src/PoEToolbox.Cli/Program.cs` → `probe-endgamemaps` | **已实现并跑通（PASS）**，Release 编译 0 警告 0 错误 |
| **A-5 实验命令** | `src/PoEToolbox.Cli/Program.cs` → `try-endgamemaps` | 已实现，补丁已生成，**未应用** |
| 实验补丁 | `%TEMP%\emprobe\patch\endgame-maps\endgame-maps.patch.json` | 待应用 |
| 导出的原始表 | `%TEMP%\emprobe\endgamemaps.en.datc64` / `endgamemaps.tc.datc64` | 备查 |

代码改动**只有** `src/PoEToolbox.Cli/Program.cs`（+393 行，两个只读/生成类命令）。
另有两个工作区改动 `MainWindow.xaml.cs` / `UpdateChecker.cs` 是**主人自己的**，我没碰。

## 二、实测数据（真实 Steam 客户端，别再重跑）

路径：`C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\Bundles2\_.index.bin`

| 项 | 实测值 |
|---|---|
| 表 | `data/balance/endgamemaps.datc64` 61,799 B（英文）+ `data/balance/traditional chinese/endgamemaps.datc64` 50,863 B（繁中） |
| 形状 | **173 行 / 29 列**，`RowLength=241` |
| 两份关系 | **结构完全一致、行号对齐**（都是全量表，不是差量表）⇒ 按客户端语言只改一份即可 |
| 目标列 | **列 25 `Unknown25`**，`Type=string` |
| 往返 | 50,863 → 51,559 B（+696，+1.4%），**字节不恒等**；但**语义全等**、**二次往返稳定**、**改一行其余 172×29 全不变** |

### ⚠️ 两个文本列，别改错

| 列 | 名字 | 非空 | 内容 | 目标 |
|---|---|---|---|---|
| 2 | `FlavourText` | **172** | `船身撞擊並碎裂在岸上。`（每张图一句风味描述） | ❌ |
| 25 | `Unknown25` | **20** | `閃光的未必是金……`（只有特殊图才有） | ✅ |

交叉验证：对方仓库 CSV 写的行号 144 = `墮落的起源……`，我方实测行号 144 同样是它 ⇒ 目标列确认。

### 20 条全文与行号（繁中，固定）

```
 47 閃光的未必是金……   48 近乎天堂。        57 好人一個……
 78 映照水域……         83 瘋狂酋長……        127 有點可疑……
141 圓環的終點……      142 最後倒下者……      143 飲星者……
144 墮落的起源……      151 沒東西可喝……      152 未知的遺跡……
153 至少很乾燥……      154 殞落群星……        155 無盡懸崖……
156 溫暖但危險……      157 荒涼又糟糕……      158 野性自由遊蕩……
159 冷如寒冰……        160 硫酸！
```

**内置替换表是 19 条（不是 18），全部命中；另有 1 条未匹配：`瘋狂酋長……`（行 83）**
⇒ 列表里应显示为「未匹配」，让用户自己补规则。

## 三、由此定下的硬约束

| # | 约束 | 来源 |
|---|---|---|
| C1 | 状态判定与「已生效跳过」**只比 `Unknown25` 文本**，绝不比整文件字节或 SHA | 往返字节不恒等，比字节会永远判成未应用 ⇒ 重复写盘、PATCHED 无限膨胀 |
| C2 | **没有任何一行需要改动时不产出 `FileChange`，不写盘** | 同上，避免空转每次把文件改大 696 B |
| C3 | 改的是**列 25**，不是列 2 | 见上表 |
| C4 | 实现时按「列序号 25 + `Type == string`」双校验，**不符即拒绝运行** | 列数会随 schema 版本变（内嵌 v7 是 27 列，运行时是 29 列） |

> D7「整表重编码」成立，**原预案「A-1 不通过改原地覆盖」已作废**（原地覆盖受字符串长度限制，更脆）。

## 四、环境坑（会反复踩，已写进记忆）

**沙箱写不了游戏目录**：Bash/PowerShell 跑在 `sandbox-center.exe` 下，策略是「允许新建文件、禁止修改既有文件」⇒ `fx-patch apply` 恒报 `Access to the path '...\_.index.bin' is denied`。

判据（别再往别的方向查）：
- 同目录**新建**文件：OK
- 同目录**覆盖已有**文件：DENIED
- 目录外覆盖已有：OK
- ACL：`GIGABYTE\imc Allow FullControl`（权限没问题）
- Steam 退出后重试：仍失败
- `dangerouslyDisableSandbox`：绕不过去

⇒ **要写游戏数据必须主人在自己的终端或 GUI 里跑。** 我探测时在 Bundles2 建的临时文件已清理。

附带教训：比较 dat 行值**必须用 `JsonSerializer.Serialize`**，数组列的值是 `List<object>`，`Equals` 比引用会报几百处假漂移（第一次跑就误报了 519 处）。

## 五、当前阻塞点：A-5 颜色语法

客户端该列 **0 条**含 `<` 与 `{` ⇒ 没有任何现成样本可证明颜色语法。只能应用后进游戏目视。

**待你执行的三步：**

1. 应用（GUI：特效补丁 → 应用自定义补丁 → 选那个 json；或自己终端跑 `fx-patch ... apply`）
2. 启动游戏，看地图列表里那 20 张图：前缀 `[前]` 应为**红色**，后缀 `[后]` 应为**绿色**
3. 看完 `fx-patch ... revert` 还原（或 GUI 卸载）

**三种结果对应三条路：**

| 结果 | 结论 | 动作 |
|---|---|---|
| 颜色正常显示 | A-5 成立 | W2 可做（颜色模块下沉 Ui + 分桶 + uisettings 注册） |
| 显示成 `[<red>{前}]` 字面文本 | dat 语法错 | 用 `try-endgamemaps` 的 `csd` 参数（`<red>{{前}}</red>`）再生成一份试 |
| 两种都不生效 | 该列不吃颜色标记 | **按主人授权砍掉 W2**，停在 W1（纯文字模板 + 改前/改后预览） |

## 六、恢复时从哪接

**不依赖颜色结论的部分（W1，可直接开工）：**
读表 → 列定位双校验 → 规则模板 `{原名}` → 改前/改后并排预览 → `AffixPatchBuilder` 生成补丁 → `FxEngineRunner` 应用 → 还原 → 内置 19 条 + 规则持久化。骨架照抄 `AffixWorkbenchView.Apply_Click`（:1047）/ `Revert_Click`（:1136）。

**关键接缝（已核，别重扫）：**
- `Shared/AffixPatchBuilder.cs`：`FileChange(GamePath, Original, Modified)`；**`BuildExport` 已接受自定义 patchId** ⇒ 实验不用改代码，但 W1 正式落地要参数化 `Build` 的 `const PatchId`
- `FxEngineRunner.RunAsync` 在 `Ui` 且是 `internal` ⇒ 新插件要加一行 `InternalsVisibleTo`
- 写盘前必须 `ReleaseFileLocks()`（Windows 拒绝替换有内存映射打开的文件）
- `Datc64File.FromBytes(bytes, "EndgameMaps", true, 2)` → 改 `Rows[i]["Unknown25"]` → `ToBytes()`
- 未命名列的键名规则 = `Unknown{序号}`（`SchemaManager.cs:125`）
- 插件 csproj = 四条 ProjectReference（Abstractions / Shared / Ui / Core）

**待拍板（未变）：**
Q-1 只写勾选项还是整套全写（默认：只写勾选项）
Q-2 简中客户端文案从哪来（默认：第一增量只保证繁中）
Q-3 模块显示名「挖坟词缀」还是「地图内容标记」
Q-5 预览是否支持同一行多色分段（默认：要）
