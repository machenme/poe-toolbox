# PRD：工程加固（Engineering Hardening）

> 本文是**需求与拍板记录**（D1~D5 那张表是它的核心）：记「要做什么、谁定的」。怎么做看 `docs/SPEC-engineering-hardening.md`（技术方案，历史计划），做完之后系统长什么样看 `docs/ARCHITECTURE.md`（现状）。

| 项 | 内容 |
|---|---|
| 文档版本 | v1 |
| 日期 | 2026-09-19 |
| 代码基线 | `cb7bc4791`（main），`version.json` = 0.2.3 |
| 关联文档 | `docs/REVIEW-software-engineering.md`（体检报告 v2）、`docs/SPEC-engineering-hardening.md`（技术方案，历史计划）、`docs/ARCHITECTURE.md`（现状） |
| 作者 | 砚（提议） → 主人（拍板） |

---

## 1. 背景与问题陈述

PoE Toolbox 在 20 个提交内长到 33 928 行 C# / 181 个源文件，功能覆盖 PoE1/PoE2 双客户端的数据浏览、补丁、上色、翻译。领域设计扎实（内容判据、基线防污染、还原语义分离），但工程底座没跟上功能密度。

体检报告定位出的三类结构性问题：

| 类别 | 事实 | 代价 |
|---|---|---|
| **集中化** | `PoEToolbox.Shared` 7332 行（占源码 21.6%）；其中 `FxPatchEngine.cs` 单文件 1783 行，最大方法约 195 行 | 补丁引擎的任何改动都在巨型文件里进行，合并冲突与回归面随功能线性增长 |
| **约定化** | 内存回收依赖「每个 `Open*` 调用点手工补 `MemoryReclaimer.Reclaim(...)`」 | 已知 `FxPatchEngine.cs:1141` 漏 1 处（因 `using` 块内 `return` 提前退出）；这类缺陷会在每次新增调用点时复发 |
| **抽象空转** | `PoEToolbox.Sdk` 53 行 / 4 接口，却让 9 个插件中只有 5 个引用它；`IPlugin.CreateView()` 返回 `UserControl`，即 Sdk 自身依赖 WPF | 「插件可独立演进」的预期是假的；新增功能必须改 App 的 `ProjectReference` 与注册代码 |

另有若干可测量的健壮性缺口：2 处 `Timeout.InfiniteTimeSpan` 网络调用、未挂 `TaskScheduler.UnobservedTaskException`、18 处空 `catch {}`、`build.bat` 缺 `-m:1`。

### 1.1 触发本 PRD 的直接原因

体检过程中**自己踩到了两类问题**，证明不做加固的代价是真实且已发生的：

1. **文档证据失效**：报告中 P0-2 引用的行号（`FxPatchEngine.cs:353/480`、`FxDiff.cs:64/65`）来自 v0.2.3 之前的记忆，文件新增方法后全部漂移。这暴露的是「靠人脑维护精确引用」的脆弱性——项目里同类手工约定很多。
2. **计数型判断不可靠**：原计划用「源码扫描」检测漏回收，实测后被证伪——`GameDataLoader.cs` 计数 4 open / 2 reclaim 是注释与 `switch` 分支造成的噪声（误报），而 `FxDiff.cs` 2 open / 1 reclaim 实际无漏（一次回收覆盖两个流，漏判）。说明护栏必须建立在**运行时行为**上。

---

## 2. 目标

| # | 目标 | 可测指标 |
|---|---|---|
| G1 | 消除「漏回收」这一整类缺陷 | `GameDataAccess` 与 `MemoryReclaimer` 的运行时开合计数在任一操作结束后配对；新增测试断言配对，且**故意移除一次回收时测试必须失败** |
| G2 | 把补丁引擎的改动面从 1 个文件降到 1 个模块 | `FxPatchEngine.cs` ≤ 400 行；新增 6~7 个职责单一的文件；全量测试与拆分前逐条一致 |
| G3 | 给引擎装上行为安全网 | 新增 ≥ 3 条性质测试（apply→revert 字节恒等 / 重复 apply 无副作用 / 多补丁任意序 revert），在拆分**之前**落地 |
| G4 | 把已知健壮性缺口清零 | `InfiniteTimeSpan` = 0；`UnobservedTaskException` 已挂；`build.bat` 含 `-m:1` |

