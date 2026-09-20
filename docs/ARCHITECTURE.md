# PoE Toolbox 架构总览

- 日期：2026-09-20
- 代码基线：`77f830b81`（main，PRD D6 与「不再自动探测游戏数据」「界面布局放大」两项变更落地那一提交），版本 `version.json` = 0.2.3
- 实测：全量测试 **200 通过 / 0 失败**，Release 34s、Debug 34s，CI 两条腿各跑一次（用例数会随后续提交增长，只作基线参考）；`dotnet publish` 出单个 `PoEToolbox.exe`
- 范围：结构、依赖方向与运行期不变式。**不写行号**——本项目行号在一次提交内就漂移过，一律以类型名 / 唯一字符串定位

> 本文回答「东西在哪、谁能引用谁、哪几条规矩破了自己会死」。
> 具体功能设计看 `DESIGN-fx-patch-engine.md`、`DESIGN-bundles2-storage.md`；
> 写作流程看 `GUIDE-patch-authoring.md`；已知结构债看 `REVIEW-software-engineering.md` 的风险清单；
> 本轮工程加固为什么这样定，看 `PRD-engineering-hardening.md`（拍板）与 `SPEC-engineering-hardening.md`（计划）；
> 只想看「加固前后差在哪」，看 `REVIEW-engineering-hardening-before-after.md`（对照，不含理由）。

---

## 1. 分层

```
PoEToolbox.App        (exe, WPF; AssemblyName = PoEToolbox)
 └─ 全部 9 个插件工程 + Ui + Shared                     ← 组合根，唯一「知道所有人」的地方
PoEToolbox.Cli        (无 WPF)
 └─ Core + LibDat2                                     ← 闭包里没有任何 UI 程序集
Plugins.* × 9         (WPF)
 └─ Ui；按需 Shared / Core / Abstractions / LibDat2 / LibBundledGGPK3
PoEToolbox.Ui         (WPF)
 └─ Shared                                             ← OutputPanel / UiStatus / FxEngineRunner / IUiPlugin
PoEToolbox.Core
 └─ Shared + LibBundledGGPK3                           ← 额外声明 WindowsForms 分组只为 GDI+ 渲染
PoEToolbox.Shared
 └─ Abstractions + LibBundledGGPK3 + LibDat2
PoEToolbox.Abstractions
 └─ 无引用                                              ← 纯契约
Lib* × 4              (net10.0)
 └─ 互有引用，均不引用任何 PoEToolbox.*
PoEToolbox.Tests
 └─ Core + Shared + 4 个插件工程（不引用 App / Ui / Cli）
```

**允许的引用方向只有自上而下**，插件之间没有任何直接引用。历史上唯一反例是 `Plugins.Voyager → Plugins.BagCleaner`（报告 P1-5），现已拆掉：纯 Win32/GDI 的输入与网格类型下沉到 `Core`，WPF 热键服务留在 BagCleaner、由 `Abstractions.IHotkeyServiceFactory` 倒置注入。

三条不能破的规则：

| 规则 | 为什么 | 破掉的后果 |
|---|---|---|
| `Shared` **不能**引用 `Core` | `Shared` 是插件与 CLI 的共同底座，`Core` 在它上面 | 循环依赖，插件工程全部连带重编 |
| 基础库（`Lib*`）保持 `net10.0`、不引用任何 `PoEToolbox.*` | 它们是格式读写层，要能被独立测试与复用 | 一旦引用上层就再也拿不出去，还带上了 Windows 桌面框架 |
| WPF 只允许出现在 `App`、`Ui`、`Plugins.*`；`Shared` / `Core` / `Abstractions` / `Cli` 都不带 | `Shared` 引用 WPF 时，`Core` 白拿了一个自己没声明的框架引用，CLI 与测试也跟着背上桌面框架 | 这条刚在 W7b/W8（`c8cabcbd4` + `2463179fe`）修完，别退回去。反例自查：`Shared` 去掉 `UseWPF` 当场 CS1069 |

## 2. 工程清单（20 个 csproj）

