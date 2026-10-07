using System.IO;
using System.Text;
namespace PoEToolbox.Shared;

/// <summary>
/// 不变式 4（README「必须保持的不变式」表）：补丁状态是三态归约，不是布尔；判据来自索引里的实际内容。改判定要同时看那 9 条性质测试。
/// 补丁状态的三态判定：把每条 op 读成 <see cref="OpState"/>，再归约成整体状态与展示文案。
/// 全部只读——判据来自索引里的实际文件内容，不看账本、不看版本，所以「已应用」永不靠猜。
/// </summary>
/// <remarks>
/// S7 从 <see cref="FxPatchEngine"/> 原样搬出，方法体未改。日志与哈希仍走引擎出口
/// （<c>FxPatchEngine.Log</c> / <c>Sha</c>），CLI 与 UI 因此共用同一套口径。
/// </remarks>
internal static class FxPatchState
{
    /// <summary>判定「是否启用」时只看对画面有实际影响的操作：副本与纯新增文件在还原后按设计保留，不算数。
    /// 与 CmdApplyOrRevert 的校验口径一致。</summary>
    private static OpState[] EffectiveStates(OpState[] states)
        => states.Where(s => s.Op.Op != "addfile-derived"
                && !(s.Op.Op == "addfile-asset" && s.Op.OriginalAsset is null))
            .ToArray();

    /// <summary>把一组操作状态归约为整体状态：任一操作冲突/不兼容，或新旧状态参半（部分应用），都算冲突。</summary>
    internal static PatchState OverallOf(OpState[] states)
    {
        var effective = EffectiveStates(states);
        if (effective.Length == 0)
            return PatchState.Incompatible;
        if (effective.Any(s => s.State is PatchState.Conflict or PatchState.Incompatible))
            return PatchState.Conflict;
        if (effective.Any(s => s.State == PatchState.Applied) && effective.Any(s => s.State != PatchState.Applied))
            return PatchState.Conflict;
        return effective[0].State;
    }

    /// <inheritdoc cref="OverallOf"/>
    internal static string MarkOf(OpState[] states) => OverallOf(states) switch
    {
        PatchState.Applied => "☑",
        PatchState.NotApplied => "☐",
        _ => "⚠",
    };

    /// <summary>状态说明：全部就位时留空，否则说清第一个挡住的原因。</summary>
    internal static string DetailOf(OpState[] states)
    {
        var bad = EffectiveStates(states).FirstOrDefault(s => s.State is PatchState.Conflict or PatchState.Incompatible);
        if (bad is not null)
            return bad.Detail;
        var pending = EffectiveStates(states).FirstOrDefault(s => s.State != PatchState.Applied);
        return pending is null ? "" : pending.Detail;
    }

    // ═══ 单 op 三态计算（只读）══════════════════════════════════
    internal static OpState[] ComputeStates(GameDataAccess gd, PatchDef patch, bool? applying = null)
        => patch.Operations.Select(op => ComputeState(gd, patch, op, applying)).ToArray();

