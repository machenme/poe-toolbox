using System.IO;
using System.IO.Compression;

namespace PoEToolbox.Shared;

/// <summary>
/// 把一个补丁打包为可分发的 zip（描述文件 + assets 目录），供「特效补丁」页的导出功能使用。
/// 支持三类来源：.patch.json（与 assets 一起打包）、.zip（原样复制）、内置补丁描述文件（同 json）。
/// zip 布局与 fx-patch diff --zip 一致：根目录 = 描述文件 + assets/，可直接被「启用特效补丁」消费。
/// </summary>
public static class PatchExportService
{
    public static void ExportTo(string source, string zipPath)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            throw new FileNotFoundException("补丁文件不存在，无法导出。", source);
        if (string.IsNullOrWhiteSpace(zipPath))
            throw new ArgumentException("导出路径为空。", nameof(zipPath));

        // 已是 zip 的补丁包：原样复制即可
        if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(zipPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("导出路径与补丁文件相同。");
            File.Copy(source, zipPath, overwrite: true);
            return;
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var stage = Path.Combine(Path.GetTempPath(), "poetoolbox-patch-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            // 只打包描述文件 + assets：源目录里可能有备份、草稿等无关文件，不带进分发物
            File.Copy(source, Path.Combine(stage, Path.GetFileName(source)), overwrite: true);
            var assets = Path.Combine(root, "assets");
            if (Directory.Exists(assets))
                CopyDirectory(assets, Path.Combine(stage, "assets"));

            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(stage, zipPath);
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch (IOException) { /* 临时目录尽力清理 */ }
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }
}
