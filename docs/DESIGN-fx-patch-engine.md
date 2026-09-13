# 设计文档：PoEToolbox 特效补丁引擎（fx-oilmod → 通用补丁系统）

> 状态：设计稿，待评审
> 日期：2026-09-13
> 前置：油弹特效隔离修改已实战验证（见 `GUIDE-fx-isolation-modding.md`）

---

## 1. 背景与动机

油弹特效削弱已在开发机上手工完成（地面火淡出 + 油花喷射删除），验证有效。现在的问题是**如何把成果分享给其他玩家**。三种分发形态的演进与淘汰：

| 形态 | 传输量 | 叠加性 | 结论 |
|---|---|---|---|
| ~~发改好的 `_.index.bin`~~ | ~84 MB | ❌ 排他：覆盖式互相踢，与官方更新/其他 mod 全冲突 | 淘汰 |
| ~~发"index.bin + 修改的 Tiny bundle"（传统补丁形态）~~ | ~84 MB + bundle | ❌ 同上 | 淘汰 |
| ✅ **补丁引擎内置在 PoEToolbox + 只发补丁包** | **几 KB** | ✅ 指令式就地修改，可叠加 | 本设计 |

核心洞察：`_.index.bin` 是每台机器/每个版本独立的全局单例，"发结果"必然排他；**"发指令、在对方 index 上就地执行"才可分发、可叠加**。而打补丁能力内置到 PoEToolbox 后，分发物只剩补丁包（几 KB），且形成推广飞轮：用户为打补丁安装 toolbox，顺带接触其他功能。

## 2. 目标与非目标

**目标（v1）**：
- `PoEToolbox.Cli` 新增 `fx-oilmod apply|revert|status` 三命令，一键应用/还原油弹特效补丁
- 按 Id 动态定位，不硬编码行号（跨版本/跨机器稳定）
- 幂等（重复执行安全）、自动备份（IndexBackupService）、固定独立 bundle

**目标（v2，本期只做接口预留）**：
- 补丁逻辑泛化为声明式补丁包格式（`patch.json` + 资源），任何特效 mod = 一个补丁包，不改程序

**非目标**：
- 不做 .pet 粒子文件编辑（二进制无工具链）
- 不保证跨客户端形态（不同发行渠道的内容可能有差异，未验证）
- 不做 App UI（v2 之后再说）

## 3. 实测支撑（设计依据，全部已验证）

| 事实 | 来源 |
|---|---|
| `Index.AddFile` 新增路径游戏端可解析加载 | 垃圾标记实验：日志 WARMUP CRIT 命中新增路径 |
| miscanimated.datc64 的 schema 解析正常（Id/AOFile 可读） | datgrep 实测 |
| AOFile 指针对 `BaseOilGroundBurningEffect`（地面火）是**渲染开关** | 游戏内验证生效 |
| AOFile 指针对 `*IgnitionTransition*`（点燃过渡）**可能只是注册** | 改指针后视觉无效，待垃圾实验实锤 |
| `oil_burst.ao` 为油弹专属文件，就地改内容天然隔离 | miscanimated 引用分析 |
| datc64 指针公式：`abs = v + dataOffset - 8` | patchptr 实测 |
| 循环动画 + `view_end=0` 会卡死；`view_end` 对 0 时刻事件型特效无视觉作用 | 用户实验 |
| 每个 mod 固定独立 bundle 可用 | `Index.PinnedWriteBundlePath` 已实现 |

## 4. v1 设计：fx-oilmod 命令

### 4.1 命令接口

```
PoEToolbox.Cli fx-oilmod <game-data> status    # 显示每个操作的当前三态
PoEToolbox.Cli fx-oilmod <game-data> apply     # 应用补丁（幂等，已应用则跳过）
PoEToolbox.Cli fx-oilmod <game-data> revert    # 还原到原版状态
```

`<game-data>` 沿用 `GameDataAccess.ResolvePath`（支持目录 / Content.ggpk / _.index.bin 自动探测）。

### 4.2 补丁操作清单（v1 内置）

