# 补丁创作方法论：改共享资源，不误伤他人

> 更新：2026-09-13　|　性质：**推荐做法**，不是强制规范
> 本文讲**通用的决策方法**。油弹补丁的完整实战复盘（含全部踩坑、探针用法）见
> [`GUIDE-fx-isolation-modding.md`](./GUIDE-fx-isolation-modding.md)。
> 需要先理解底层机制（为什么改文件必须 Redirect、PATCHED 为什么只增不减）看
> [`DESIGN-bundles2-storage.md`](./DESIGN-bundles2-storage.md)。

---

## 0. 一句话原则

> **改一个被多处引用的文件时，先问一句：顺带跟着一起变的那几处，我能不能接受？**
> 能接受 → 直接改原件，最快也最好维护。
> 不能接受 → 才轮到"别动原件——复制成副本，改副本，然后只把**你想影响的那一处引用**的指向改到副本上。"

就地改原件会让**所有**引用者一起变；改副本 + 只动一处引用，只有那一处会变。
两者不是优劣关系，是**成本与影响面的取舍**——隔离多一个文件、多一条要维护的指针。判据见 §2。

---

## 1. 为什么：用真实数据看一遍

同一个 `grd_Burning01.ao`，在两张表里的共享程度**完全不同**：

| 表（原版） | 引用 `Metadata/Effects/Spells/grd_Zones/grd_Burning01.ao` 的行数 |
|---|---|
| `data/balance/groundeffects.datc64`（377 行） | **368 行** ← 几乎全表 |
| `data/balance/miscanimated.datc64`（11,282 行） | **1 行**（ROW[8307] `BaseOilGroundBurningEffect`） |

**真正的共享在 `groundeffects`**：直接改原件会让整个燃烧地面家族（368 种地面参数变体）一起变。
而 `miscanimated` 只有一处，它是"哪个技能用哪个特效"的精确开关——**所以隔离从这里切入**。

| 做法 | 后果 |
|---|---|
| 直接改 `grd_burning01.ao` 的内容 | **368 处地面效果全部受影响** |
| 复制成 `grd_burning01_oil.ao` + 只改 miscanimated 黏油那一行 | 那 368 处继续用原件，**只有黏油的地面变** |

内置补丁用的就是后者：

```json
{ "op": "addfile-derived",
  "src": ".../grd_burning01.ao",
  "dst": ".../grd_burning01_oil.ao",
  "replace": [ { "old": "2 0 0 Linear 0.25 0 Linear", "new": "2 0 0 Linear 0.25 1 Linear", "count": 2 } ] },

{ "op": "patchptr-byid",
  "table": "data/balance/miscanimated.datc64",
  "id": "BaseOilGroundBurningEffect",
  "originalPath": ".../grd_Burning01.ao",
  "newPath":      ".../grd_Burning01_oil.ao" }
```

原件 `grd_burning01.ao` 一个字节都没动。

---

## 2. 决策表：什么时候隔离，什么时候就地改

核心问题只有一个：**改这个共享文件，会把改动带给它所有的引用者 —— 那些"我本来不打算动"的地方，你能不能接受？**

| 判据 | 做法 |
|---|---|
| 引用者恰好都是你想改的（例如同一个技能家族的全部变体） | ⭐ **直接改原文件**，最快也最简单 |
| 顺带受影响的那些引用，效果可以接受（雷同、无害、或本来就想一起生效） | **直接改原文件** |
| 只想影响其中一部分，多余的误伤不能接受 | 复制副本 + 只改那几处引用 |
| **没有任何引用者**（根资源，见 §3） | 只能就地改，别硬套隔离 |

**别为了套方法论而绕远路。** 隔离（建副本 + 改引用）是有成本的：多一个文件、多一条要维护的指针、
还原路径更长。如果直接改原文件就能解决、且顺带被影响的引用你不在乎，就直接改 —— 这是最先该试的做法。
只有在"改了会连带到我不想动的东西"时，才值得为隔离付出那份复杂度。

