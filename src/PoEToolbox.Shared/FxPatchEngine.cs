using System.Security.Cryptography;
using PoEToolbox.Shared;
using System.IO;

/// <summary>
/// 通用特效补丁引擎（fx-patch 范式）。
/// 补丁 = 一个声明式 .patch.json（记录对逻辑文件的修改 + 目标 bundle 名），
/// 引擎按描述就地修改索引：幂等、三态判定、冲突中止、独立 PATCHED bundle、自动备份。
/// fx-oilmod = 引擎的内置补丁实例入口（地面燃烧特效 oil-ground-fx-lite、黏油榴弹特效 oil-grenade-fx-lite）。
/// CLI 直接调用 Run()；UI 设置 <see cref="LogSink"/> 后调用即可捕获全部输出。
/// </summary>
public static class FxPatchEngine
{
    private static readonly AsyncLocal<Action<string>?> LogSinkCurrent = new();

    /// <summary>UI 日志钩子：设置后所有引擎输出同时流入该回调（CLI 不设置，走 Console）。
    /// 按异步流隔离：两个 UI 视图并发执行引擎时各自的回调互不覆盖。</summary>
    public static Action<string>? LogSink
    {
        get => LogSinkCurrent.Value;
        set => LogSinkCurrent.Value = value;
    }

    /// <summary>引擎输出：CLI 走 Console，UI 设了 <see cref="LogSink"/> 后进回调。
    /// 拆出去的 Fx/* 模块（同一程序集）复用这一个出口，避免各模块自己 Console.WriteLine。</summary>
    internal static void Log(string msg)
    {
        LogSink?.Invoke(msg);
        Console.WriteLine(msg);
        FileLogger.App.Info($"[fx-patch] {msg}");
    }

    internal static void LogErr(string msg)
    {
        LogSink?.Invoke("[错误] " + msg);
        Console.Error.WriteLine(msg);
        FileLogger.App.Error($"[fx-patch] {msg}");
    }

    /// <summary>内置补丁选择器：fx-oilmod 省略补丁 ID 时对全部内置补丁生效。</summary>
    public const string BuiltInAll = "all";

    /// <summary>Index-relative directory every patch bundle lives in.</summary>
    internal const string PatchBundleDirectory = "PATCHED/";

    // ═══ 补丁描述模型 ═══ 已提到 Fx/FxPatchModel.cs（命名空间级），这里不再重复定义
    // ═══ 补丁 bundle 路径 ═══ 见 Fx/FxPatchIdentity.cs

    // ═══ 入口 ═══════════════════════════════════════════════════
    public static int Run(string[] args, string? builtInPatchId)
    {
        // 内置补丁：fx-oilmod <game-data> [patch-id|all] <status|list|apply|revert|cleanup|purge|restore>（省略 patch-id = 全部）
        // 通用补丁：fx-patch <game-data> <patch.json|patch.zip> <status|apply|revert|cleanup|purge>
        //           fx-patch diff <vanilla> <modified> [-o dir] [--id x] [--bundle y] [--version v]
        if (builtInPatchId is not null)
        {
            if (args.Length is not (2 or 3)) { Usage(builtIn: true); return 2; }
            var action = args[^1].ToLowerInvariant();
            if (action is not ("status" or "list" or "apply" or "revert" or "cleanup" or "purge" or "restore")) { Usage(builtIn: true); return 2; }
            try
            {
                return RunBuiltIn(args[0], args.Length == 3 ? args[1] : BuiltInAll, action);
            }
            catch (Exception ex)
            {
                LogErr($"Error: {ex.Message}");
                return 1;
            }
        }

        if (args.Length > 0 && args[0].Equals("diff", StringComparison.OrdinalIgnoreCase))
            return FxDiff.Run(args[1..]);
        if (args.Length != 3) { Usage(builtIn: false); return 2; }
        var gameData = args[0];
        // 用户提供的补丁文件原始路径（zip 或 .patch.json）：账本记下它，
        // 界面才能在不开索引的前提下对这个第三方补丁直接还原 / 卸载。
        var sourceFile = Path.GetFullPath(args[1]);
        var actionCustom = args[2].ToLowerInvariant();
        if (actionCustom is not ("status" or "apply" or "revert" or "cleanup" or "purge")) { Usage(builtIn: false); return 2; }

        PatchDef? patch = null;
        string? patchDir = null;
        string? rawIdHint = null;
        try
        {
            var path = args[1];
            if (!File.Exists(path))
            {
                LogErr($"补丁描述文件不存在: {path}");
                return 1;
            }
            // 支持分发为 zip 压缩包（目录压缩而成）：解压到临时目录后按普通补丁处理
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                rawIdHint = Path.GetFileNameWithoutExtension(path);
                var extracted = FxPatchPackage.ExtractZipPatch(path, out var dir);
                if (extracted is not null)
                    path = extracted;
                else if (dir is not null)
                    patchDir = dir; // 没有 patch.json：交给下面的整包替换识别
                else
                    return 1;
            }
            if (patchDir is null)
            {
                patch = FxPatchPackage.LoadPatchFile(path);
                patchDir = patch.BasePath;
                rawIdHint = patch.PatchId;
            }
        }
        catch (Exception ex)
        {
            LogErr($"补丁描述加载失败: {ex.Message}");
            return 1;
        }

