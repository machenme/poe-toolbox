# PoE2 特效隔离修改指南（实战复盘：油弹地面火淡出）

> 日期：2026-09-12　|　状态：已验证生效　|　客户端：Windows 版 PoE2（Bundles2 索引形态），Build 322001
> 用途：下次修改任何技能/特效时，直接参考本文档的结论、流程与工具。

---

## 0. 核心结论（先读这个）

1. **`.ot` 里的 `preload_animated_object` 只是预热，不决定渲染。** 改它指向新文件完全无效（游戏照样渲染旧的）。想改"哪个技能用哪个特效文件"，真正的开关在 **dat 表 `data/balance/miscanimated.datc64` 的行级指针**。
2. **`Index.AddFile` 新增索引路径在游戏端完全可用**（前提：`AppendPathEntry` 的 RecursiveSize 修复已合入，见 `src/LibBundle3/Index.cs`）。新增文件游戏能正常解析和加载。
3. **共享特效文件（如 `grd_zones/grd_burning01.ao`）被两条 dat 链引用**：
   - `groundeffects.datc64`：368 行共享（整个点燃地面家族）→ 改它=影响所有燃烧地面，**不可用于隔离**；
   - `miscanimated.datc64`：**只有 1 行**（ROW[8307]，`BaseOilGroundBurningEffect`）→ **隔离修改的正确切入点**。
4. FADE 曲线语义：`TimelineParameterEventType` 的 `"curve": "2 0 0 Linear 0.25 X Linear"` 中，**0=特效最大，1=特效最小**（强度淡出，不是严格透明度）。
5. **游戏日志是免费的加载探针**：`logs\LatestClient.txt` 里 `[CRIT] Failed to create resource "<路径>"` 能精确告诉你游戏加载了什么、失败在哪。
6. **找条目最高效的方式是按 Id 名字查**：`datgrep 'data/balance/miscanimated.datc64' '<名字关键词>'` 一条命令秒出全部匹配的行号+Id+AOFile。miscanimated 的 schema 解析是正常的（Id/AOFile 都能读出）；**只有 groundeffects 解析失败**（AOFile 是数组列），那张表才需要 `datrow` 原始字节法。App 数据浏览器的"提取为 JSON"导出的全表 JSON 也可以本地随便搜，**JSON 数组下标 = 行号**（与 patchptr 的行号一致）。

---

## 1. 背景与目标

- 目标：只让**涂油手雷（oil grenade）**的地面燃烧特效在淡出阶段渐隐，其他所有使用 `grd_zones/grd_burning01.ao` 的燃烧地面（怪物火、其他技能等）完全不受影响。
- 原始曲线（`loop` 事件的 `grdZone_FADE_CTRLs` + `grdZone_ParticleFADE_CTRLs`）：
  `"curve": "2 0 0 Linear 0.25 0 Linear"` → 目标：末端 `0` → `1`。
- 文件是共享资源，直接改原件会波及所有使用者 → 必须隔离。

---

## 2. 引用链分析（本次最关键的认知）

```
服务器/技能逻辑
   │
   ├─ oilground.ot (entity template, client 端)
   │    └─ preload_animated_object = grd_Burning01.ao   ← 只是预热！改它无效！
   │
   ├─ groundeffects.datc64
   │    └─ 368 行共享 grd_Burning01.ao（点燃地面家族所有参数变体）← 改它影响一切
   │
   └─ miscanimated.datc64  ★ 正确切入点
        └─ ROW[8307]（油弹点燃地面条目，key=1695860）
           ├─ +8 字段: 指针 → "Metadata/Effects/Spells/grd_Zones/grd_Burning01.ao"
           └─ 相邻行 8308-8310 = 油弹的 3 个 MTX 皮肤变体（key 连续分配）
```

判断"哪一行属于哪个技能"的方法：**优先按 Id 名字搜**（`datgrep '<表>' '<关键词>'`，如 `OilGrenade`/`BurningGround`），其次看相邻行的 key 是否连续递增、行内字符串是否引用该技能目录下的其他特效。