**非量化但明确要达成的效果**：下次出现「引用行号 → 已过期」这类问题时，有机制而不是靠记忆兜住。

---

## 3. 非目标（明确不做）

| 不做 | 理由 |
|---|---|
| 不重写 UI 为 MVVM | 7477 行后台代码，大爆炸重写风险远大于收益。改为「新模块强制 ViewModel + 老模块随改动迁移」的渐进规则（见 §4 B4） |
| 不改动领域逻辑与补丁格式 | 内容判据、`Redirect` 唯一改法、基线防污染是产品可信度的地基，本次一行不动 |
| 不引入新依赖 | 现有 2 个 NuGet 包够用；不引 MediatR / AutoMapper / DI 容器 |
| 不做动态加载插件 | 「编译期静态注册」是**有意的**架构选择（单文件 exe、无版本地狱）。本次只处理「Sdk 名不副实」的表述与契约问题 |
| 不优化运行时性能 | 无实测瓶颈诉求；内存回收只做「不漏」，不做「更快」 |
| 不拆分第三方库（LibBundle3 / LibGGPK3 / LibDat2） | 保持与上游的可比对性，便于日后同步 |

---

## 4. 范围与分批

按「风险递增 / 依赖递增」排序，**前两批必须连做**（B2 依赖 B1 建立的安全网）。

### B1 — 半天，零业务风险

| 项 | 内容 |
|---|---|
| W1 | 内存回收下沉：`GameDataAccess.Dispose()` 自动触发回收，消除手工约定；补运行时配对计数与测试 |
| W4 | 超时统一：`DatContainer.cs:37`、`PatchClient.cs:271` 的无限超时改为统一策略 |
| W5 | 异常兜底：挂 `TaskScheduler.UnobservedTaskException` 与 `AppDomain.UnhandledException`，落 `FileLogger` |
| W6 | `build.bat` 补 `-m:1` |

交付判据：全量测试绿（176 + 新增）、`dotnet publish` 手动验证一次出包成功。

### B2 — 1 天，重构的安全网

| 项 | 内容 |
|---|---|
| W3 | 三条引擎性质测试（作为 B3 的准入条件） |

交付判据：三条测试可运行；**人为破坏一次 revert 逻辑时，测试必须失败**（验证测试有效而非空转）。

### B3 — 1~2 天，需与功能开发同批（不要单开重构窗口）

| 项 | 内容 |
|---|---|
| W2 | `FxPatchEngine.cs` 拆分为 7 个文件 |
| W7 | `Shared` 去 UI 关注点（`ThemeManager` / `OutputPanel` / `UiStatus`） |

交付判据：`FxPatchEngine.cs` ≤ 400 行；拆分后所有补丁场景（内置补丁 apply/revert、整包替换型、purge、restore、diff 生成）手工回归通过。

### B4 — 长期，规则化

| 项 | 内容 |
|---|---|
| W8 | Sdk 定位决策（补全契约 或 改名 `Abstractions`） |
| 规则 | 新模块强制 ViewModel；新调用点默认走带作用域的 API |

---

## 5. 用户故事

| 角色 | 故事 | 验收 |
|---|---|---|
| 维护者（主人） | 我要新增一个补丁 op 类型时，只改 `FxPatchOperations.cs` 一个文件、跑一次测试就能确认没破坏 apply/revert | 新增 op 不修改 `FxPatchEngine.cs` 的编排代码 |
| 维护者 | 我要在文档/ISSUE 里引用代码位置时，能给出不会随提交漂移的锚点 | 引用方式为「类型名.方法名」，不写行号 |
| 维护者 | 我要在调试内存问题时知道「谁没还」 | 日志里有 open / dispose / reclaim 三个计数，异常时不配对会打警告 |
| 使用者（玩家） | 我不关心内部怎么改，但工具不能因此变慢、变卡、变不可回滚 | 全量手工回归通过；包大小不显著变化 |