> 反面例子不是说隔离不好：`grd_Burning01.ao` 被 `groundeffects` 的 **368 行**引用，
> 直接改等于给 368 个地面效果一起换材质——那种就得隔离。
> 而 `crossbow_oilgrenade/oil_Burst.ao` 只有 `OilGrenadeExplosion` 一行引用，隔离毫无意义，直接改。

判断"被几处引用"的现成手段：探针的 `datrow <表> <路径子串>`（输出引用它的行号与字段偏移），
以及 `datgrep <表> <Id 关键词>`（按 Id 秒查）。完整用法见 `GUIDE-fx-isolation-modding.md` §6。

### 一个省力的判断：先看路径形状

GGG 自己就给每个变体准备了独立文件副本，所以**路径里写着归属的，通常是专属文件**：

| 路径形态 | 通常情况 |
|---|---|
| 带技能 / 皮肤 / 怪物目录，如 `…/crossbow_oilgrenade/oil_Burst.ao`、`…/Mercenary/Verisium/…/oil_Burst.ao` | 专属副本，一般只被 1 行引用 |
| 通用目录，如 `…/grd_Zones/grd_Burning01.ao` | 可能被几百行共享，**必须查过再动手** |

实测印证（原版 `miscanimated`，5 个都叫 `oil_Burst.ao`，**每个只被 1 行引用**）：

| 文件 | 引用它的 Id |
|---|---|
| `Metadata/Effects/Spells/crossbow_oilgrenade/oil_Burst.ao` | `OilGrenadeExplosion` |
| `…/Microtransactions/…/Verisium/crossbow_oilgrenade/oil_Burst.ao` | `OilGrenadeExplosionVerisium` |
| `…/Abyssal/…/oil_Burst.ao` | `OilGrenadeExplosionAbyssal` |
| `…/Divine/…/oil_Burst.ao` | `OilGrenadeExplosionDivine` |
| `…/monsters_effects/Act3_FOUR/VaalGuardBombThrower/oil_Burst.ao` | `VaalGuardOilGrenadeExplosion` |

**别踩的统计坑**：按文件名字串数"出现几次"会把不同目录的同名文件混在一起，得到虚高的共享数。
要看**完整路径**，并且数"有几行的指针指向它"，而不是数路径字符串出现了几次。

---

## 3. 根资源：隔离做不到的一类

引用关系是**向上递归**的：改 A → 复制 A' 并改引用者 B 的指向 → 那 B 也被改了 → 是否复制 B？……
一直往上，**终点一定是一个"没有任何文件引用它"的资源**。

这类资源是**引擎按固定路径硬加载**的，典型就是 `data/` 下的 dat 表和部分根特效定义。
它们没有引用者，所以复制成副本后**没人会去加载那个副本**——隔离在此失效。

实测确认（全库扫 1,240 张 datc64 + 独立复核，均为 0 引用者）：

- `data/balance/miscanimated.datc64`
- `metadata/effects/spells/crossbow_oilgrenade/oilground.ot`

**所以"每个被改的文件都复制一份"是做不到的。** 本方法论是"尽量隔离"，不是"全部隔离"；
补丁里留一两个就地改的根资源是正常且必要的。

---

## 4. 四种操作怎么选

| 目标 | 推荐 op | 说明 |
|---|---|---|
| 基于原件生成一个改过的副本 | `addfile-derived` | 只声明"替换规则"，不内嵌成品，客户端更新后仍能生成 |
| 把补丁包里的成品写进去 | `addfile-asset` | 需附带原版字节才能还原 |
| 把某个技能从 A 特效改成 B 特效 | `addfile-derived` + `patchptr-byid` | **隔离改法的标准组合**：先造 B，再改那一行的指针 |
| 改某个文本/特效文件的内容 | `edittext` | 就地改；**影响面 = 所有引用者** |

---

## 5. 版本号

`patch.json` 的 `version` 是**必填**的，它决定补丁写入哪个 bundle：
`PATCHED/<bundleName>_v<version>`。**改了补丁内容必须升 version**，否则新内容会被当成
"覆盖其他 Mod"而拒绝执行。详见 README「版本号与清理」小节。

