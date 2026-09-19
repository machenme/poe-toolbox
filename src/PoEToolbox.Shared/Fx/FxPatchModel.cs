namespace PoEToolbox.Shared;

/// <summary>
/// 补丁描述的数据模型：<c>.patch.json</c> 的形状（<see cref="PatchDef"/> /
/// <see cref="PatchOp"/>）以及状态判定用到的 <see cref="PatchState"/>。
/// </summary>
/// <remarks>
/// 这些类型原先是 <see cref="FxPatchEngine"/> 的嵌套类型。拆引擎时每个新文件都要写
/// <c>PatchDef</c>，既啰嗦又让方法签名读不清，所以先提到命名空间级。
/// 本次只搬类型定义，没有改任何字段名与语义。
/// </remarks>

internal sealed class PatchDef
{
    public string PatchId { get; set; } = "";
    public string BundleName { get; set; } = "";

    /// <summary>补丁内容版本，可选。写了就拼进 bundle 名（<c>_v&lt;version&gt;</c>），
    /// 不写则本补丁固定落在一个 bundle 里。
    /// 状态判定全部基于文件内容，所以同一补丁反复覆盖自己的 bundle 是安全的，
    /// 不再需要靠升版本来绕开"疑似覆盖其他 Mod"的判断。</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    public List<PatchOp> Operations { get; set; } = new();

    /// <summary>补丁描述文件所在目录（assets 相对路径的基准）。运行时注入，不序列化。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BasePath { get; set; }
}

internal sealed class PatchOp
{
    public string Op { get; set; } = "";       // addfile-derived | addfile-asset | patchptr-byid | edittext
    public string? Src { get; set; }           // addfile-derived
    public string? SourceSha256 { get; set; }  // addfile-derived：源文件 SHA-256（可选）
    public string? TargetSha256 { get; set; }  // addfile-derived：生成目标 SHA-256（可选）
    public string? Dst { get; set; }
    public List<TextReplace> Replace { get; set; } = new();
    public string? Asset { get; set; }         // addfile-asset：补丁包内成品内容（相对 BasePath）
    public string? OriginalAsset { get; set; } // addfile-asset：原版内容（可选，供 revert 还原）
    public string? Table { get; set; }         // patchptr-byid
    public string? Id { get; set; }
    public string? OriginalPath { get; set; }
    public string? NewPath { get; set; }
    public string? Path { get; set; }          // edittext
    public string? Old { get; set; }
    public string? New { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Describe => Op switch
    {
        "addfile-derived" => $"副本+替换  {Src} → {Dst}",
        "addfile-asset" => $"成品内容  {Dst}" + (OriginalAsset is null ? "（新增）" : "（替换）"),
        "patchptr-byid" => $"指针重定向  {Table} 中 Id={Id}",
        "edittext" => $"文本替换  {Path}",
        _ => Op,
    };
}

internal sealed class TextReplace
{
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
    public int Count { get; set; } = -1;       // -1 = 全部替换
}

internal enum PatchState { NotApplied, Applied, Conflict, Incompatible }

/// <summary>内置补丁注册表里的一条登记：命令行 / UI 用的 id、展示名、内嵌资源文件名。</summary>
internal sealed record BuiltInPatchDef(string Id, string DisplayName, string FileName);

    /// <summary>内置补丁的整体状态快照，供 UI 展示与默认勾选。</summary>
internal sealed record BuiltInPatchStatus(string Id, string DisplayName, PatchState State, string Detail);


/// <summary>一条操作在某次判定中的状态与说明。纯数据，供日志与界面展示。</summary>
internal sealed record OpState(PatchOp Op, PatchState State, string Detail);