### 油弹家族在 miscanimated.datc64 里的完整条目地图（范例）

| 行号 | Id | AOFile | 说明 |
|---|---|---|---|
| 8183 | `OilGrenadeExplosion` | `…crossbow_oilgrenade/oil_Burst.ao` | **油爆特效**（爆炸本体） |
| 8307 | `BaseOilGroundBurningEffect` | `…grd_Zones/grd_Burning01_oil.ao` | **地面燃烧**★本次改的（原指向 grd_Burning01.ao） |
| 8308-8310 | `Verisium/Divine/AbyssalOilGroundBurning` | MTX `…/grd_Burning01.ao` | 皮肤变体 |
| 8332 | `RudjaOilGroundBurning` | Rudja `burninng_GRDZONE.ao` | 怪物版本 |
| 8590 | `VaalGuardOilGrenadeExplosion` | Vaal `oil_Burst.ao` | Vaal 爆炸 |
| 8592 | `VaalGuardOilGroundBurningEffect` | Vaal `grd_Burning01.ao` | Vaal 地面 |
| 11031/11203/11204 | `OilGrenadeExplosion{Verisium,Abyssal,Divine}` | MTX `oil_Burst.ao` | MTX 爆炸 |
| 8310-8313 等 | `*OilGroundIgnitionTransitionEffect` | `transition_FIRE.ao` 家族 | 点火过渡特效 |

规律：普通版条目 Id 以 `Base` 开头或无前缀；MTX 版 = `<皮肤名>+<效果名>`；怪物版 = `<怪物名>+<效果名>`。每个技能的"一套特效"在表里是行号相邻的一簇条目。

---

## 3. 本次全部修改点清单（当前索引里的实际状态）

| # | 对象 | 修改内容 | 状态 |
|---|---|---|---|
| 1 | `metadata/effects/spells/grd_zones/grd_burning01_oil.ao`（**新增路径**，AddFile） | 内容 = 原件 + `loop` 的两处（前 2 处）`2 0 0 Linear 0.25 0` → `…0.25 1`；`loop_burst` 的两处保持 0 | ✅ 生效，sha `815567a5aff83986`，11,686 B |
| 2 | `data/balance/miscanimated.datc64` | blob 末尾追加字符串 `Metadata/Effects/Spells/grd_Zones/grd_Burning01_oil.ao`（110 B，含 NUL）；**ROW[8307] 行内 +8 的 8 字节指针** `0x19E0AC` → `0x237252` 指向新字符串 | ✅ 生效 |
| 3 | `data/balance/miscanimated.datc64` ROW[11281]（+24） | 修复性改动：该行原有一个指向"旧 EOF"的空串指针被追加数据意外劫持，已改指新字符串末尾 NUL（`0x2372BC`→`0x2372BE` 实为空串定位） | ✅ 无害化 |
| 4 | `metadata/effects/spells/crossbow_oilgrenade/oilground.ot` | preload 引用改为 `_oil.ao`（**对渲染无效，纯遗留**，无害可不管） | 无影响 |
| 5 | `metadata/effects/spells/grd_zones/grd_burning01.ao`（原件） | **未动**（sha `9458b35986fafffd`），字节级原样 | ✅ |
| 6 | `grd_burning01_copy.ao` | 早期曲线测试遗留（0.8），无引用，游戏不加载 | 无影响 |

索引整体：3,111,590 files / 64,091 bundles / unresolved 5（客户端固有）/ 缺失 bundle 0 / 自定义 bundle 仅 `LibGGPK3/0.bundle.bin`。

---

## 4. 踩坑记录（按时间序，每条都是真金白银的教训）

### 坑 1：目录表 `RecursiveSize=0` → 游戏崩溃（已修复于库代码）
`AddFile` 追加的目录记录如果 `RecursiveSize=0`，游戏解析目录表会错乱，报
`[CRIT] Failed to create resource "…ao" (Unknown token: Â·)` 随后断线。
**修复**：`AppendPathEntry` 中叶子记录 `RecursiveSize = entry.Length`，根记录（PathHash=`0xF42A94E69CFF42FE`）`RecursiveSize += entry.Length`。已合入 `Index.cs` 并有测试。**新环境必须确认此修复在位**。