---

## 6. 验收标准

| # | 标准 | 验证方式 |
|---|---|---|
| A1 | Release 全量测试通过 | `dotnet test tests/PoEToolbox.Tests -c Release` |
| A2 | Debug 下测试通过（~~除已知的 `DisposeWithoutSave_*`，见未决项 D4~~ → **无例外，Debug 与 Release 均 195 全绿**） | `dotnet test -c Debug` |
| A3 | 配对计数测试具备**反向有效性** | 临时注释掉一处回收 → 测试失败 → 恢复 |
| A4 | 补丁功能无行为回归 | 手工：内置补丁 apply → 状态查询 → revert；整包替换型 apply → revert；purge；彻底还原 |
| A5 | 发布链路未破 | `build.bat` 出包；CI 的 publish 无 loose json 护栏仍生效 |
| A6 | 无新增 NuGet 依赖 | 检查各 `.csproj` |
| A7 | `FxPatchEngine.cs` ≤ 400 行 | `wc -l` |

---

## 7. 约束

1. **行为等价优先**：B1/B2/B3 只允许「移动 / 提取 / 接线」，不允许顺手改逻辑。确需改行为的（如补 `:1141` 的回收）单独提交、单独说明。
2. **发布链路不可破**：新增任何运行时读取的文件必须走 `EmbeddedResource`（CI 会拦 publish 输出里的 `*.json`）。
3. **不破坏 `InternalsVisibleTo` 契约**：拆分后仍为同一程序集，测试与插件的 `internal` 访问必须照旧可用。
4. **依赖方向不可逆**：`Core → Shared`，因此 **`Shared` 不能引用 `Core`**。datc64 相关逻辑当前只能留在 `Shared`（见 SPEC §2.4）。
5. **单文件 exe 不变**：不新增需随包发布的目录。
6. **游戏数据安全**：任何改动不得影响「原版客户端可完全还原」这一保证。

---

## 8. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| 拆分过程中行为漂移 | 中 | 高（补丁错写游戏文件） | 先落 B2 性质测试；每步只搬不改；每步跑全量测试与手工回归 |
| `Dispose` 自动回收导致卡顿 | 低 | 中 | 回收本就是后台 + 合并（`MemoryReclaimer` 已有 coalescing）；保留 `reclaimOnDispose: false` 逃生口 |
| 自动回收与「另一个模块仍持有数据」冲突 | 低 | 中 | 复用现有 `CreateAbortCheck()` 语义（有人持锁或期间有人新开则不回收） |
| 一次性开太久导致与功能开发冲突 | 中 | 中 | B3 强制与下个功能同批；不单开重构窗口 |
| 测试造数据成本高 | 中 | 中 | 复用 `SeedIndex.cs` 与 `Fixtures/`；性质测试基于最小索引快照 |

---

## 9. 度量与回归

| 指标 | 基线（2026-09-19） | 目标 |
|---|---|---|
| `FxPatchEngine.cs` 行数 | 1783 | ≤ 400 |
| `Shared` 行数 | 7332 | 不增长（W7 后下降） |
| 测试用例数 | 176 | ≥ 180 |
| 漏回收点（运行时计数差） | 1 确认 + 1 待确认 | 0 |
| `InfiniteTimeSpan` 调用点 | 2 | 0 |
| 视图后台代码总行数 | 7477（不含 4 个窗口/控件） | 只增不改（新模块走 ViewModel） |

---

## 10. 里程碑

