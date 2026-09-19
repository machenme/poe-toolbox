# 工程加固前后对比（2026-09-19 ~ 2026-09-20）

- 区间：基线 `cb7bc4791` → `060fb453b`（C28），共 **28 个提交**（清单见 `docs/SPEC-engineering-hardening.md` §10.5）；本文自身的提交 C29 只回填文档，不含代码
- 版本：`version.json` 仍是 **0.2.3**——本轮不出新版，纯结构改动
- 触发原因：`docs/REVIEW-software-engineering.md`（体检报告 v2）列出的 P0/P1/P2 风险项
- 怎么复核：`git log --oneline cb7bc4791..060fb453b`（28 条，即全部代码提交）；每步的实测数字在 `docs/SPEC-engineering-hardening.md` §0 与 §9
- 本文不含机器相关信息：游戏客户端绝对路径、账号、机器名一律不写（§8 的实测只给相对路径与字节数）

> 本文只回答一个问题：**同一份代码，改之前和改之后分别是什么样、差别带来什么**。
> 为什么这样设计不在本文（看 SPEC），当前规矩是什么也不在本文（看 `docs/ARCHITECTURE.md`）。

---

## 1. 一句话总结

改之前：一个能跑、但**没人能安全改动**的程序——引擎是一个 1754 行的文件，插件互相引用，`Shared` 白背 WPF，回收要手工调用，测试只在 Release 跑，设计决策记在被 `.gitignore` 挡住的草稿里。
改之后：**同一套功能，分层、回收、异常、测试、文档每一条都有了可核对的边界**，且过程中**没有改过任何用户可见行为**（§9）。

---

## 2. 分层与依赖方向

| 面 | 改之前 | 改之后 | 带来的差别 |
|---|---|---|---|
| 契约层叫什么 | `PoEToolbox.Sdk`，但 `AssemblyName` 也叫 `Sdk`，且引用了 WPF | `PoEToolbox.Abstractions`，**不引用 WPF**（`574404bd4` → `2463179fe`） | 「Sdk」这个词原本暗示对外 ABI，实际插件是编译期静态注册的；改名不带收益，所以同时把 WPF 拿掉，`IPlugin` 只剩生命周期与元数据 |
| 插件怎么带界面 | `IPlugin.CreateView()` 直接在契约里 | 新接口 `PoEToolbox.Ui.IUiPlugin`，导航只列 `OfType<IUiPlugin>()` | 无界面插件（如后台补丁执行）也能注册、照样收生命周期回调 |
| UI 共享件在哪 | `Shared` 里有 `OutputPanel`、`UiStatus`、`FxEngineRunner`，因此 `Shared` 必须 `UseWPF` | 新建 `PoEToolbox.Ui` 承接（`c8cabcbd4`），`Shared` 无 WPF | CLI 与测试的编译闭包里不再有 WPF。反证很硬：`Shared` 去掉 `UseWPF` 当场 CS1069，改完之后再也触发不了 |
| 主题管理器在哪 | `Shared/ThemeManager.cs` | `App/Themes/ThemeManager.cs`（`69645d931`） | 主题只在入口进程有意义，不该被插件继承 |
| `Core` 的 GDI+ | 靠 `Shared` 的 `UseWPF` **传递**过来（`<UseSystemDrawing>` 在 .NET 10 SDK 上是空转属性） | 显式 `<FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />` | 原本是一条隐式依赖：`Shared` 一改，`Core` 就崩。现在写在自己 csproj 里 |
| 插件之间 | `Plugins.Voyager → Plugins.BagCleaner` 工程引用（复用 6 个类型） | **插件之间零引用**（`e21cd1059` + `5e73be5f2`） | BagCleaner 内部改动不再波及 Voyager；见 §3 |
| 引用方向 | 自上而下，但有一条插件间反例 | 自上而下，无反例；ARCHITECTURE 立了不变式 12 兜住 | 破坏方式是「下次有人想复用别的插件的代码」，所以写成了两条允许的路径而不是一句禁止 |

## 3. P1-5：跨插件复用怎么落地（前后对照）