### 坑 2：preload 重定向的误导性
改 `oilground.ot` 的 preload 指向副本后游戏无报错、视觉无变化——因为 preload 只是预热。**判断"改 X 有没有用"不要靠猜，用垃圾标记实验（见 §6）或直接定位 dat 引用。**

### 坑 3：dat 表字符串搜索的编码陷阱
datc64 的字符串是 **UTF-16LE 且无 BOM**。按 BOM 解码的文本搜索会漏掉全部命中；大小写也要注意（`grd_Burning01` 驼峰 vs 小写，字节不同）。必须用**原始字节级 UTF-16LE 匹配**（探针 `grep` 已内置 ASCII + UTF-16LE 双通道）。

### 坑 4：datc64 指针不是绝对偏移
行内 8 字节指针 = **相对分隔符（0xBB×8）位置的偏移**：`abs = dataOffset - 8 + v`（dataOffset = 分隔符位置 + 8）。另外字段是**紧凑排列**的，8 字节指针不一定 8 对齐，扫描时要逐字节。

### 坑 5：blob 追加会"劫持"指向旧 EOF 的空串指针
原 blob 最后可能存在指向"文件末尾"（即空串）的指针。追加数据后它会指向新字符串的开头——等于凭空让一个无关行引用了你的新文件。**追加后必须全表扫描新指针值，把多余命中改指真正的空串（任意 NUL NUL 处）。**

### 坑 6：文件表乱序不是问题（不用排序）
客户端原始文件表按 PathHash **严重乱序**（155 万处逆序），游戏是哈希查找不是二分——`AddFile` 追加到表尾完全没问题。

### 坑 7：本项目 Datc64File 解析器对部分表失效
`groundeffects`/`miscanimated` 用 schema 解析时字符串列全空（schema 过旧或布局差异）。**原始字节解析（探针 `datrow`）是可靠替代**，不要在解析器上死磕。

### 坑 8（环境）：工具链输出
- PowerShell 工具不回显 stdout：输出必须 `| Out-File -Encoding utf8` 再用 Read 读（`>` 默认 UTF-16 会乱码）；
- Bash 工具 PATH 残缺（`ls`/`cat` 等 command not found）：一律用 PowerShell 或专用工具。

### 坑 9：循环动画 + view_end=0 → 游戏卡死（不崩溃、不报错）
`transition_fire.ao`（`view_looping = true`）加 `view_end = 0` 后，点燃时主线程死循环卡死。
**规则：改 view_end 前确认目标动画不是 looping。** 实验补充：`view_end = 1` 与 250 无视觉差异——
对"事件全在 time 0.0"的特效，view_end 是死杠杆（帧只是事件坐标系，粒子生成后的寿命由 .pet
内部参数决定，宿主动画不回收）；view_end 只在事件分布在不同时间点或有随动画驱动的参数曲线时有用。

### 坑 10：PowerShell 传参会吞双引号
给探针传含 `"` 的 old/new 文本时，PowerShell 会丢引号导致 needle 不匹配（edit 静默 0 替换、
文件原样写回）。**用探针的 `@file` 语法**：把 old/new 写进文本文件（用 `\n` `\t` 转义序列表示
换行缩进），参数传 `@C:/path/old.txt`。

---

## 5. 标准操作流程（SOP——下次改其他技能照这个走）

> 以下 `$idx` = `<游戏安装目录>\Bundles2\_.index.bin`（例如 Steam 库里的 PoE2 目录，路径以本机实际位置为准）
> `$exe` = `C:\work\poe-toolbox\.scratch\RealDataAddFile\bin\Debug\net10.0-windows\RealDataAddFile.exe`
> 前置：`dotnet build .scratch\RealDataAddFile\RealDataAddFile.csproj -c Debug`；**游戏必须关闭**。