    private static OpState ComputeState(GameDataAccess gd, PatchDef patch, PatchOp op, bool? applying = null)
    {
        try
        {
            switch (op.Op)
            {
                case "addfile-derived":
                {
                    if (!gd.FileExists(op.Src!))
                        return new OpState(op, PatchState.Incompatible, $"找不到源文件 {op.Src}");
                    if (applying == false)
                        return gd.FileExists(op.Dst!)
                            ? new OpState(op, PatchState.Applied, $"副本存在 {op.Dst}（还原时保留无引用副本）")
                            : new OpState(op, PatchState.NotApplied, "副本不存在");
                    var source = gd.ReadFile(op.Src!)!;
                    var sourceSha = FxPatchEngine.Sha(source);
                    if (op.SourceSha256 is not null
                        && !sourceSha.Equals(op.SourceSha256, StringComparison.OrdinalIgnoreCase))
                        return new OpState(op, PatchState.Incompatible,
                            $"源文件 SHA-256 不匹配：实际 {sourceSha}，预期 {op.SourceSha256}");

                    var target = FxPatchOperations.BuildDerivedContent(source, op, out var targetSha);
                    if (op.TargetSha256 is not null
                        && !targetSha.Equals(op.TargetSha256, StringComparison.OrdinalIgnoreCase))
                        return new OpState(op, PatchState.Incompatible,
                            $"目标内容 SHA-256 不匹配：生成 {targetSha}，预期 {op.TargetSha256}");
                    if (!gd.FileExists(op.Dst!))
                        return new OpState(op, PatchState.NotApplied, "副本不存在");

                    var existingSha = FxPatchEngine.Sha(gd.ReadFile(op.Dst!)!);
                    return existingSha.Equals(targetSha, StringComparison.OrdinalIgnoreCase)
                        ? new OpState(op, PatchState.Applied, $"副本内容 SHA-256 匹配 {targetSha}")
                        : new OpState(op, PatchState.Conflict,
                            $"目标文件已存在但内容指纹不匹配：实际 {existingSha}，预期 {targetSha}（可能被其他 Mod 占用）");
                }
                case "addfile-asset":
                {
                    var assetPath = FxPatchPackage.ResolvePatchFilePath(patch, op.Asset!);
                    if (!File.Exists(assetPath))
                        return new OpState(op, PatchState.Incompatible, $"补丁包内找不到 asset {op.Asset}");
                    if (!gd.FileExists(op.Dst!))
                        return new OpState(op, PatchState.NotApplied, "目标文件不存在");
                    var sha = FxPatchEngine.Sha(gd.ReadFile(op.Dst!)!);
                    if (sha == FxPatchEngine.Sha(File.ReadAllBytes(assetPath)))
                        return new OpState(op, PatchState.Applied, "内容与补丁 asset 一致");
                    if (op.OriginalAsset is not null)
                    {
                        var origPath = FxPatchPackage.ResolvePatchFilePath(patch, op.OriginalAsset);
                        if (File.Exists(origPath) && sha == FxPatchEngine.Sha(File.ReadAllBytes(origPath)))
                            return new OpState(op, PatchState.NotApplied, "内容为原版");
                    }
                    return new OpState(op, PatchState.Conflict, "内容与补丁/原版均不一致（可能与其他 mod 冲突）");
                }
                case "patchptr-byid":
                {
                    if (!gd.FileExists(op.Table!))
                        return new OpState(op, PatchState.Incompatible, $"找不到表 {op.Table}");
                    var dat = gd.ReadFile(op.Table!)!;
                    var layout = FxDatc64Pointers.ParseLayout(dat);
                    var field = FxDatc64Pointers.LocatePtrField(dat, layout, op.Id!, op.OriginalPath!, op.NewPath!);
                    return field is null
                        ? new OpState(op, PatchState.Incompatible, $"表中找不到 Id={op.Id} 的行或字段")
                        : new OpState(op, field.State, $"ROW[{field.Row}] +{field.FieldOffset} = \"{field.CurrentValue}\"");
                }
                case "edittext":
                {
                    if (!gd.FileExists(op.Path!))
                        return new OpState(op, PatchState.Incompatible, $"找不到文件 {op.Path}");
                    var text = TextEncodingDetector.Decode(gd.ReadFile(op.Path!)!, out _, out _);
                    var hasOld = text.Contains(op.Old!, StringComparison.Ordinal);
                    // 空 New（删除型替换）对 Contains 恒为 true，必须排除
                    var hasNew = op.New!.Length > 0 && text.Contains(op.New!, StringComparison.Ordinal);
                    return (hasOld, hasNew) switch
                    {
                        (true, false) => new OpState(op, PatchState.NotApplied, "原文存在"),
                        (false, true) => new OpState(op, PatchState.Applied, "目标文本存在"),
                        // 删除型替换：New 为空时"原文已消失"即视为已应用
                        (false, false) when op.New.Length == 0 => new OpState(op, PatchState.Applied, "原文已删除"),
                        _ => new OpState(op, PatchState.Conflict, "新旧文本同时缺失/存在（可能与其他 mod 冲突）"),
                    };
                }
                default:
                    return new OpState(op, PatchState.Incompatible, $"未知 op 类型 {op.Op}");
            }
        }
        catch (Exception ex)
        {
            return new OpState(op, PatchState.Incompatible, ex.Message);
        }
    }

    internal static int CountUnresolved(GameDataAccess gd)
        => gd.Index.Files.Values.Count(f => string.IsNullOrEmpty(f.Path));

    internal static string Label(PatchState s) => s switch
    {
        PatchState.NotApplied => "未应用",
        PatchState.Applied => "已应用",
        PatchState.Conflict => "冲突　",
        _ => "不兼容",
    };
}
