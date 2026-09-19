namespace PoEToolbox.Shared;

/// <summary>
/// 补丁的身份：它把内容写到哪个 bundle、哪些文件是它自己造出来的。
/// 从 <see cref="FxPatchEngine"/> 分出来的一块——纯查询，不碰文件系统也不碰索引写入。
/// </summary>
internal static class FxPatchIdentity
{
    /// <summary>写入目标：写了 version 时是 <c>PATCHED/&lt;bundleName&gt;_v&lt;version&gt;</c>，
    /// 没写就是 <c>PATCHED/&lt;bundleName&gt;</c>——同一补丁恒定落在同一个 bundle，
    /// 反复 apply/revert 复用它，而不是每轮都在磁盘上留下一个新文件。</summary>
    internal static string BundlePathOf(PatchDef patch)
        => string.IsNullOrWhiteSpace(patch.Version)
            ? $"{FxPatchEngine.PatchBundleDirectory}{patch.BundleName}"
            : $"{FxPatchEngine.PatchBundleDirectory}{patch.BundleName}_v{patch.Version}";

    /// <summary>匹配该补丁写过的全部 bundle（任意版本，也兼容旧的时间戳命名）。
    /// 拿它判断某个文件是不是本补丁自己的产物。</summary>
    internal static string BundlePrefixOf(PatchDef patch)
        => $"{FxPatchEngine.PatchBundleDirectory}{patch.BundleName}_";

    /// <summary>该 bundle 是否属于本补丁：无版本补丁的 bundle 名不带下划线后缀，
    /// 光靠前缀 <see cref="BundlePrefixOf"/> 认不出来，需要再比一次完整路径。</summary>
    internal static bool IsBundleOwnedBy(PatchDef patch, string bundlePath)
    {
        // 索引里的 bundle 记录统一带 .bundle.bin 后缀，先剥掉再比
        var bare = bundlePath.EndsWith(".bundle.bin", StringComparison.OrdinalIgnoreCase)
            ? bundlePath[..^".bundle.bin".Length]
            : bundlePath;
        return bare.Equals(BundlePathOf(patch), StringComparison.OrdinalIgnoreCase)
               || bare.StartsWith(BundlePrefixOf(patch), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>该文件当前是否落在本补丁写的 bundle 里——是则可安全覆盖，
    /// 不是则说明被别的 Mod 占用。</summary>
    internal static bool IsPatchOwned(LibBundle3.Index index, PatchDef patch, string path)
        => index.TryGetFile(path, out var fr)
           && fr?.BundleRecord?.Path is { } bundlePath
           && IsBundleOwnedBy(patch, bundlePath);

    /// <summary>补丁自己造出来的路径，也就是 purge 唯一允许删除的东西。
    /// 其余落在这个前缀下的文件都来自游戏本体（被重定向进来的还原版表/被改过的资源），
    /// 删掉它们的索引记录等于把文件从游戏里挖走。</summary>
    internal static HashSet<string> OwnedPathsOf(PatchDef patch)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in patch.Operations)
        {
            var isPatchCreated = op.Op switch
            {
                // 副本路径由补丁凭空造出
                "addfile-derived" => true,
                // 没有原版字节 == 原版本来就没有这个文件
                "addfile-asset" => op.OriginalAsset is null,
                // patchptr-byid / edittext 修改的都是游戏原有文件
                _ => false,
            };
            if (isPatchCreated && !string.IsNullOrEmpty(op.Dst))
                owned.Add(op.Dst!);
        }
        return owned;
    }
}