改之前的形状：Voyager 要复用 BagCleaner 的 `NativeMethods` / `InputSimulator` / `GridCalculator` / `FixedScreenGrid` / `GridConfig` / `HotkeyService`，最省事的做法就是引用那个插件工程。

改之后分成三类，各自走了不同的路：

| 类型 | 去哪了 | 为什么是这里 |
|---|---|---|
| `NativeMethods`、`IInputSimulator`、`InputSimulator` | `Core/Input/` | 纯 Win32。`Core` 本来就声明了桌面框架引用，放这里**不新增任何依赖边**；放 `Shared` 就要给 `Shared` 加回桌面框架，§2 的成果退一半 |
| `GridCalculator`、`FixedScreenGrid`、`GridConfig` | `Core/ScreenGrid/` | 同上，签名用 `System.Drawing` |
| `HotkeyService` | **留在 BagCleaner**，契约 `IHotkeyService` + `IHotkeyServiceFactory` 提到 `Abstractions` | 实现是 WPF 的 `HwndSource.AddHook`，沉不进任何无 UI 层。接口补了 `IsInitialized`、继承 `IDisposable`，消费者按 `baseIdOffset` 向工厂要实例 |

`Ui → Core` 这条路没走：它会新增一条跨层边，要改 ARCHITECTURE 的不变式，收益却只是省一个工厂接口。

## 4. 补丁引擎

| 面 | 改之前 | 改之后（`fc8a84441` 等 S1~S8） |
|---|---|---|
| 体积 | `FxPatchEngine.cs` **1754 行** | **211 行**：只剩入口门面 + 日志出口 |
| 逻辑在哪 | 全在上面那个文件里，靠局部变量串 | `Shared/Fx/` **9 个模块**：`FxBuiltInPatches` / `FxDatc64Pointers` / `FxPatchCommands` / `FxPatchIdentity` / `FxPatchModel` / `FxPatchOperations` / `FxPatchPackage` / `FxPatchState` / `FxRawPackPatch` |
| 状态判定 | 布尔「打了没」 | 三态归约（未应用 / 已应用 / 冲突 / 不兼容），纯函数、**读实际索引内容不读账本**，9 条性质测试盯着（`docs/ARCHITECTURE.md` 不变式 4） |
| CLI 动词 | 只有内置路径 | 内置 `fx-oilmod <gd> [id|all] status|list|apply|revert|cleanup|purge|restore`，自定义 `fx-patch <gd> <json|zip> ...` |
| 认知修正 | 以为「revert 撤销后回到原状」 | 本机实测：**`revert` 只回语义、不回字节**，逐字节还原只有 `restore`（§8） |

## 5. 内存回收与异常

| 面 | 改之前 | 改之后 |
|---|---|---|
| 回收触发 | 手工在 `using` 块外调 `MemoryReclaimer.Reclaim()`，**11 处散在 8 个文件**，漏一处就白留几百 MB | 下沉到 `GameDataAccess.Dispose`（`ddcedd5be`），插件/引擎侧的调用点归零，全仓只剩 `Dispose` 内那一处。`FxPatchEngine` 预检冲突时 `return` 提前退出那条路径自动覆盖——**这正是原来漏掉的一处** |
| 「源码扫描测试」这个方案 | 报告提议数 `Reclaim()` 调用点 | **实测被否掉**：朴素计数既误报又漏判，改成测运行时行为（回收链真的跑没跑） |
| 常驻索引连接 | `AffixDataService.ConnectGameData` 打开后长期持有，看起来像漏回收 | 是**有意的**，注释写清了两处释放时机（PRD D1，`07fdfa49c`）；回收由 W1 下沉自动带上 |
| 未处理异常 | 没有兜底，崩了没记录 | App + Cli 均挂兜底并落盘（`f154c507f`，`FileLogger.WriteCritical`） |
| 空 `catch` | `src/` 下 19 处静默（报告口径写的是 18 处，实测不同） | 逐处判定：**4 处补日志**（config 解析、插件段反序列化、schema 下载、旧临时目录清理），**15 处注明有意吞掉**（取消、轮询、注册表试探、退出竞态等）。全加日志会被正常使用刷爆 |

