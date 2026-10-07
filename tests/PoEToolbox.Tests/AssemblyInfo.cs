using Xunit;

/// <summary>
/// 不变式 9（README「必须保持的不变式」表）：本文件就是那条规则的落点。
/// 整个测试程序集关并行。理由不是「并行慢」——实测并行 26~36s、串行 37s，这套测试是 I/O 受限的，
/// 并行几乎没换来时间——而是进程级静态太多，跨 collection 的并发会互相毁产物：
/// `ConfigService.DataDirectoryOverride` 只被挂了 collection 的四个类串行住，
/// 但 `FxPatchPackage.ExtractZipPatch` 的解压根目录同样由它派生，
/// 于是「不在该组」的 `FxDiffPackagingTests` 会把补丁解压进别人正在用的临时树，
/// 对方 `Dispose` 递归删目录，它的文件就凭空消失（实测 3 次里挂 3 次，且每次挂的不是同一条）。
///
/// collection 归属因此变成了一张必须手工维护、且漏一个就偶发挂的清单。关掉并行是更便宜的不变式。
/// 下面的 [Collection] 标注保留——它现在记录「哪些类共享同一个静态」，将来若要重新开启并行，
/// 必须先按这条注释把清单补全。
/// </summary>
[assembly: CollectionBehavior(DisableTestParallelization = true)]
