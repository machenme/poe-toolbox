using System.IO;
using System.Text;

namespace PoEToolbox.Shared;

/// <summary>
/// 内置特效补丁：注册表、程序内嵌资源的读取与释放、以及给 UI 用的状态快照查询。
/// 补丁描述本身仍然是普通的 <see cref="PatchDef"/>——内置只表示「来源在程序里」。
/// </summary>
/// <remarks>
/// S5 从 <see cref="FxPatchEngine"/> 原样搬出，方法体未改；日志与状态判定仍回调引擎
/// （<c>FxPatchEngine.Log</c> / <c>FxPatchState.ComputeStates</c>），保证 CLI 与 UI 看到的是同一套口径。
/// 命令行入口 <c>RunBuiltIn</c> 与选择器常量 <c>FxPatchEngine.BuiltInAll</c> 留在门面里。
/// </remarks>
internal static class FxBuiltInPatches
{
    /// <summary>内置补丁注册表：GUI 下拉菜单与 CLI 共用。新增内置补丁在这里登记即可。</summary>
    internal static readonly BuiltInPatchDef[] BuiltIns =
    [
        new("oil-ground-fx-lite", "地面燃烧特效", "oil-ground-fx-lite.patch.json"),
        new("oil-grenade-fx-lite", "黏油榴弹特效", "oil-grenade-fx-lite.patch.json"),
    ];

    /// <summary>加载内置补丁：先把程序内嵌的那份释放到工具箱数据目录（AppData），再从磁盘读。
    /// 来源只有一处（内嵌资源），不再依赖 exe 旁有没有 Patches 目录，部署缺文件的问题不复现。</summary>
    internal static PatchDef LoadBuiltInPatch(BuiltInPatchDef def)
    {
        var path = MaterializeBuiltInPatch(def);
        if (path is not null)
            return FxPatchPackage.LoadPatchFile(path);

        // 释放失败（目录不可写）时退回直接读内嵌资源，功能不受影响，只是「导出」按钮没了落盘来源
        var embedded = ReadEmbeddedPatchJson(def.FileName);
        if (embedded is not null)
            return FxPatchPackage.ParsePatchJson(embedded, basePath: null);

        throw new FileNotFoundException($"找不到内置补丁描述文件：{def.FileName}（程序内嵌资源缺失）。");
    }

    /// <summary>内嵌补丁资源的名字前缀，与 csproj 的 EmbeddedResource LogicalName 对齐。</summary>
    private const string EmbeddedPatchPrefix = "Patches.";

    /// <summary>读程序内嵌的补丁描述 JSON（<see cref="EmbeddedPatchPrefix"/> + 文件名）；没嵌入返回 null。</summary>
    private static string? ReadEmbeddedPatchJson(string fileName)
    {
        var asm = typeof(FxBuiltInPatches).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n =>
            n.Equals(EmbeddedPatchPrefix + fileName, StringComparison.Ordinal)
            || n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>内置补丁在工具箱数据目录（AppData）里的释放位置。</summary>
    private static string BuiltInPatchDirectory => Path.Combine(ConfigService.PatchesDirectory, "builtin");

    /// <summary>把内嵌的内置补丁描述释放到工具箱数据目录并返回其路径；释放失败返回 null。
    /// 已存在且内容一致就跳过写入；内容不同（工具升级改了补丁）以程序内嵌版本为准覆盖。</summary>
    private static string? MaterializeBuiltInPatch(BuiltInPatchDef def)
    {
        var json = ReadEmbeddedPatchJson(def.FileName);
        if (json is null)
            return null;
        try
        {
            var dir = BuiltInPatchDirectory;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, def.FileName);
            if (!File.Exists(target) || !string.Equals(File.ReadAllText(target), json, StringComparison.Ordinal))
                File.WriteAllText(target, json, new UTF8Encoding(false));
            return target;
        }
        catch (Exception ex)
        {
            FileLogger.App.Warn($"[fx-patch] 内置补丁 {def.Id} 释放到数据目录失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>按 id 解析内置补丁描述文件路径（导出补丁文件用）；补丁不存在或无法释放返回 null。</summary>
    public static string? TryResolveBuiltInPatchPath(string patchId)
    {
        var def = BuiltIns.FirstOrDefault(b => b.Id.Equals(patchId, StringComparison.OrdinalIgnoreCase));
        return def is null ? null : MaterializeBuiltInPatch(def);
    }

    /// <summary>只读扫描全部内置补丁的启用状态（UI 靠它把已启用的补丁默认勾选上）。
    /// 判定口径与 <c>status</c> 命令一致，但不打印日志，返回结构化结果。</summary>
    internal static List<BuiltInPatchStatus> QueryBuiltInStatus(string gameDataPath)
    {
        var resolved = GameDataLoader.ResolvePath(gameDataPath);
        using (var gd = GameDataAccess.OpenReadOnlyMapped(resolved))
        {
            var result = new List<BuiltInPatchStatus>(BuiltIns.Length);
            foreach (var def in BuiltIns)
            {
                PatchDef patch;
                try
                {
                    patch = LoadBuiltInPatch(def);
                }
                catch (Exception ex)
                {
                    result.Add(new BuiltInPatchStatus(def.Id, def.DisplayName, PatchState.Incompatible,
                        $"补丁描述加载失败：{ex.Message}"));
                    continue;
                }
                var states = FxPatchState.ComputeStates(gd, patch);
                result.Add(new BuiltInPatchStatus(def.Id, def.DisplayName, FxPatchState.OverallOf(states), FxPatchState.DetailOf(states)));
            }
            return result;
        }
    }
}