---

## 6. 避坑清单

1. **引用要覆盖全**：一处资源被 N 条引用，只改其中几条 = 出现"改了一半生效"的诡异状态，
   比直接冲突更难排查。要么全覆盖，要么走隔离。
2. **`extends` 继承链**：`.ao`/`.ot` 里有 `extends "Metadata/Parent"`。改父资源的引用时，
   要考虑继承它的子文件是否也需要跟着改。
3. **共享基材动不得**：`art/particles/fxrigs/fx_start/fxrig.amd` 被 **4,143 处**引用
   （跨职业、跨章节、跨时装）。改它会波及全局，一定要走隔离。
4. **有些 dat 表按 NameHash 引用，不是按字符串**。用路径字符串去搜会一条都搜不到，
   得用 8 字节 hash 搜（探针的 `hashgrep`）。**搜不到不等于没人引用。**
5. **datc64 指针是相对偏移**：`abs = dataOffset - 8 + v`，而且要**逐字节扫描**（8 字节指针不一定 8 对齐）。
6. **blob 追加会劫持"指向旧 EOF 的空串指针"**：追加新字符串后必须全表扫一遍新指针值，
   把多出来的命中改指真正的空串。
7. **`.ot` 里的 `preload_animated_object` 只是预热，不决定渲染**。改它对视觉效果无效——
   真正的开关在 dat 表的行级指针。

---

## 7. 动手前 Checklist

1. [ ] 确认要改的文件**被谁引用、几处**（`datrow` / `datgrep`）
2. [ ] 把顺带会受影响的引用者列出来，问自己一句：**这些跟着一起变，我能接受吗？**
   - 能（或本来就想一起改）→ **就地改**，收工
   - 不能，且它是个能被指向的资源 → 复制副本 + 只改目标那一处引用
   - 不能，但它是根资源（没有引用者）→ 只能就地改，并在补丁说明里写明它是根、必须就地改
3. [ ] 只在少数明显的全局共享基材上犹豫也不必：见 §6 第 3 条（`fxrig.amd` 这类一律隔离）
5. [ ] 升 `version`
6. [ ] 应用后跑 `status` 确认状态，再用 `datrow` 复核引用关系
7. [ ] 进游戏目验，查 `logs\LatestClient.txt` 有没有 `Failed to create resource`

---

## 附：实测数据共享度（本机客户端，3,111,588 个文件）

两种口径别混：表内**行数**（同一张 dat 表里有几行指向它） vs 全库**文件数**（有多少个文件引用它）。

| 资源 | 口径 | 数量 | 结论 |
|---|---|---|---|
| `Metadata/Effects/Spells/grd_Zones/grd_Burning01.ao` | 表内行数（`groundeffects`，共 377 行） | **368** | 绝对共享，改它必须隔离 |
| 同上 | 表内行数（`miscanimated`，共 11,282 行） | 1 | 隔离的切入点 |
| `art/particles/fxrigs/fx_start/fxrig.amd` | 全库引用它的文件数 | 4,143 | 绝对共享，改它必须隔离 |
| 5 个 `oil_Burst.ao`（各在不同目录） | 表内行数 | **各 1** | 专属副本，互不干扰 |

以上数字均由原始字节反查得出（指针 `abs = dataOffset - 8 + v`），并用已知的
`ROW[8307] BaseOilGroundBurningEffect` 与 `groundeffects` 的 368 行做过交叉校验。

---

*相关文件：[`GUIDE-fx-isolation-modding.md`](./GUIDE-fx-isolation-modding.md)（实战复盘 + 探针手册）、
[`DESIGN-fx-patch-engine.md`](./DESIGN-fx-patch-engine.md)（引擎设计）、
[`PATCH-INTEGRITY-BACKUP-DEFERRED.md`](./PATCH-INTEGRITY-BACKUP-DEFERRED.md)（还原与清理语义）。*