| 层 | 工程 | TFM | WPF | 职责 |
|---|---|---|---|---|
| 契约 | `Abstractions` | `net10.0-windows` | — | `IPlugin`（只有生命周期与元数据）、`IEventBus`、`ILogger`、`IConfigService`、`IHotkeyService` + `IHotkeyServiceFactory`（热键契约，WPF 实现在 `BagCleaner`）。`IAppState` 不在这里，它在 `Shared/GameSessionState.cs` |
| 共享 | `Shared` | `net10.0-windows` | — | `GameDataAccess`、补丁引擎门面 `FxPatchEngine` + `Fx/` 九模块、`ConfigService`、`FileLogger`、`EventBus`、`PoeDetector`、`NetworkDefaults` |
| 界面共享 | `Ui` | `net10.0-windows` | ✅ | `OutputPanel`、`UiStatus`、`FxEngineRunner`（进程级互斥的引擎执行器）、`IUiPlugin` |
| 领域 | `Core` | `net10.0-windows` | — | datc64 二进制、`SchemaManager`、翻译目录、poe.ninja 抓取管线、DDS 贴图渲染、`Input/`（Win32 鼠标键盘模拟）、`ScreenGrid/`（客户区比例 ↔ 屏幕绝对坐标） |
| 入口 | `App` | `net10.0-windows` | ✅ | 主窗、主题（`Themes/`，含 `ThemeManager`）、`PluginManager`；**`AssemblyName` 是 `PoEToolbox`** |
| 入口 | `Cli` | `net10.0-windows` | — | 命令行，仅 `Core` + `LibDat2`；`fx-oilmod` / `fx-patch` 等 |
| 插件 | `PriceTagger` `DataBrowser` `BagCleaner` `Voyager` `TermTranslator` `PoeCnPatch` `Poe2Font` `FxPatch` `AffixWorkbench` | `net10.0-windows` | ✅ | 每个一个 `IUiPlugin` + 若干 `UserControl` |
| 基础 | `LibGGPK3` `LibBundle3` `LibDat2` `LibBundledGGPK3` | `net10.0` | — | GGPK / Bundles2 / DAT 的格式读写；不依赖上层 |
| 测试 | `PoEToolbox.Tests` | `net10.0-windows` | — | 200 条；引用 `Core` + 4 个插件 + `Shared`，**不引用 `App` / `Ui` / `Cli`** |

## 3. 运行期数据流

**A. 浏览 / 编辑游戏数据（读路径）**

```
插件 View / CLI → GameDataLoader.Use(...) 或直接 GameDataAccess.Open / OpenReadOnlyMapped
                          → LibDat2 / LibGGPK3 / LibBundle3
                                   ↑
                     dat 定义：LibDat2 的内嵌资源（不是外部 json）
                     列语义：Core/Schema/SchemaManager（schema.min.json，缺文件时才联网拉）
```
`GameDataAccess` 是唯一的持锁入口：只读映射、进程内引用计数、`Dispose` 时按需触发回收。上层优先用
`GameDataLoader.Use<T>(path, mode, ...)`，它把 open/use/dispose 收在一个调用里。

**B. 打补丁（写路径，唯一会改游戏索引的路径）**

```
UI（FxPatchView / 词缀上色 / 创建补丁）→ Ui.FxEngineRunner ─┐
Cli（fx-oilmod / fx-patch）────────────────────────────────┴→ FxPatchEngine.Run(args, builtInId)
                                                                    ↓
                                        Shared/Fx/：Commands（命令分发）· Operations（op 执行）·
                                          Package（解压/校验）· Identity（内容判据）· State（三态归约）·
                                          Model / Datc64Pointers / RawPackPatch / BuiltInPatches
                                                                    ↓
                                        IndexBackupService（改前备份）+ GameDataAccess（写）
```

**C. 物价 / 翻译（联网路径）**

```
PriceTagger / AffixWorkbench → Core/Pipeline/PoeNinjaFetcher → NetworkDefaults.RequestTimeout
                                失败：按分类降级 + NetworkDefaults.DescribeFailure 出中文原因
```
超时与用户主动取消**必须**区分：`OperationCanceledException` 不带 `ct.IsCancellationRequested` 时是超时，
按超时降级；是取消时立刻上抛。混在一起会让一次超时中断整轮十几个分类。

## 4. 不变式（破坏方式是「自己下次改的时候踩」）