| # | 操作 | 定位方式 | 修改内容 |
|---|---|---|---|
| 1 | AddFile | 新路径 `…/grd_zones/grd_burning01_oil.ao` | 内容 = 原件读取后替换 loop 前 2 处 `2 0 0 Linear 0.25 0` → `…0.25 1`（在工具内动态生成，不存成品） |
| 2 | patchptr | miscanimated 中 `Id = BaseOilGroundBurningEffect` 的行，AOFile 字段 | 指针 → 指向 `…/grd_Burning01_oil.ao` 字符串 |
| 3 | edittext | `…/crossbow_oilgrenade/oilground.ot` | preload 引用 `grd_Burning01.ao` → `grd_Burning01_oil.ao` |
| 4 | edittext | `…/crossbow_oilgrenade/oil_burst.ao` | 删除 `oilSpill.pet` 粒子事件（`"events": […]` → `"events": []`） |

**实验性操作（v1 默认关闭，`--with-transition` 开启）**：

| # | 操作 | 说明 |
|---|---|---|
| 5 | AddFile + patchptr | `transition_FIRE_oil.ao`（删火波事件）+ `BaseOilGroundIgnitionTransitionEffect` 行指针 —— **实测改指针后视觉无效**（该链路可能非渲染开关），保留代码待垃圾标记实验实锤 |

### 4.3 按 Id 定位算法（核心，替代硬编码行号）

```
1. 读 miscanimated.datc64 字节
2. 定位 blob：扫描分隔符 0xBB×8；dataOffset = 分隔符位置 + 8
3. 找目标 Id 字符串：blob 内按 UTF-16LE 字节精确匹配 "BaseOilGroundBurningEffect"
4. 找引用行：扫描行区每个字节偏移的 int64 v，abs = v + dataOffset - 8，
   abs 解码出的字符串 == 目标 Id（精确相等，非 Contains）→ 得到 (row, fieldOffset)
5. AOFile 字段 = 该行内"当前解码 == 原始 AOFile 路径"的那个指针字段
   （如多字段歧义，用 schema 列偏移交叉验证）
```

三态判定（每个操作独立）：

| 状态 | 判据 | apply 行为 | revert 行为 |
|---|---|---|---|
| 未应用 | AOFile = 原始路径 | 执行 | 跳过 |
| 已应用 | AOFile = 补丁路径 | 跳过 | 还原为原始路径 |
| 冲突 | 其他值 | **报错并中止**（不硬改，提示可能与其他 mod 冲突） | 同左 |

### 4.4 存储与隔离（v1.1 修订：PATCHED 目录约定）

