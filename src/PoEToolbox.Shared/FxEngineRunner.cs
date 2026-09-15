using System.IO;
using System.Threading;

namespace PoEToolbox.Shared;

/// <summary>
/// 引擎执行器：busy 防重入、路径校验、日志钩子装卸与结果汇总。
/// busy 标志是进程级的——「特效补丁」「创建补丁」「词缀上色」等视图同时执行引擎会
/// 并发写同一份游戏数据，必须互斥（原先在 Plugins.FxPatch 内，词缀上色加入后上移 Shared）。
/// 引擎异常统一就地回显到输出面板，不再穿透到全局错误处理。
/// </summary>
internal static class FxEngineRunner
{
    private static int _busy;

    public sealed record Invocation(string? BuiltInId, string[] Args);

    /// <summary>引擎是否正在执行（跨视图共享）。</summary>
    public static bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>由宿主（主窗口）注册：引擎执行前广播释放所有插件持有的游戏数据句柄。
    /// 任何模块的只读映射都会阻塞引擎对 _.index.bin 的整文件替换（ERROR_USER_MAPPED_FILE）。</summary>
    public static Action? ReleaseExternalLocks;

    public static async Task<bool> RunAsync(
        string action,
        IReadOnlyList<Invocation> invocations,
        string? gameDataPath,
        bool skipGameData,
        Action clearLog,
        Action<string> appendLog,
        Action<string, UiStatus.Kind> setStatus,
        Action<bool> setBusy)
    {
        var gameData = (gameDataPath ?? GameDataPathPreference.Get())?.Trim();
        if (!skipGameData
            && (string.IsNullOrWhiteSpace(gameData)
                || (!File.Exists(gameData) && !Directory.Exists(gameData))))
        {
            setStatus("游戏数据路径无效。", UiStatus.Kind.Warning);
            return false;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            setStatus("已有任务在执行，请稍候。", UiStatus.Kind.Warning);
            return false;
        }

        try
        {
            // 执行前广播（UI 线程）：各插件释放映射，避免阻塞索引替换
            try
            {
                ReleaseExternalLocks?.Invoke();
            }
            catch (Exception ex)
            {
                appendLog($"[警告] 释放插件游戏数据句柄失败：{ex.Message}");
            }

            return await RunLockedAsync(action, invocations, skipGameData, clearLog, appendLog, setStatus, setBusy);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task<bool> RunLockedAsync(
        string action,
        IReadOnlyList<Invocation> invocations,
        bool skipGameData,
        Action clearLog,
        Action<string> appendLog,
        Action<string, UiStatus.Kind> setStatus,
        Action<bool> setBusy)
    {
        setBusy(false);
        clearLog();
        setStatus($"{action}……执行中（打开索引需要数秒）", UiStatus.Kind.Neutral);

        var failed = 0;
        var lastExitCode = 0;
        Exception? engineFailure = null;
        await Task.Run(() =>
        {
            // AsyncLocal 钩子按异步流隔离，并发执行时各视图的输出不会串线
            FxPatchEngine.LogSink = appendLog;
            FxDiff.LogSink = appendLog;
            try
            {
                foreach (var invocation in invocations)
                {
                    if (invocations.Count > 1)
                        appendLog($"──── {invocation.BuiltInId ?? Path.GetFileName(invocation.Args[1])} ────");
                    lastExitCode = FxPatchEngine.Run(invocation.Args, invocation.BuiltInId);
                    if (lastExitCode != 0)
                        failed++;
                }
            }
            catch (Exception ex)
            {
                engineFailure = ex;
            }
            finally
            {
                FxPatchEngine.LogSink = null;
                FxDiff.LogSink = null;
            }
        });

        setBusy(true);

        if (engineFailure is not null)
        {
            appendLog("");
            appendLog($"════════════ ❌ {engineFailure.Message} ════════════");
            setStatus($"❌ {action}执行出错：{engineFailure.Message}", UiStatus.Kind.Error);
            FileLogger.App.Error($"UI {action}: engine threw an exception.", engineFailure);
            return false;
        }

        if (failed == 0)
        {
            setStatus($"✅ {action}成功。", UiStatus.Kind.Success);
            FileLogger.App.Info($"UI {action}: success.");
            appendLog("");
            appendLog($"════════════ ✅ {action}成功 ════════════");
            return true;
        }

        var detail = invocations.Count > 1
            ? $"（成功 {invocations.Count - failed} 个，失败 {failed} 个）"
            : $"（退出码 {lastExitCode}，详见下方引擎输出）";
        setStatus($"❌ {action}未完成{detail}。", UiStatus.Kind.Error);
        FileLogger.App.Error($"UI {action}: {failed}/{invocations.Count} invocation(s) failed.");
        appendLog("");
        appendLog($"════════════ ❌ {action}未完成{detail} ════════════");
        return false;
    }
}