### Step 0 只读体检（每次必做）
```
$exe $idx check          # files/bundles/unresolved/missing 基线
$exe $idx dirs 3         # RecursiveSize==0 的记录数必须为 0
```

### Step 1 定位特效文件与引用者
```
$exe $idx discover '<路径子串>'            # 找候选 .ao/.ot 文件
$exe $idx grep '<目录前缀>' '<关键词>'      # 目录内内容搜索（ASCII+UTF-16 双通道）
$exe $idx show '<文件路径>'                 # 按 BOM 解码打印内容（含 sha256）
```
判断共享性：同一 .ao 路径被几个 .ot 引用？MTX 变体目录（`microtransactions/...`）里有独立副本吗？

### Step 2 定位 dat 表里的行级引用（隔离的关键）

**首选：按 Id 名字直接查（秒出，schema 解析正常）**
```
$exe $idx datgrep 'data/balance/miscanimated.datc64' '<名字关键词，如 OilGrenade>'
```
输出全部匹配的行号 + Id + AOFile。也可以用 App 数据浏览器的"提取为 JSON"导出全表后本地搜索（数组下标=行号）。

**备用：原始字节法（schema 解析失败的表用，如 groundeffects）**
```
$exe $idx datrow 'data/balance/groundeffects.datc64' '<特效路径子串>'
```
输出：blob 中该字符串的偏移、引用它的**行号与字段偏移**、每行的 key 与其他字符串字段。
- 引用行 = 1 且能确认属于目标技能 → ✅ 可隔离；
- 引用行很多（如 groundeffects 的 368 行）→ ❌ 该表不可隔离，找别的表或改用别的策略。

### Step 3 创建副本文件（AddFile）
```
$exe $idx copycontent '<原件路径>' '<副本路径>' '<旧文本>' '<新文本>' 2
```
（`copycontent` 对已存在的副本直接覆盖重写；最后一个参数=替换前 N 处；不带文本参数=原样复制）

### Step 4 dat 指针重定向（blob 追加 + 单行改指针）
```
$exe $idx patchptr 'data/balance/miscanimated.datc64' '<旧路径子串>' '<新完整路径>'
```
内置保护：引用行数 ≠ 1 时拒绝执行。执行后自动备份（IndexBackupService）。

### Step 5 检查空串指针劫持
```
$exe $idx datrow 'data/balance/miscanimated.datc64' '<新路径子串>'   # 应只有 1 行引用
# 若出现额外行（通常是最后一行、值为旧 EOF），用 fixfield 修掉：
$exe $idx fixfield 'data/balance/miscanimated.datc64' '0x<新字符串v值>' '0x<空串v值>' apply <多余行号>
# 空串 v 值 = (文件末尾 NUL 处的绝对偏移) - (dataOffset - 8)
```

### Step 6 验证（重开索引）
```
$exe $idx check                       # 基线数字 +1 file
$exe $idx datrow …                    # 引用关系复核
$exe $idx show '<副本路径>' 1          # sha/内容确认
```

### Step 7 游戏内目验 + 日志确认
- 启动游戏触发目标技能；
- `logs\LatestClient.txt` 查 `Failed to create resource`：**无 CRIT = 资源加载正常**；
  需要判定"游戏到底加载不加载某文件"时，把该文件写成垃圾文本（`writetext` 模式）再进游戏——CRIT 出现=被加载，不出现=没被用。
- 回滚兜底：`$exe $idx restore`（baseline 逐字节），或启动器的"验证/还原游戏文件"。

---

## 6. 探针工具手册（`.scratch/RealDataAddFile/`，gitignored）