## 6. 联网与配置

| 面 | 改之前 | 改之后 |
|---|---|---|
| 超时 | 各调用点各写各的，有的没写 | `NetworkDefaults` 统一（`2327ae8c9`）；`DescribeFailure` 把超时/取消/网络失败翻成中文原因，`PoeNinjaFetcher` 调用点降级不挂起（`12bd84929`） |
| 配置读写 | `ConfigService` 路径写死，测试会真写用户 AppData | 一条根注入缝 `DataDirectoryOverride`（`f83d2d7f6`），六个派生路径即时求值；损坏时改名 `config.json.corrupt.<ts>` 并记 `LastReadError` 而不是静默返回空 |
| 缝的例外 | 三处 `static readonly` 快照不跟缝走；affix 另有一条自己的缝 | C16（`e93260476`）清零，只剩 `FileLogger.App` 一条**有意的**例外并写进缝的注释 |
| 测试隔离 | xUnit 默认并行，偶发互删产物 | 整个测试程序集关并行（`34f50e849`）。实测并行 26~36s vs 串行 34~37s——I/O 受限下并行没换来时间，却需要一张「漏一个就偶发挂」的清单 |

## 7. 测试与 CI（数字对照）

| 指标 | 改之前 | 改之后 |
|---|---|---|
| 用例数 | 176 | **195**（引擎拆分 +9、ptr 性质 +3、网络提示 +3、config 分支 +3、P-4 +1） |
| Release | 176 全绿 | 195 全绿，34s |
| Debug | 与 Release 跑同一套用例，但 `DisposeWithoutSave_*` **长期挂着**（撞 `Index.Dispose` 的 `Debug.Fail`）——那条在基线就有，本轮第一次让它变绿 | 195 全绿，34~36s（`587f6a96a`——**本项目 Debug 首次全绿**） |
| D4 的解法 | PRD 只给了两选项：改 `Index` 或不跑用例 | 两个都没采纳：测试期间接管 `Trace.Listeners`，把守卫本身断言下来。既没跳过、也没为测试让步产品防线，还第一次给 `Debug.Fail` 上了回归保护 |
| CI 测试腿 | **只跑 Release** → `#if DEBUG` 里的断言在 CI 上等于不执行 | Release + Debug 两条（`7e07f81c5`），代价一次 CI +36s。做成一个 `Test (Debug)` 步骤而不是 matrix，避免整条 job 复制一遍 |
| 出包 | `build.bat` 不带 `-m:1`（并发 restore 会打架） | 带（`d6b3d46ba`）；publish 后断言无 loose `.json` |

## 8. 本轮新增的实测知识（不是代码，但影响后续判断）

补丁写入路径在本人同意后于本机真实 Steam 客户端跑完整链（`docs/SPEC-engineering-hardening.md` §9.1）。三条以前没人写下来的事实：

1. **`revert` 恢复语义不恢复字节**：文本替换型 revert 后索引与基线 SHA 不同（bundle 885→990 B）；整包替换型 revert 后文件副本仍在（bundle 涨到 2,786,283 B）。只有 `restore` 逐字节回到基线。UI 文案已经是四档（启用 / 还原 / 卸载 / 彻底还原游戏客户端），不用改，但文档和注释从此不许写「revert 可逆到原状」。
2. **打一个补丁会让 115 MB 的索引撑大 4.9%~6.4%**（+5.6 ~ +7.3 MB）。不是泄漏。
3. **打过补丁的索引与 Steam 的校验记录不一致**：Steam 再校验一次就把索引换回原版，而补丁账本还在。这就是三态判定必须读实际索引内容、不能读账本的原因（不变式 4、13）。

一个**没复现的开放项**：校验前的旧索引上，`status` 与 `apply` 预检对同一条 `edittext` 判成了「未应用」和「冲突」两种结果。三条理论逐一排查都没命中，那块索引随后被 Steam 覆盖，现场没了——留成待办而不是结论。

## 9. 明确没有变的东西（避免误读这份对比）