| # | 不变式 | 依据 |
|---|---|---|
| 1 | **引擎执行互斥是进程级的**。`FxEngineRunner` 的 busy 标志在 `Ui` 里，多个视图共用同一把锁——并发跑引擎会同时写同一份游戏数据 | `Ui/FxEngineRunner.cs` |
| 2 | **引擎跑之前必须广播释放句柄**。任何模块留着的只读映射都会让 `_.index.bin` 的整文件替换失败（`ERROR_USER_MAPPED_FILE`）。宿主在 `MainWindow` 构造时注册 `FxEngineRunner.ReleaseExternalLocks`，各插件在里面释放自己持有的 `GameDataAccess` | `Ui/FxEngineRunner.cs`、`App/MainWindow.xaml.cs` |
| 3 | **内存回收挂在 `Dispose` 上，不要在调用点手工 `Reclaim()`**。`GameDataAccess` 用引用计数 + `CreateAbortCheck()` 保证「还有人持锁就不回收」 | `Shared/GameDataAccess.cs`、`MemoryReclaimer.cs` |
| 4 | **补丁状态是三态归约，不是布尔**。「未打 / 已打 / 冲突（部分生效）」由 `FxPatchState` 的纯函数算出，UI 只做展示。改判定要同时看那 9 条性质测试 | `Shared/Fx/FxPatchState.cs` |
| 5 | **写路径必过备份**。改索引前由 `IndexBackupService` 留还原点；`PatchBundleRepair` 负责校验和修复 | `Shared/IndexBackupService.cs` |
| 6 | **插件是编译期静态注册的**，`PluginManager.RegisterAll()` 是唯一真相，没有动态加载。所以程序集名不承担 ABI 含义，改名安全；也所以「给契约加版本号」目前无人消费 | `App/PluginManager.cs` |
| 7 | **`IPlugin` 无界面、`IUiPlugin` 带界面**。`Abstractions` 因此不引用 WPF。导航只列 `OfType<IUiPlugin>()`；无界面插件照样注册、照样收生命周期回调 | `Abstractions/IPlugin.cs`、`Ui/IUiPlugin.cs` |
| 8 | **`InternalsVisibleTo` 要写 `PoEToolbox`，不是 `PoEToolbox.App`**。入口工程的 `AssemblyName` 与 csproj 文件名不同名 | `App/PoEToolbox.App.csproj` |
| 9 | **测试程序集整体关并行**（`tests/.../AssemblyInfo.cs` 的 `DisableTestParallelization`）。进程级静态太多（路径注入缝、日志器静态事件、回收链、游戏数据），而 xUnit 默认只串行化**同一 collection 内**的类——跨组的并发照样能毁产物：`FxDiffPackagingTests` 会把补丁解压进别的类正在用的临时树，对方 `Dispose` 递归删目录，它的文件就凭空消失。实测并行 26~36s、串行 34~37s，I/O 受限下并行没换来时间。**`[Collection]` 标注保留**，作用是记录哪些类共享哪个静态；若将来要重新开启并行，必须先照这条把清单补全 | `tests/.../AssemblyInfo.cs`、`AffixColorSchemeTests.cs`（`ConfigPathTestCollection`，四个类同挂） |
| 10 | **进程级静态事件的订阅者不要直接枚举自己攒的列表**。`FileLogger.EntryLogged` 谁记一条都会回调，包括后台线程；断言前先加锁取快照 | `tests/.../ConfigServiceTests.cs` |
| 11 | **`Debug.Fail` 类防线要在测试里被断言，而不是被跳过**。.NET 的 `Debug.Fail` 走 `Trace.Listeners` 派发，测试主机把它翻成异常才让用例挂；测试期间临时换上自己的监听器就能既躲开异常、又断言「守卫确实响了」（`CapturedDebugFail`）。CI 有 Release + Debug 两条测试腿，Debug 腿专门为了让 `#if DEBUG` 里的断言在 CI 上真跑一次——只有 Release 覆盖的守卫等于没被守住 | `tests/.../CapturedDebugFail.cs`、`LibBundle3/Index.cs` 的 `Dispose`、`.github/workflows/build.yml` |
| 12 | **插件工程之间不许互相引用**。要复用别的插件里的东西，只有两条路：类型本身是纯 Win32/GDI/领域逻辑就下沉到 `Core`（它有桌面框架引用，System.Drawing 放这里不再增加依赖），或者留在原插件、把契约提到 `Abstractions` 用工厂倒置、由组合根 `PluginManager` 注入。第三条路（`Ui → Core`）会新增一条跨层边，不走 | `Abstractions/IHotkeyServiceFactory.cs`、`Core/Input/`、`Core/ScreenGrid/`（报告 P1-5 的落地形态） |
| 13 | **`revert` 只把语义还原成「未打」，不承诺字节回到原版**；逐字节还原只有 `restore`。实测：文本替换型 revert 后索引与基线 SHA 不同（bundle 885→990 B），整包替换型 revert 后文件副本仍在（bundle 涨到 2,786,283 B），`purge`/`cleanup` 也各自只回收无人引用的部分。UI 文案已按「启用 / 还原 / 卸载 / 彻底还原游戏客户端」四档分开措辞，别合并也别写成「撤销」。另一条实测：打一个补丁会让 115 MB 的索引撑大 4.9%~6.4%，且打过补丁的索引与 Steam 的校验记录不一致——Steam 再校验一次就把索引换回原版而账本还在，所以不变式 4 的三态判定必须读实际索引内容 | 本机写路径实测，`docs/SPEC-engineering-hardening.md` §9.1 |
| 14 | **游戏数据文件没有「自动检测」这条路径**：`Content.ggpk` / `_.index.bin` 只来自用户亲手选过一次，之后存在 `config.json` 的 `CurrentGameDataPath` 里沿用。空配置读出来必须是 `null`，各视图自己拦下并提示「请先选择游戏数据」；`GameDataLoader.ResolvePath` 拿到空路径直接抛 `InvalidOperationException`，**不再回退去扫注册表或默认安装目录**。理由不是洁癖：静默猜一份客户端意味着补丁可能打进用户没打算动的游戏里，而且一旦猜错，补丁账本与 `Bundles2\backup\` 基线都记在那份客户端上。`PoeDetector` 因此只剩进程/窗口检测 | `Shared/GameDataPathPreference.cs`、`Shared/GameDataLoader.cs`、`tests/.../GameDataPathPreferenceTests.cs` |

### 4.1 谁来守它

编号是双向才查得动的：文档指向代码靠上表的「依据」列，代码指回文档靠实现点上的 `不变式 N（docs/ARCHITECTURE.md §4）` 锚点注释。两头都在位时，`grep -rn "不变式 4" src tests` 一次就能同时拿到实现与验证两端。

| # | 守它的测试 | 说明 |
|---|---|---|
| 1 | 无 | 落 `Ui/FxEngineRunner`，测试工程按 §2 有意不引用 `Ui`，结构性测不到 |
| 2 | 无 | 同上（`Ui` + `App`） |
| 3 | `GameDataDisposalTests`、`MemoryReclaimerTests` | 反向有效性由 PRD A3 把关（注释掉一处回收必须变红） |
| 4 | `FxPatchPropertyTests`（9 条性质） | |
| 5 | `PatchBundleRepairTests` | |
| 6 | 无 | 落 `App/PluginManager`，同上 |
| 7 | 无 | 落 `Ui/IUiPlugin`；但 `Abstractions` 一旦引用 WPF 就当场编译失败（§1 第三条） |
| 8 | 编译保证 | `InternalsVisibleTo` 写错，测试读不到 internal 直接编译不过 |
| 9 | `AssemblyInfo.cs` 自身 | 该文件即这条的落点 |
| 10 | `ConfigServiceTests` | |
| 11 | `CapturedDebugFail` 的各调用方 | 只有 CI 的 Debug 腿跑得到 |
| 12 | `PluginReferenceGuardTests` | 读 9 个插件 csproj 的引用列表；实测 14ms 绿，人为加一行跨插件引用即红 |
| 13 | `FxPatchPropertyTests`、`FxRawPackPatchTests` | **只守语义**。撑大 4.9%~6.4% 那组字节数字是 `SPEC` §9.1 的一次性本机实测，没有可重跑的基线 |
| 14 | `GameDataPathPreferenceTests`、`GameDataLoaderTests` | |

14 条里 9 条有自动化、1 条（8）由编译保证、4 条（1、2、6、7）落在测试工程有意不引用的 `App`/`Ui` 层。那 4 条只能靠人工回归（PRD §11 D6 那份目视清单），**别把它们当成「已被测试守住」**。

## 5. 落盘位置与内嵌资源

| 内容 | 位置 | 说明 |
|---|---|---|
| 数据根目录 | `%LocalAppData%\PoEToolbox` | `ConfigService.DataDirectory`，全部子目录由它派生 |
| 配置 | 同上 `config.json` | 按插件分段；原子写（临时文件 + rename）；损坏时改名 `.corrupt.<ts>` 并**记日志**后按空配置继续 |
| 日志 | 同上 `logs/poe-toolbox-yyyyMMdd.log` | 按日滚动，保留 7 天；`FileLogger` 自身的失败**故意**静默 |
| 内置特效补丁 | 同上 `patches/builtin/` | 源是 `Shared/Patches/**/*.patch.json`，**编译进 exe**，运行时释放；exe 旁边不需要任何附加文件 |
| 词缀上色方案 | 同上 `affix-schemes/` | 用户方案与内置「官方原版」方案 |
| 补丁解压临时目录 | 同上，`poe-toolbox-patch-*` | 生命周期由 `FxPatchPackage.CleanStalePatchTempDirs` 管（14 天），不放系统 TEMP |
| schema | 同上 `schema/schema.min.json` | 只有文件不存在时才联网拉；主站失败回落备份站 |
| dat 列定义 | **LibDat2 程序集内嵌资源** | 曾经是外置 json，已内嵌。CI 在出包后断言 publish 目录里没有任何 loose `.json`——跑一次就要读的数据留在外面，功能在用户机器上直接失效 |
| Oodle 原生库 | 内嵌于 `App`，启动时释放到 `%LocalAppData%\PoEToolbox\native\oo2core.dll` | 源文件是仓库根的 `oo2core.dll`，以 `EmbeddedResource` 进 exe；`App.ExtractEmbeddedDll` 按大小比对决定是否重写（升级换库时会重写），再由 `DllImportResolver` 加载 |
| 源码树里的 `*.dll` | `.gitignore` 全忽略，只有仓库根的 `oo2core.dll` 有 `!` 例外 | 后果：往 `src/` 任何目录扔一个 dll 都不会出现在 `git status` 里（曾长期躺着一个 2026-07-27 的 `src/PoEToolbox.Core/LibDat2.dll` 构建残留，无任何工程引用，已清走）。需要入库的二进制必须显式加 `!` 例外，别指望 `git add -f` 之后别人看得见 |

## 6. 已知结构债（不在本文里解决）

| 项 | 现状 | 出处 |
|---|---|---|
| UI 组织 | 59 处 `MessageBox.Show` 散落各 View，无 `IDialogService`；插件 View 多为 code-behind 而非 ViewModel | 报告 P1-1 / P2-6 |
| 超大文件 | 单文件 1k 行以上还有 5 个：`LibBundle3/Index.cs`、`AffixWorkbenchView`、`DataBrowserView`、`CsdDocument`、`FxPatchView`（基线时分别约 1450/1380/1350/1030/1020 行，不逐次更新，别当准数用） | 报告 P0/P1 |
| 日志器单例不吃测试缝 | `FileLogger.App` 在第一次被触碰时就按当时的根目录建好了文件句柄，之后改注入缝不影响它。断言日志内容请订阅 `FileLogger.EntryLogged`，不要去读日志文件 | — |
| 未被使用的上游 API | `LibDat2.DatContainer.DownloadSchemaMin()`（`SchemaMin` 全仓无人置真）与 `LibGGPK3.PatchClient.UpdateNodeAsync`（无调用方）**不是本项目的死代码**：两者都随 `src/Lib*` 一起从上游 vendored 进来、初版提交（`02ef81c47`）就存在，是库对外的公共 API。删它们只是增加与上游的分歧（本项目已在 `LibBundle3/Index.cs` 带着注释改过上游 bug，分歧要省着用），留着也不占运行时时。**唯一的实际风险是下一个人照它们做设计**，所以在此标注而不是删除 | SPEC §4.3 |
| CLI 仍需桌面框架 | GDI+ 有两处：`Core` 的 DDS 渲染，以及 `Cli` 自己的 `DdsTextReplacer`（它的 csproj 还写着一句在 .NET 10 SDK 上空转的 `<UseSystemDrawing>`，能编译其实靠 `Core` 传下来的框架引用）。加上 `Cli` 的 TFM 本身就是 `net10.0-windows`。WPF 已经拿掉了，桌面框架还没拿掉 | SPEC §11 |