| 模式 | 用途 |
|---|---|
| `check [bundleDir]` | 索引体检：数量、unresolved、缺失 bundle、关键文件存在性 |
| `dirs [n]` | 目录表健康：首末记录、RecursiveSize==0 计数、hash 有序性 |
| `fileorder` | 文件表 hash 有序性统计、指定路径的表内位置 |
| `discover <terms> [take]` | 按路径子串搜文件 |
| `show <path> [maxChars]` | 按 BOM 解码打印文件内容 + sha256 |
| `dump <path> [maxChars]` | 转义打印（\r\n\t 可见） |
| `grep <prefixCSV> <text>` | 目录前缀内内容搜索（ASCII + UTF-16LE 字节级） |
| `info <path>` / `bundles [prefix]` | 文件所在 bundle 详情 / bundle 列表 |
| `copyedit <src> <dst> [old] [new] [first:N]` | 复制+改文本+保存+重开校验（dst 必须不存在） |
| `edit <path> <old> <new> [first:N]` | 就地文本替换 |
| `copycontent <src> <dst> [old] [new] [N]` | 复制内容（dst 可已存在）+ 可选替换 |
| `writetext <path> <text>` | 写垃圾标记（加载探针实验用） |
| `datrow <dat> <substr>` | datc64 行级引用定位（原始字节，不依赖 schema；groundeffects 等解析失败的表用它） |
| `datgrep <dat> <substr>` | schema 解析查行号（miscanimated 等正常表的首选，按 Id/AOFile 秒查） |
| `strctx <dat> <substr>` | 提取 blob 中含关键词的完整字符串 |
| `hashgrep <prefix> <path>` | 按 NameHash 的 8 字节搜索（dat 按哈希引用的场景） |
| `patchptr <dat> <oldSubstr> <newFullPath>` | blob 追加字符串 + 单行指针重定向（≥2 行引用时拒绝） |
| `fixfield <dat> <findHex> <replHex> [apply] [row]` | 行区 8 字节值查找/替换（空串指针修复等） |

所有写入模式自动走 `IndexBackupService`（baseline 快照），写入固定落在 `Bundles2\LibGGPK3\0.bundle.bin`。

---

### 坑 11：datrow 的跨界解码假阳性
blob 追加字符串后，`datrow` 可能报某个**未修改的行**（典型：指向旧 EOF 的空串指针行）"引用了新
字符串"——实为解码起点落在上一字符串尾部、一路穿越到新字符串区域的假阳性。
**判据**：用 `fixfield` 按精确指针值搜索，搜不到 = 假阳性（真劫持总能搜到精确值）；
拿不准时用 `hexdump <dat> <off> <len>` 看原始字节确认。

---

## 7. 回滚与版本更新

- **逐字节原始索引**：`<游戏索引同目录>\backup\_.index.bin`（本次改动前的索引）；
- **启动器还原（验证/修复游戏文件）**：整体恢复游戏数据（注意：不要用工具的 `restore` 命令恢复过期 baseline——baseline 可能早于客户端补丁，恢复会造成 bundle 缺失，见 2026-09-11 任务 8 的事故记录）；
- **客户端版本更新**会覆盖索引并使 patch 失效：更新后重放 §5 流程即可（SOP 全程可脚本化）。

## 8. 下次修改其他技能的 Checklist

1. [ ] `check` + `dirs` 体检，记录基线数字
2. [ ] `discover`/`grep` 找到目标技能的特效文件与 .ot，确认共享范围
3. [ ] 先想清楚改**哪个文件**：特效 .ao（内容）还是 .ot（结构）？确认它有没有被 dat 表引用
4. [ ] `datrow` 在 `miscanimated.datc64`（以及必要时 `groundeffects` 等）定位行级引用，确认引用行数与技能归属（key 连续性 + 相邻行字符串）
5. [ ] AddFile 副本 → `patchptr` 重定向 → `datrow` 复核 + 空串指针检查
6. [ ] 游戏内目验 + 日志无 CRIT
7. [ ] 把新增修改点追加到本文档 §3 的清单里

---
*相关文件：PRD `docs/PRD-oil-burst-fx-isolation.md`；过程日志 `.workbuddy/memory/2026-09-11.md`、`2026-09-12.md`；探针源码 `.scratch/RealDataAddFile/Program.cs`。*

*本文是**具体案例复盘**。抽出来的通用决策方法见
[`GUIDE-patch-authoring.md`](./GUIDE-patch-authoring.md)（什么情况该隔离、什么情况只能就地改）。*
