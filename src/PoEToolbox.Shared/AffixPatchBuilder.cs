using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEToolbox.Shared;

/// <summary>
/// 把词缀上色的计算结果（原版字节 + 修改后字节）落盘为 FxPatchEngine 可直接消费的补丁：
/// <c>&lt;PatchesDirectory&gt;/affix-workbench/{affix-workbench.patch.json, assets/NNNN_*[.orig]}</c>，
/// 布局与 op 格式同实例补丁（addfile-asset + 原版备份）。
/// 每次重新生成 Version+1——引擎按版本号复用 PATCHED bundle，改内容必须升版本。
/// </summary>
public static class AffixPatchBuilder
{
    /// <summary>工作台补丁固定单例 id（方案不改变补丁身份，靠 Version 递增演进）。</summary>
    public const string PatchId = "affix-workbench";

    public sealed record FileChange(string GamePath, byte[] Original, byte[] Modified);

    private sealed record PatchJson(
        [property: JsonPropertyOrder(0)] string PatchId,
        [property: JsonPropertyOrder(1)] string BundleName,
        [property: JsonPropertyOrder(2)] string Version,
        [property: JsonPropertyOrder(3)] List<OpJson> Operations,
        [property: JsonPropertyName("_comment"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Comment = null);

    private sealed record OpJson(
        [property: JsonPropertyOrder(0)] string Op,
        [property: JsonPropertyOrder(1)] string Dst,
        [property: JsonPropertyOrder(2)] List<object> Replace,
        [property: JsonPropertyOrder(3)] string Asset,
        [property: JsonPropertyOrder(4)] string? OriginalAsset);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>生成/更新补丁目录，返回 patch.json 完整路径。</summary>
    public static string Build(IReadOnlyList<FileChange> changes, string? patchesDir = null)
    {
        if (changes.Count == 0)
            throw new ArgumentException("没有可写入的文件变更。", nameof(changes));

        var dir = Path.Combine(patchesDir ?? ConfigService.PatchesDirectory, PatchId);
        var assetsDir = Path.Combine(dir, "assets");
        Directory.CreateDirectory(assetsDir);

        var version = NextVersion(Path.Combine(dir, PatchId + ".patch.json"));

        // 重建 assets 目录：旧资产与当前方案无关（内容已重算），保留只会误导排查
        foreach (var old in Directory.GetFiles(assetsDir))
            File.Delete(old);

        var ops = new List<OpJson>();
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var assetName = $"{i:D4}_{AssetStem(change.GamePath, changes, i)}";
            var assetPath = Path.Combine(assetsDir, assetName);
            File.WriteAllBytes(assetPath, change.Modified);
            File.WriteAllBytes(assetPath + ".orig", change.Original);
            ops.Add(new OpJson("addfile-asset", change.GamePath, [], $"assets/{assetName}", $"assets/{assetName}.orig"));
        }

        var patch = new PatchJson(PatchId, PatchId, version.ToString(), ops);
        var jsonPath = Path.Combine(dir, PatchId + ".patch.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(patch, JsonOpts));
        return jsonPath;
    }

    private static int NextVersion(string existingJsonPath)
    {
        if (!File.Exists(existingJsonPath))
            return 1;
        try
        {
            var existing = JsonSerializer.Deserialize<PatchJson>(File.ReadAllText(existingJsonPath), JsonOpts);
            return int.TryParse(existing?.Version, out var v) ? v + 1 : 1;
        }
        catch
        {
            return 1; // 旧描述损坏时从 v1 重建；引擎 apply 前的完整性校验仍会拦截不一致
        }
    }

    /// <summary>把方案变更生成为**可分发的独立补丁**（别人不需要装本工具也能用）：
    /// <c>&lt;outputDir&gt;/&lt;patchId&gt;/&lt;patchId&gt;.patch.json + assets/</c>，颜色定义（uisettings.xml 变更）
    /// 无条件包含——分发对象客户端上很可能还没有这些颜色。返回补丁描述文件完整路径（zip 时以该目录为根）。</summary>
    public static string BuildExport(IReadOnlyList<FileChange> changes, string outputDir, string patchId, string? comment = null)
    {
        if (changes.Count == 0)
            throw new ArgumentException("没有可写入的文件变更。", nameof(changes));

        var dir = Path.Combine(outputDir, patchId);
        var assetsDir = Path.Combine(dir, "assets");
        Directory.CreateDirectory(assetsDir);

        var ops = new List<OpJson>();
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var assetName = $"{i:D4}_{AssetStem(change.GamePath, changes, i)}";
            var assetPath = Path.Combine(assetsDir, assetName);
            File.WriteAllBytes(assetPath, change.Modified);
            File.WriteAllBytes(assetPath + ".orig", change.Original);
            ops.Add(new OpJson("addfile-asset", change.GamePath, [], $"assets/{assetName}", $"assets/{assetName}.orig"));
        }

        var patch = new PatchJson(patchId, patchId, "1", ops, comment);
        var jsonPath = Path.Combine(dir, patchId + ".patch.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(patch, JsonOpts));
        return jsonPath;
    }

    /// <summary>资产文件名 = 文件名主体；同目录内重名时补上级目录名避免覆盖。</summary>
    private static string AssetStem(string gamePath, IReadOnlyList<FileChange> changes, int index)
    {
        var name = Path.GetFileName(gamePath);
        var duplicated = changes
            .Where((c, i) => i != index && string.Equals(Path.GetFileName(c.GamePath), name, StringComparison.OrdinalIgnoreCase))
            .Any();
        if (!duplicated)
            return name;
        var parent = Path.GetFileName(Path.GetDirectoryName(gamePath.TrimEnd('/')));
        return $"{parent}_{name}";
    }
}