        // 整包替换型补丁：包里带着 _.index.bin 与其他 .bin（bundle），应用 = 备份后整体覆盖同名文件，
        // 不走索引级 op。这类补丁的作者通常直接改客户端文件后打包，没有 patch.json。
        if (patchDir is not null && FxRawPackPatch.TryDetectRawPack(patchDir, rawIdHint) is { } rawPack)
            return FxRawPackPatch.RunRawPack(GameDataLoader.ResolvePath(gameData), rawPack, actionCustom, sourceFile);

        if (patch is null)
        {
            LogErr("压缩包内既没有补丁描述（patch.json / *.patch.json），也不是整包替换补丁（需含 _.index.bin 与其他 .bin 文件）。");
            return 1;
        }

        try
        {
            return DispatchPatch(GameDataLoader.ResolvePath(gameData), patch, actionCustom, sourceFile);
        }
        catch (Exception ex)
        {
            LogErr($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>内置补丁（fx-oilmod）分发：cleanup 跨补丁跑一次；其余动作按选定补丁逐个执行。</summary>
    private static int RunBuiltIn(string gameData, string selector, string action)
    {
        List<BuiltInPatchDef> selected;
        if (selector.Equals(BuiltInAll, StringComparison.OrdinalIgnoreCase))
        {
            selected = FxBuiltInPatches.BuiltIns.ToList();
        }
        else
        {
            var hit = FxBuiltInPatches.BuiltIns.FirstOrDefault(p => p.Id.Equals(selector, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                LogErr($"未知内置补丁: {selector}（可用: {string.Join("、", FxBuiltInPatches.BuiltIns.Select(p => $"{p.DisplayName} {p.Id}"))}，或 {BuiltInAll} 表示全部）");
                return 2;
            }
            selected = [hit];
        }

        var resolved = GameDataLoader.ResolvePath(gameData);
        if (action == "cleanup")
            return FxPatchCommands.CmdCleanup(resolved);
        if (action == "restore")
            return FxPatchCommands.CmdRestoreBaselineFull(resolved);
        if (action == "list")
            return FxPatchCommands.CmdStatusList(resolved);

        var exit = 0;
        foreach (var def in selected)
        {
            PatchDef patch;
            try
            {
                patch = FxBuiltInPatches.LoadBuiltInPatch(def);
            }
            catch (Exception ex)
            {
                LogErr($"内置补丁 {def.Id} 加载失败: {ex.Message}");
                exit = 1;
                continue;
            }
            var code = DispatchPatch(resolved, patch, action);
            if (code != 0)
                exit = code;
        }
        return exit;
    }

    private static int DispatchPatch(string resolved, PatchDef patch, string action, string? sourceFile = null)
        => action switch
        {
            "status" => FxPatchCommands.CmdStatus(resolved, patch),
            "apply" => FxPatchCommands.CmdApplyOrRevert(resolved, patch, apply: true, sourceFile),
            "revert" => FxPatchCommands.CmdApplyOrRevert(resolved, patch, apply: false, sourceFile),
            "cleanup" => FxPatchCommands.CmdCleanup(resolved),
            _ => FxPatchCommands.CmdPurge(resolved, patch),
        };

    private static void Usage(bool builtIn)
    {
        LogErr(builtIn
            ? $"Usage: fx-oilmod <game-data> [patch-id|{BuiltInAll}] <status|list|apply|revert|cleanup|purge|restore>（省略 patch-id 对全部内置补丁执行；restore = 彻底还原官方客户端：整包替换型补丁 + 全部补丁文件与索引一并复原；可用: {string.Join(", ", FxBuiltInPatches.BuiltIns.Select(p => p.Id))}）"
            : "Usage: fx-patch <game-data> <patch.json|patch.zip> <status|apply|revert|cleanup|purge>\n       fx-patch diff <原版index.bin> <修改后index.bin> [-o 目录] [--id x] [--bundle y] [--version v] [--zip]");
    }

    // ═══ datc64 原始字节引擎 ═══ 已提到 Fx/FxDatc64Pointers.cs

    // ═══ 状态判定与单 op 执行 ═══ 已提到 Fx/FxPatchState.cs 与 Fx/FxPatchOperations.cs

    // ═══ 子命令（status / apply / revert / cleanup / purge / restore）═══ 已提到 Fx/FxPatchCommands.cs

    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
