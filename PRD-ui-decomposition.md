# UI 逻辑下沉（View 瘦身）

- 日期：2026-09-24
- 代码基线：`6f580ffa7`（main，v0.2.5）
- 模式：Mini-PRD
- 上游：`docs/REVIEW-software-engineering.md` §9（2026-09-24 架构复核，判定「不值得重构整个架构」，唯一值得动的债是 N1）
- 下游：技术方案交 `spec-maker`。**冲突以 `docs/ARCHITECTURE.md` 为准**

---

## Problem and outcome

**问题**：3 个插件界面的后台代码合计 3756 行（`AffixWorkbenchView` 1388 / `DataBrowserView` 1350 / `FxPatchView` 1018），领域逻辑与 WPF 事件处理器写在同一处——配色色阶生成、预览行构建、正则过滤、配色方案的保存/导入/导出、补丁的应用与还原，乃至 `Index` 与 `GameDataAccess` 的直接持有，全部在 View 里。改一个功能要在这上千行里定位，改完**没有任何自动化能证明没改坏**，唯一的安全网是「开一次程序点一遍」。

**结果（outcome）**：加功能或修 bug 时，能在一个有单测的小类型里改，而不是在一个 1388 行的事件处理器集合里改；改完 `dotnet test` 能给出结论，目视确认只剩「观感对不对」这一件事。

**成功如何被观察**：① 目标 View 里 grep 不到领域类型；② 抽出的逻辑有单测；③ 全量测试两条腿都绿且用例数只增不减；④ 主流程目视与重构前一致。

---

## Target user and primary workflow

使用者 = 维护者本人，**既是唯一用户也是唯一开发者**（单用户本地工具，不编 persona）。

主流程：想改某个插件界面的行为 → 定位到对应的 Service / ViewModel → 改 → 跑单测 → 开程序目视确认 → 发版。

---

## In scope

| 优先级 | 项 | 为什么是它 |
|---|---|---|
| **P0** | `AffixWorkbenchView`（1388 行 / 76 方法）的领域逻辑抽出：色阶（Ramp）生成与 legacy 升级、预览行构建、过滤与正则、配色方案的保存/删除/导入/导出 | 最大体量；已有 `AffixDataService` 作承接点（不用新建抽象）；这几块是纯逻辑，抽出来立刻可测；词缀上色是活跃功能 |
| **P1** | `DataBrowserView`（1350 行）的游戏数据读取抽出——当前 View 直接持有 `GameDataAccess`（3 处）与 `Index`（10 处） | **风险最高**：与架构不变式 2「引擎跑之前必须广播释放句柄」正面相撞，View 持有的句柄可能让 `_.index.bin` 整文件替换失败 |
| **P2** | `FxPatchView`（1018 行）的启停 / 创建补丁流程抽出 | 相对干净（领域引用仅 `FxPatchEngine`×1），主要是交互编排，收益最低 |
| 贯穿 | 59 处 `MessageBox.Show` 收敛到 `IDialogService`，随各 View 同批做 | 与 P0/P1 同一批；单独做没有收益 |

---

## Out of scope

- **不动分层与工程边界**：`Abstractions` / `Shared` / `Core` / `Ui` 的引用关系保持现状。这是「UI 组织方式」的债，不是分层的债
- **不做全套 MVVM 迁移**：不引入新的 MVVM 库，不要求所有插件都上 ViewModel。只动 3 个超过 1000 行的
- **不动 XAML 视觉**：不换控件、不改布局、不改主题令牌（`PoEToolbox.Ui/Themes/` 一个 key 都不动）
- **不动领域类型本身**：`CsdDocument`、`GameDataAccess`、`FxPatchEngine`、`IndexBackupService` 一个字节都不改
- **不追求行数指标本身**：行数只是信号。降到 500 行以下但逻辑还在里面，等于没做

---

## Acceptance checks

**AC-1（P0 可测性）**
Given 我要改「词缀色阶生成」的逻辑
When 它位于一个独立类型、且该类型有 3 条以上单测
Then 我改完跑一次 `dotnet test` 就知道有没有改坏，不必开程序点一遍

**AC-2（P0 边界）**
Given `AffixWorkbenchView` 抽出完成
When 我在该文件里 grep `GameDataAccess` / `Index` / `CsdDocument`
Then 三者的命中数均为 0——领域访问只经由 Service

**AC-3（P1 边界）**
Given `DataBrowserView` 抽出完成
When 我在该文件里 grep `GameDataAccess` / `Index`
Then 二者命中数均为 0，且该 View 不再持有需要手工释放的句柄

**AC-4（回归网）**
Given 任一重构提交完成
When 跑 `dotnet test -c Release -m:1` 与 `dotnet test -c Debug -m:1`
Then 两条腿均为 **221+ 通过 / 0 失败**，用例数只增不减

**AC-5（行为一致）**
Given 重构完成、程序启动
When 走一遍「选择游戏数据 → 选词缀 → 上色 → 应用 → 还原」
Then 行为与重构前一致：目视无差异，且补丁账本状态与重构前相同

**AC-6（对话收敛）**
Given `IDialogService` 落地
When 在插件 View 里 grep `MessageBox.Show`
Then 命中数为 0

---

## Assumptions and open questions

| # | 类型 | 内容 | 验证方式 |
|---|---|---|---|
| A1 | 假设 | `AffixDataService` 是 P0 的天然承接点（已存在、已被 View 持有） | 读该文件确认职责边界够不够放色阶与方案存取；不够则新建 `AffixWorkbenchService` |
| A2 | 假设 | 抽出的逻辑**放插件工程自己的 `Services/`**，不下沉 `Core` | spec-maker 阶段确认没有跨插件复用需求；有则另议 |
| A3 | 假设 | `AffixWorkbenchView` 里已有的 5 个嵌套 VM 类（`EntryVm` / `LineVm` / `AffixSourceVm` / `AffixSourceGroupVm` / `TierPreviewVm`）直接搬出去即可，无需重写 | 抽出时逐个确认它们不依赖 WPF 类型 |
| Q1 | 开放 | `IDialogService` 放 `Abstractions` 还是 `Ui`？涉及工程引用边 | **交 spec-maker 定** |
| Q2 | 开放 | P1（`DataBrowserView`）抽到什么粒度算完 | 等 P0 的实测成本出来再定，不预先承诺 |
| Q3 | 开放 | P2（`FxPatchView`）是否值得做 | 做完 P0/P1 后按剩余收益重估，允许直接砍掉 |

---

## Handoff

```
State: DRAFT → VERIFY
Mode: Mini-PRD
Passed: outcome, scope, non-goals, acceptance checks（AC-1 ~ AC-6 均可观察）
Pending: Q1（IDialogService 落点）、Q3（P2 是否做）
Handoff: spec-maker required
```

交给 `spec-maker` 的理由：涉及跨模块设计（Service 落点）、一条新的工程引用边（`IDialogService`）、以及并发/资源生命周期（View 持有的索引句柄什么时候释放）——都不是产品问题。
