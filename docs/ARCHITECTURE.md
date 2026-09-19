# PoE Toolbox 架构总览

- 日期：2026-09-19
- 代码基线：`9290f15a9`（main），版本 `version.json` = 0.2.3
- 实测：Release 全量测试 **192 通过 / 0 失败**（用例数会随后续提交增长，只作基线参考）；`dotnet publish` 出单个 `PoEToolbox.exe`
- 范围：结构、依赖方向与运行期不变式。**不写行号**——本项目行号在一次提交内就漂移过，一律以类型名 / 唯一字符串定位

> 本文回答「东西在哪、谁能引用谁、哪几条规矩破了自己会死」。
> 具体功能设计看 `DESIGN-fx-patch-engine.md`、`DESIGN-bundles2-storage.md`；
> 写作流程看 `GUIDE-patch-authoring.md`；已知结构债看 `REVIEW-software-engineering.md` 的风险清单。

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

**允许的引用方向只有自上而下**。唯一反例是已知的结构债：`Plugins.Voyager → Plugins.BagCleaner`（见 §6）。

三条不能破的规则：

| 规则 | 为什么 | 破掉的后果 |
|---|---|---|
| `Shared` **不能**引用 `Core` | `Shared` 是插件与 CLI 的共同底座，`Core` 在它上面 | 循环依赖，插件工程全部连带重编 |
| 基础库（`Lib*`）保持 `net10.0`、不引用任何 `PoEToolbox.*` | 它们是格式读写层，要能被独立测试与复用 | 一旦引用上层就再也拿不出去，还带上了 Windows 桌面框架 |
| WPF 只允许出现在 `App`、`Ui`、`Plugins.*`；`Shared` / `Core` / `Abstractions` / `Cli` 都不带 | `Shared` 引用 WPF 时，`Core` 白拿了一个自己没声明的框架引用，CLI 与测试也跟着背上桌面框架 | 这条刚在 W7b/W8（`c8cabcbd4` + `2463179fe`）修完，别退回去。反例自查：`Shared` 去掉 `UseWPF` 当场 CS1069 |

## 2. 工程清单（20 个 csproj）

| 层 | 工程 | TFM | WPF | 职责 |
|---|---|---|---|---|
| 契约 | `Abstractions` | `net10.0-windows` | — | `IPlugin`（只有生命周期与元数据）、`IEventBus`、`ILogger`、`IConfigService`。`IAppState` 不在这里，它在 `Shared/GameSessionState.cs` |
| 共享 | `Shared` | `net10.0-windows` | — | `GameDataAccess`、补丁引擎门面 `FxPatchEngine` + `Fx/` 九模块、`ConfigService`、`FileLogger`、`EventBus`、`PoeDetector`、`NetworkDefaults` |
| 界面共享 | `Ui` | `net10.0-windows` | ✅ | `OutputPanel`、`UiStatus`、`FxEngineRunner`（进程级互斥的引擎执行器）、`IUiPlugin` |
| 领域 | `Core` | `net10.0-windows` | — | datc64 二进制、`SchemaManager`、翻译目录、poe.ninja 抓取管线、DDS 贴图渲染 |
| 入口 | `App` | `net10.0-windows` | ✅ | 主窗、主题（`Themes/`，含 `ThemeManager`）、`PluginManager`；**`AssemblyName` 是 `PoEToolbox`** |
| 入口 | `Cli` | `net10.0-windows` | — | 命令行，仅 `Core` + `LibDat2`；`fx-oilmod` / `fx-patch` 等 |
| 插件 | `PriceTagger` `DataBrowser` `BagCleaner` `Voyager` `TermTranslator` `PoeCnPatch` `Poe2Font` `FxPatch` `AffixWorkbench` | `net10.0-windows` | ✅ | 每个一个 `IUiPlugin` + 若干 `UserControl` |
| 基础 | `LibGGPK3` `LibBundle3` `LibDat2` `LibBundledGGPK3` | `net10.0` | — | GGPK / Bundles2 / DAT 的格式读写；不依赖上层 |
| 测试 | `PoEToolbox.Tests` | `net10.0-windows` | — | 192 条；引用 `Core` + 4 个插件 + `Shared`，**不引用 `App` / `Ui` / `Cli`** |

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
| 11 | **`Debug.Fail` 类防线要在测试里被断言，而不是被跳过**。.NET 的 `Debug.Fail` 走 `Trace.Listeners` 派发，测试主机把它翻成异常才让用例挂；测试期间临时换上自己的监听器就能既躲开异常、又断言「守卫确实响了」（`CapturedDebugFail`）。注意 `#if DEBUG` 里的断言 **CI 收不到**——CI 只跑 Release | `tests/.../CapturedDebugFail.cs`、`LibBundle3/Index.cs` 的 `Dispose` |

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
| 插件间直接依赖 | `Voyager` 复用 `BagCleaner` 的 P/Invoke、`GridCalculator`、`Models`、`Services`（8 处 `using`）。**是真依赖，不是误引**，正确解法是把这几样下沉到 `Shared`/`Core` | 报告 P1-5 |
| UI 组织 | 59 处 `MessageBox.Show` 散落各 View，无 `IDialogService`；插件 View 多为 code-behind 而非 ViewModel | 报告 P1-1 / P2-6 |
| 超大文件 | 单文件 1k 行以上还有 5 个：`LibBundle3/Index.cs`、`AffixWorkbenchView`、`DataBrowserView`、`CsdDocument`、`FxPatchView`（基线时分别约 1450/1380/1350/1030/1020 行，不逐次更新，别当准数用） | 报告 P0/P1 |
| 日志器单例不吃测试缝 | `FileLogger.App` 在第一次被触碰时就按当时的根目录建好了文件句柄，之后改注入缝不影响它。断言日志内容请订阅 `FileLogger.EntryLogged`，不要去读日志文件 | — |
| 未被使用的上游 API | `LibDat2.DatContainer.DownloadSchemaMin()`（`SchemaMin` 全仓无人置真）与 `LibGGPK3.PatchClient.UpdateNodeAsync`（无调用方）**不是本项目的死代码**：两者都随 `src/Lib*` 一起从上游 vendored 进来、初版提交（`02ef81c47`）就存在，是库对外的公共 API。删它们只是增加与上游的分歧（本项目已在 `LibBundle3/Index.cs` 带着注释改过上游 bug，分歧要省着用），留着也不占运行时时。**唯一的实际风险是下一个人照它们做设计**，所以在此标注而不是删除 | SPEC §4.3 |
| CLI 仍需桌面框架 | `Core` 的 DDS 渲染用 GDI+，且 `Cli` 的 TFM 本身是 `net10.0-windows`；WPF 已经拿掉了，桌面框架还没拿掉 | SPEC §11 |