| 批次 | 内容 | 相对工期 | 依赖 |
|---|---|---|---|
| B1 | W1 / W4 / W5 / W6 | 半天 | — |
| B2 | W3 | 1 天 | B1（W1 的计数接口被 W3 复用） |
| B3 | W2 / W7 | 1~2 天 | **B2 必须先完成** |
| B4 | W8 / 规则落地 | 长期 | — |

发版处理：B1+B2 可作为补丁版本随下次发版（若不便发版则只提交不 tag）；B3 建议与下一个功能补丁合并发版。

---

## 11. 待决项（需主人拍板）

| # | 问题 | 选项 | 我的建议 |
|---|---|---|---|
| ~~D1~~ | ~~`AffixDataService.ConnectGameData` 打开索引后常驻不回收，是否有意？~~ | ~~(a) 有意，连接态需要 (b) 漏回收~~ | ✅ **已关闭（`07fdfa49c`），按 (a)**：注释写进了 `_gd` 字段——常驻到下次 Connect 或 `Dispose` 是有意的（列表要持续按行读 csd/uisettings，每次访问重开索引不现实），并标明两处释放时机。回收链本身已由 W1 下沉覆盖，见 `GameDataAccess.Dispose` |
| ~~D2~~ | ~~Sdk 定位~~ | ~~(a) 补全契约 (b) 改名 `Abstractions` 并去 WPF 依赖~~ | ✅ **已拍板并落地（2026-09-19）**：主人选 **b2**（改名 + 真正去 WPF）。因为单纯改名不带收益，b2 与 SPEC §7.2 的 `PoEToolbox.Ui` 绑定执行，分三个提交：`574404bd4`（改名）→ `c8cabcbd4`（建 Ui、搬 UI 关注点）→ `2463179fe`（`IPlugin` 去掉 `CreateView()`，界面插件改实现 `IUiPlugin`）。结果见 SPEC §8 |
| ~~D3~~ | ~~`PRD`/`SPEC` 是否需要入库~~ | ~~(a) 保持草稿 (b) 定稿后移入 `docs/`~~ | ✅ **已关闭，按 (b)**：两份定稿已移入 `docs/`（文件名不变），`.gitignore` 的通配 `PRD-*.md`/`SPEC-*.md` 换成两条锚在根目录的具体文件（词缀上色那对旧稿不入库）。入库前逐行确认过：文档里没有客户端绝对路径、账号、机器名。三份文档的指向写清了——ARCHITECTURE 现状 / SPEC 历史计划 / PRD 拍板记录 |
| ~~D4~~ | ~~Debug 下 `DisposeWithoutSave_*` 用例必挂（`Index.Dispose` 的 `Debug.Fail`）~~ | ~~(a) 修 `Index` 使 Debug 下不 `Fail` (b) 给用例加 Debug 跳过条件~~ | ✅ **已关闭（`587f6a96a`），两个选项都没采纳**：(b) 会让 Debug 永远测不到这条路径，而且 (a)(b) 都漏了第三条路——`Debug.Fail` 走 `Trace.Listeners` 派发，测试可以在自己这段窗口里接管它，于是既不用跳过、也没为测试改产品防线，还第一次给守卫本身上了回归保护（删掉 `Debug.Fail` → 用例红，已做破坏自检）。原判断「`Debug.Fail` 是有意的防线，不该为测试让步」保留——确实没让步 |
| ~~**D5（新增）**~~ | ~~CI 只跑 Release，`#if DEBUG` 里的断言在 CI 上等于不执行——要不要给 CI 加一条 Debug 测试腿~~ | ~~(a) 不加，Debug 只在本地跑 (b) 加一步 `dotnet test -c Debug`~~ | ✅ **已关闭，按 (b)**（`7e07f81c5`）。复测数字：Debug **36s** / Release 34s，一次 CI 多花 36s。落地成一个 `Test (Debug)` 步骤而非 matrix——matrix 会把 checkout/setup/restore/publish/打包整条 job 复制一遍，restore 与配置无关，复用同一次就够 |