- **用户可见行为零改动**：没有任何功能开关、界面文案、数据格式、文件格式随本轮变化。体检报告的 MVVM、`MessageBox.Show` 收口（P1-1 / P2-6）**明确排除在外**。
- **版本号 0.2.3 未动**：这轮的收益是「下次改动不再危险」，没有新用户功能可写进 changelog。
- **上游 vendored 的 `Lib*` 没被「整理」**：`DatContainer.DownloadSchemaMin` / `PatchClient.UpdateNodeAsync` 一度被判成死代码要删，核查后判定收回——它们是上游公共 API，删了只是增加分歧（`62816cf26`）。
- **`Shared` 依然是插件与 CLI 的共同底座**，没有为了搬家把它拆开。

## 10. 文档体系前后

| 面 | 改之前 | 改之后 |
|---|---|---|
| 架构现状 | 只有 README 里一棵会过时的树（还缺 4 个插件工程） | `docs/ARCHITECTURE.md`：分层与三条规则 / 20 个工程清单 / 三条运行期数据流 / **13 条不变式** / 落盘位置 / 已知结构债（`9398f1b20` 起，本轮跟到 13 条） |
| 设计稿在哪 | `PRD-*.md` / `SPEC-*.md` 整段被 `.gitignore` 挡住，决策过程只存在本机 | 两份定稿入库到 `docs/`（PRD D3，`060fb453b`），通配换成两条锚在根目录的具体文件。三份文档指向写清：**ARCHITECTURE = 现状、SPEC = 历史计划、PRD = 拍板记录**，冲突以 ARCHITECTURE 为准 |
| 行号引用 | 文档按行号指代码，一次提交内就全部失效 | 「不写行号」的规矩立进 ARCHITECTURE，其余文档跟进（`5adb66ae0`）：行号换成类型名/唯一字符串，并回代码逐个确认符号还在 |
| 重复文档 | 内置补丁说明有 `.txt` + `.md` 两份，`.txt` 是子集且停在旧口径 | 只留 `.md`（`62816cf26`） |
| 报告处置 | 体检报告的 17 条风险项没有处置状态 | §6.5 状态表逐项标 ✅/部分/未动，所引 SHA 逐个核过（`9d4ddc619`）；本轮又回填了 P1-5、P2-2、P2-8 三条 |

## 11. 还没做的（前后对照里剩下的那半边）

| 项 | 状态 | 缺什么 |
|---|---|---|
| 开一次程序目视确认 | **待本人做** | W7a 主题三态、W7b 输出面板/状态栏、W8 导航 10 项——三件事在同一次启动里能看完，自动化测不到 |
| P1-1 MVVM / P2-6 `IDialogService` | 未做 | 59 处 `MessageBox.Show` 散落各 View，与 MVVM 同批，SPEC §11 明确排除在本轮外 |
| 超大文件（P0/P1） | 未做 | 1k 行以上还有 5 个；本轮只拆了引擎那一处，其余属于功能重构 |
| CLI 仍带桌面框架 | 未做 | WPF 已拿掉，`Microsoft.WindowsDesktop.App` 还在——GDI+ 有两处要用它（`Core` 的 DDS 渲染、`Cli` 自己的 `DdsTextReplacer`），要先隔离这层依赖才能换回纯 `net10.0` |
| 断网手工验证 | 未做 | 要真拔网线才测得到；桩 handler 测试已覆盖同一路径 |
| P2-3 文案集中 / P2-4 GitHub 侧确认 | 未动 / 未验证 | 前者靠日后自觉，后者需本人在仓库设置里看一次 |
| `_.index.bin` 的 115 MB 还原点 | 仍在游戏目录 `Bundles2\backup\` | 是否清走还没拍板；留着是可逆的一侧，删了要还原就没有基线了 |
| `DEBUG-affix-dangling-patched-bundle.md` 里一条绝对安装路径 | 未处理 | 它在 D3 之前就已入库，写的是标准 Steam 安装位置（不含用户名/账号/机器名），敏感度低。要么把它并进 `docs/` 一起脱敏，要么明确「DEBUG 笔记类不受此规约束」——需要一次拍板，别默认 |
