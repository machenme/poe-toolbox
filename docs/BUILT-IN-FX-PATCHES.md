# 内置特效补丁说明

本文档详细介绍工具内置的两个特效补丁（fx-patch 引擎 `FxPatchEngine.BuiltIns`，见 `src/PoEToolbox.Shared/FxPatchEngine.cs:48-51`）各自修改了哪些文件、哪些内容。补丁定义文件位于 `src/PoEToolbox.Shared/Patches/`（编译为程序集内嵌资源，运行时释放到工具箱数据目录 `AppData\Roaming\PoEToolbox\patches\builtin\` 读取；exe 旁不再需要 `Patches` 目录）。

## 为什么要「复制副本 + 改引用」，而不是直接替换游戏文件？

打个比方：游戏里很多特效文件是**好几样东西共用的**。比如那个燃烧地面的文件，不止黏油榴弹在用——炸药榴弹、瓦尔守卫等技能也在用同一个文件。

如果直接把这个文件改掉：

- **会误伤别的技能**：你以为只改了黏油榴弹，结果炸药榴弹、瓦尔守卫的特效也跟着变了，而且你根本没打算动它们。
- **还原不干净**：改回去依赖「记得当初改了什么」，一旦记错、或者游戏更新动了这个文件，就还原不回原样，留下永久性的怪特效。
- **跟其他 Mod 打架**：别的补丁也改这个文件的话，后装的会覆盖先装的，谁开谁关都会把对方弄坏。

所以本工具的做法是：**原版文件一个字节都不碰**。复制一份出来单独改，再把黏油榴弹的引用指到这份副本上。这样：

1. 只有黏油榴弹变，其他技能原样不动。
2. 随时可以一键还原——只要把引用指回原文件就行，原版从来就没被改过，怎么还都干净。
3. 别的 Mod 改的是别的文件（或再复制一份副本），各改各的，互不干扰。

---

## 共通机制

| 项目 | 说明 |
|---|---|
| 修改方式 | 不写原生 bundle，全部落盘到索引同目录 `PATCHED/<bundleName>_v<version>.bundle.bin`，索引引用重定向 |
| op 类型 | `addfile-derived`（读原件+替换生成独立副本）、`patchptr-byid`（dat 表按 Id 重定向指针）、`edittext`（就地文本替换） |
| 还原 | revert 自动取反：副本保留但无引用、指针回原路径、文本反向替换；「彻底还原」则连 PATCHED 一并清除 |
| 副本原则 | 改共享资源一律复制副本、只改目标那一处引用，原版文件与其他技能的引用完全不动 |
| 叠加 | 状态判定基于内容（`ComputeState`），与第三方 Mod 天然栈式共栖 |

---

## 1. 地面燃烧特效（oil-ground-fx-lite）

**作用对象**：黏油榴弹留下的燃烧地面。两个效果：

1. 燃烧地面换成调淡的独立副本（燃烧减弱）。
2. 去掉油面点燃瞬间的过渡动画（火焰波粒子 + 点燃音效）。

**落盘 bundle**：`PATCHED/OilGround_v2.bundle.bin`

### 修改清单

#### A. 燃烧地面调淡

| # | op | 文件 | 内容 |
|---|---|---|---|
| 1 | addfile-derived | `metadata/effects/spells/grd_zones/grd_burning01.ao` → 生成 `grd_burning01_oil.ao` | 2 处 `Linear 0.25 0 Linear` → `Linear 0.25 1 Linear`（调淡透明度参数） |
| 2 | patchptr-byid | `data/balance/miscanimated.datc64`，Id=`BaseOilGroundBurningEffect` | 指针从 `grd_Burning01.ao` 重定向到 `grd_Burning01_oil.ao` |
| 3 | edittext | `metadata/effects/spells/crossbow_oilgrenade/oilground.ot` | `preload_animated_object` 从 `grd_Burning01.ao` 改指 `_oil.ao` |

#### B. 去掉点燃过渡动画（v2 新增）

为 Base / Abyssal / Divine 三个点燃过渡各做一个「只保留骨架」的独立副本（删除粒子事件与音效事件，即事件列表清空为 `"events": []`），再把引用重定向到副本：

| # | op | 源文件 → 副本 | 被删除的事件 |
|---|---|---|---|
| 4 | addfile-derived | `metadata/effects/spells/grd_zones/transition_FIRE.ao` → `transition_FIRE_oil.ao` | ① 粒子 `FX/transitionWave_FIRE_01.pet`（火焰波）② 音效 `OilIgnite` |
| 5 | patchptr-byid | `miscanimated.datc64`，Id=`BaseOilGroundIgnitionTransition` | 指针重定向到 `transition_FIRE_oil.ao` |
| 6 | addfile-derived | `metadata/effects/microtransactions/spells/mercenary/abyssal/crossbow_explosivegrenade/transition_FIRE.ao` → `grd_zones/transition_FIRE_oil_abyssal.ao` | 同上两类事件（Abyssal 皮肤版路径） |
| 7 | patchptr-byid | `miscanimated.datc64`，Id=`AbyssalOilGroundIgnitionTransition` | 指针重定向到 `_oil_abyssal.ao` |
| 8 | addfile-derived | `metadata/effects/microtransactions/spells/mercenary/divine/crossbow_explosivegrenade/transition_FIRE.ao` → `grd_zones/transition_FIRE_oil_divine.ao` | 同上两类事件（Divine 皮肤版路径） |
| 9 | patchptr-byid | `miscanimated.datc64`，Id=`DivineOilGroundIgnitionTransition` | 指针重定向到 `_oil_divine.ao` |
| 10 | edittext | `metadata/effects/spells/crossbow_oilgrenade/oilground.ot` | `preload_animated_object` 从 `transition_FIRE.ao` 改指 `transition_FIRE_oil.ao` |

**不动的东西**：共享的原版 `grd_Burning01.ao`、`transition_FIRE.ao` 本体，以及其他技能（炸药榴弹、瓦尔守卫等）对它们的引用。

---

## 2. 黏油榴弹特效（oil-grenade-fx-lite）

**作用对象**：黏油榴弹落地瞬间。去掉落地的油花粒子。

**落盘 bundle**：`PATCHED/OilGrenade_v2.bundle.bin`

### 修改清单

| # | op | 文件 | 内容 |
|---|---|---|---|
| 1 | edittext | `metadata/effects/spells/crossbow_oilgrenade/oil_burst.ao` | 删除 t=0 的粒子事件（`FX/oilSpill.pet` 油花，bone_group=`FX_line`），事件列表清空为 `"events": []` |

仅此一处，就地文本替换，不生成副本。

---

## 补丁来源与拆分说明

- 两个补丁原先是一个合一补丁，后拆分：燃烧地面调整归 `oil-ground-fx-lite`，油花粒子归 `oil-grenade-fx-lite`，可独立启停、栈式叠加。
- 拆分改了补丁内容，version 从 1 升到 2（改补丁内容必须升 version，否则新内容会被当成覆盖其他 Mod 而拒绝执行）。
- 副本文件带 `targetSha256` 校验，源文件与目标内容不符时拒绝套用，防止游戏版本更新后改错地方。

## 操作方式

- GUI：「特效补丁」模块（内置多选 + 启停 + 日志）。
- CLI：
  - `fx-oilmod <game-data> <patch-id|all> <status|list|apply|revert|cleanup|purge>`
  - `fx-patch <game-data> <patch.json> <action>`（自定义补丁通道）
- 彻底还原官方客户端：`fx-oilmod <game-data> all restore`（GUI 红色按钮）。
