using System.IO;
using System.Text;
namespace PoEToolbox.Shared;

/// <summary>
/// 单条 op 的执行：在已打开的写入通道里改内容 / 加文件 / 重定向指针，返回「是否真的动了字节」。
/// 幂等性靠状态判定先行（<see cref="FxPatchState"/>），这里只负责成对的正反变换。
/// </summary>
/// <remarks>
/// S7 从 <see cref="FxPatchEngine"/> 原样搬出，方法体未改。落盘语义保持不变：
/// 副本文件走 <c>saveIndex: false</c>，由调用方在末尾显式 <c>gd.Save()</c>。
/// </remarks>
internal static class FxPatchOperations
{
    // ═══ 单 op 执行（写入通道内）════════════════════════════════
    /// <summary>按 op 类型分派；返回「是否真的改动了字节」。落盘由调用方在末尾统一 <c>gd.Save()</c>。</summary>
    internal static bool ExecuteOp(GameDataAccess gd, PatchDef patch, PatchOp op, bool apply)
        => op.Op switch
        {
            "addfile-asset" => ExecuteAsset(gd, patch, op, apply),
            "addfile-derived" => ExecuteDerived(gd, patch, op, apply),
            "patchptr-byid" => ExecutePtrById(gd, op, apply),
            "edittext" => ExecuteEditText(gd, op, apply),
            _ => throw new InvalidOperationException($"未知 op 类型 {op.Op}"),
        };

    private static bool ExecuteAsset(GameDataAccess gd, PatchDef patch, PatchOp op, bool apply)
    {
        var assetPath = FxPatchPackage.ResolvePatchFilePath(patch, op.Asset!);
        var newBytes = File.ReadAllBytes(assetPath);
        if (apply)
        {
            if (gd.FileExists(op.Dst!))
            {
                if (FxPatchEngine.Sha(gd.ReadFile(op.Dst!)!) == FxPatchEngine.Sha(newBytes))
                {
                    FxPatchEngine.Log($"[skip] {op.Dst} 内容已与 asset 一致");
                    return false;
                }
                gd.Index.TryGetFile(op.Dst!, out var fr);
                fr!.Write(newBytes);
                FxPatchEngine.Log($"[op] 替换 {op.Dst}（{newBytes.Length:N0} B，asset）");
                return true;
            }
            gd.AddFile(op.Dst!, newBytes, saveIndex: false);
            FxPatchEngine.Log($"[op] 新增 {op.Dst}（{newBytes.Length:N0} B，asset）");
            return true;
        }
        // revert：有原版字节才还原；新增类（无 OriginalAsset）保留
        if (op.OriginalAsset is null)
        {
            FxPatchEngine.Log($"[skip] {op.Dst} 保留（新增内容，无原版字节可还原）");
            return false;
        }
        var origPath = FxPatchPackage.ResolvePatchFilePath(patch, op.OriginalAsset);
        if (!File.Exists(origPath))
            throw new InvalidOperationException($"补丁包内找不到原版 asset {op.OriginalAsset}");
        var origBytes = File.ReadAllBytes(origPath);
        if (!gd.FileExists(op.Dst!) || FxPatchEngine.Sha(gd.ReadFile(op.Dst!)!) == FxPatchEngine.Sha(origBytes))
        {
            FxPatchEngine.Log($"[skip] {op.Dst} 已是原版内容");
            return false;
        }
        gd.Index.TryGetFile(op.Dst!, out var fr2);
        fr2!.Write(origBytes);
        FxPatchEngine.Log($"[op] 还原 {op.Dst}（{origBytes.Length:N0} B，原版 asset）");
        return true;
    }