- **PATCHED 目录约定**：所有补丁 bundle 统一写入游戏索引（`_.index.bin`，或 GGPK 解包出的 index.bin）**同目录**下的 `PATCHED\` 文件夹，命名 = `<技能名>_<yyyyMMdd-HHmmss>.bin`（如 `PATCHED/OilGrenade_20260912-071345.bin`）。
  - 依据：索引内 bundle 路径本就是相对路径且支持子目录（现有 `LibGGPK3/0.bundle.bin` 游戏端正常加载），`PATCHED/` 子目录同理可用；
  - 好处：一个补丁 = 一个可识别文件，按技能名+时间一目了然；重放 apply 会生成新时间戳 bundle，旧 bundle 即成为无引用垃圾，可整目录或按名清理；
  - 工具实现：`Index.PinnedWriteBundlePath = "PATCHED/<名>_<时间戳>.bin"`（本补丁全部写入落在该 bundle，与其他 mod / 官方数据物理隔离）。
- **备份**：apply/revert 前走 `IndexBackupService.Begin/Complete`（baseline 快照，现有设施）。
- **副本内容来源**：v1 由工具动态生成（读原件 → 文本替换），不内嵌成品字节 → 对同版本客户端天然适配，且避免"内容过期"问题。

## 5. v2 展望：通用补丁包格式（本期仅预留思路）

```json
{
  "patchId": "oil-grenade-fx-lite",
  "targetTables": ["data/balance/miscanimated.datc64"],
  "operations": [
    { "op": "addfile-derived", "src": "…/grd_burning01.ao", "dst": "…/grd_burning01_oil.ao",
      "replace": [ { "old": "2 0 0 Linear 0.25 0", "new": "2 0 0 Linear 0.25 1", "count": 2 } ] },
    { "op": "patchptr-byid", "table": "…/miscanimated.datc64",
      "id": "BaseOilGroundBurningEffect", "newPath": "…/grd_Burning01_oil.ao",
      "expectOriginal": "…/grd_Burning01.ao" },
    { "op": "edittext", "path": "…/oil_burst.ao", "jsonPointer": "ParticleEffects.events", "new": "[]" }
  ]
}
```

- 补丁包 = `patch.json` + 资源文件，zip 几 KB，社区任何人可制作
- 引擎四种 op（addfile-derived / patchptr-byid / edittext / editjson）覆盖本次全部手法
- PoEToolbox 可做补丁包管理 UI（列表、勾选、一键应用/还原/冲突提示）

### 5.1 社区补丁包生产方案（如何让别人方便地做补丁）

社区作者不碰 C# 代码，只写一份声明式 `patch.json`，用 toolbox 自带命令验证和打包：

```
PoEToolbox.Cli fx-patch validate <补丁目录>   # 校验 patch.json 结构 + 对本机索引 dry-run 定位
PoEToolbox.Cli fx-patch pack <补丁目录>       # 产出 <patchId>.fxpatch.zip（几 KB）
PoEToolbox.Cli fx-patch apply <补丁zip> <game-data>   # v2：通用应用入口（v1 由 fx-oilmod 内置替代）
```

- **作者工作流**：复制模板 → 填 4 类 op（addfile-derived / patchptr-byid / edittext / editjson，字段含义与 §4.2 内置操作一一对应）→ `validate`（对本机索引按 Id 定位、逐 op 报三态，定位失败=客户端不兼容提前暴露）→ `pack` 分发。
- **验证兜底**：`validate` 必须在"不写入"前提下跑完整定位算法（§4.3），把"行号不存在 / 引用行数≠1 / expectOriginal 不匹配"全部暴露为明确报错——补丁包质量靠工具校验，不靠作者自觉。
- **分发形态**：zip 内 `patch.json` + 可选资源（v1 的 addfile-derived 动态生成模式使资源通常为空）；应用时 bundle 落到 `PATCHED/<patchId>_<时间戳>.bin`，与 4.4 约定一致。
- **v1 与 v2 的关系**：v1 的 `fx-oilmod` 把定位器/patchptr/edittext 四个引擎能力做成可复用内部组件，v2 的 `fx-patch apply` 只是把同样的 op 用 patch.json 参数化驱动——引擎不用重写。

## 6. 风险与对策

| 风险 | 等级 | 对策 |
|---|---|---|
| 对方客户端版本不同，Id/路径不存在 | 中 | Step 1 定位失败即中止，明确报"不兼容此客户端版本" |
| 对方已装其他 mod 占用目标字段 | 中 | 三态"冲突"检测，报错不硬改 |
| 官方版本更新使补丁失效 | 确定 | 重放 apply 即可；补丁按 Id 定位，重放安全 |
| blob 追加的 EOF 空串哨兵被劫持（ROW 11281 型） | 中 | patchptr 内置检查：追加后全表扫描新指针值，多余命中自动改指新 NUL（本次已实现） |
| 点燃过渡操作实际无效（AOFile 非渲染开关） | 低 | 标记实验性默认关闭，不阻塞其余操作 |
| **循环动画 + view_end=0 卡死** | 高 | 本次教训：工具永不生成 view_end=0；文档明示 |

## 7. 验收标准（v1 完成定义）

1. 在开发机真实索引上：`status` → `apply` → `status`（全"已应用"）→ 游戏内目验（油花消失、地面火淡出、点燃不卡死）
2. `revert` → `status`（全"未应用"）→ 游戏内目验（全部恢复原版）
3. 再 `apply` → 恢复削弱状态（幂等闭环）
4. 中途断电/异常模拟：重跑 apply 能检测半完成状态并安全继续（逐 op 三态判定）
5. 全程 `unresolved` 计数不变、`check` 无缺失 bundle

## 8. 实施清单

- [ ] `PoEToolbox.Cli/Program.cs` 新增 `fx-oilmod` 命令骨架（status/apply/revert）
- [ ] miscanimated 定位器（Id → row → AOFile 字段偏移），复用探针 datrow/datgrep 逻辑
- [ ] 三态判定 + 冲突中止
- [ ] AddFile 动态生成副本内容（curve 替换）
- [ ] PinnedWriteBundlePath 集成（`LibGGPK3/oilmod`）
- [ ] edittext 两个文件（oilground.ot / oil_burst.ao，@file 式多行替换内化）
- [ ] IndexBackupService 集成
- [ ] 闭环测试（§7）+ 游戏内目验
- [ ] GUIDE 手册更新：给普通用户的使用说明章节