    private static bool ExecuteDerived(GameDataAccess gd, PatchDef patch, PatchOp op, bool apply)
    {
        // revert：副本保留（无引用即无害；可整目录清理 PATCHED bundle）
        if (!apply)
        {
            FxPatchEngine.Log($"[skip] {op.Dst} 副本保留（还原指针后无引用，随 PATCHED bundle 清理）");
            return false;
        }
        var source = gd.ReadFile(op.Src!) ?? throw new FileNotFoundException($"索引中找不到源文件: {op.Src}");
        var sourceSha = FxPatchEngine.Sha(source);
        if (op.SourceSha256 is not null && !sourceSha.Equals(op.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"源文件 SHA-256 不匹配：实际 {sourceSha}，预期 {op.SourceSha256}");
        var content = BuildDerivedContent(source, op, out var targetSha);
        if (op.TargetSha256 is not null && !targetSha.Equals(op.TargetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"目标内容 SHA-256 不匹配：生成 {targetSha}，预期 {op.TargetSha256}");
        if (gd.FileExists(op.Dst!))
        {
            var existingSha = FxPatchEngine.Sha(gd.ReadFile(op.Dst!)!);
            if (existingSha.Equals(targetSha, StringComparison.OrdinalIgnoreCase))
            {
                FxPatchEngine.Log($"[skip] {op.Dst} 已存在且内容指纹正确");
                return false;
            }
            // 本补丁上一版留下的副本要放行（否则升版本时补丁会被自己挡住）；
            // 真正被其他 Mod 占用的仍然拒绝。
            if (!FxPatchIdentity.IsPatchOwned(gd.Index, patch, op.Dst!))
                throw new InvalidOperationException($"目标文件 {op.Dst} 已存在但内容指纹不匹配（实际 {existingSha}，预期 {targetSha}），拒绝覆盖其他 Mod。");
            gd.Index.TryGetFile(op.Dst!, out var ownCopy);
            ownCopy!.Write(content);
            FxPatchEngine.Log($"[op] 覆盖本补丁旧版副本 {op.Dst}（{content.Length:N0} B）");
            return true;
        }
        gd.AddFile(op.Dst!, content, saveIndex: false);
        FxPatchEngine.Log($"[op] 新增副本 {op.Dst}（{content.Length:N0} B）");
        return true;
    }

    private static bool ExecutePtrById(GameDataAccess gd, PatchOp op, bool apply)
    {
        var dat = gd.ReadFile(op.Table!)!;
        var layout = FxDatc64Pointers.ParseLayout(dat);
        var field = FxDatc64Pointers.LocatePtrField(dat, layout, op.Id!, op.OriginalPath!, op.NewPath!)
            ?? throw new InvalidOperationException($"表中找不到 Id={op.Id}（客户端不兼容）");
        var target = apply ? op.NewPath! : op.OriginalPath!;
        var (newDat, changed) = FxDatc64Pointers.RepointString(dat, layout, field, target);
        if (!changed)
        {
            FxPatchEngine.Log($"[skip] 指针已指向 \"{target}\"");
            return false;
        }
        gd.Index.TryGetFile(op.Table!, out var fr);
        fr!.Write(newDat);
        FxPatchEngine.Log($"[op] {op.Table} ROW[{field.Row}] +{field.FieldOffset} → \"{target}\"");
        return true;
    }

    private static bool ExecuteEditText(GameDataAccess gd, PatchOp op, bool apply)
    {
        var from = apply ? op.Old! : op.New!;
        var to = apply ? op.New! : op.Old!;
        if (from.Length == 0)
        {
            FxPatchEngine.Log($"[skip] {op.Path} 删除型替换无法反向表达，跳过还原");
            return false;
        }
        var bytes = gd.ReadFile(op.Path!)!;
        var text = TextEncodingDetector.Decode(bytes, out var enc, out var preamble);
        var count = CountOccurrences(text, from);
        if (count == 0)
        {
            FxPatchEngine.Log($"[skip] {op.Path} 已是目标状态");
            return false;
        }
        if (count != 1)
            throw new InvalidOperationException($"{op.Path} 中目标文本出现 {count} 次（预期 1 次），拒绝硬改。");
        text = text.Replace(from, to, StringComparison.Ordinal);
        gd.Index.TryGetFile(op.Path!, out var fr);
        fr!.Write(TextEncodingDetector.Encode(text, enc, preamble));
        FxPatchEngine.Log($"[op] {op.Path} 文本已替换");
        return true;
    }

    // ═══ addfile-derived：读原件 → 声明的替换 → 回编码（动态生成，不内嵌成品）═══
    internal static byte[] BuildDerivedContent(byte[] bytes, PatchOp op, out string targetSha)
    {
        var text = TextEncodingDetector.Decode(bytes, out var enc, out var preamble);
        foreach (var r in op.Replace)
        {
            var occurrences = CountOccurrences(text, r.Old);
            var want = r.Count < 0 ? occurrences : r.Count;
            if (occurrences < want)
                throw new InvalidOperationException(
                    $"源文件 {op.Src} 中 \"{Truncate(r.Old, 40)}\" 只出现 {occurrences} 处（预期 ≥{want}）——客户端不兼容。");
            text = r.Count < 0
                ? text.Replace(r.Old, r.New, StringComparison.Ordinal)
                : ReplaceFirstN(text, r.Old, r.New, r.Count);
        }
        var result = TextEncodingDetector.Encode(text, enc, preamble);
        targetSha = FxPatchEngine.Sha(result);
        return result;
    }

    // ═══ 文本工具 ═══════════════════════════════════════════════
    private static string ReplaceFirstN(string text, string oldText, string newText, int limit)
    {
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        var done = 0;
        while (done < limit)
        {
            var at = text.IndexOf(oldText, pos, StringComparison.Ordinal);
            if (at < 0)
                break;
            sb.Append(text, pos, at - pos).Append(newText);
            pos = at + oldText.Length;
            done++;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    private static int CountOccurrences(string text, string needle)
        => needle.Length == 0 ? 0 : text.Split(needle).Length - 1;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
