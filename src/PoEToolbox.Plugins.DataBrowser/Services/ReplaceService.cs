using System.IO;
using System.Text;
using LibBundle3.Records;
using PoEToolbox.Plugins.DataBrowser.Models;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser.Services;

public sealed record FileReplacementResult(
    string VirtualPath,
    int ReplacementSize,
    string BaselinePath,
    string BundlePath);

/// <summary>Replaces an existing indexed file and persists its bundle/index changes.</summary>
public static class ReplaceService
{
    public static FileReplacementResult Replace(
        string gameDataPath,
        string virtualPath,
        string replacementPath,
        CancellationToken cancellationToken = default)
    {
        ValidateReplacement(virtualPath, replacementPath);
        cancellationToken.ThrowIfCancellationRequested();

        var replacementSize = checked((int)new FileInfo(replacementPath).Length);
        using var gameData = GameDataAccess.Open(gameDataPath);
        if (!gameData.TryGetFile(virtualPath, out var target) || target is null)
            throw new FileNotFoundException("索引中未找到目标文件。", virtualPath);

        // Do not check cancellation after Write: a partially written in-memory index
        // must always be saved together with its redirected bundle record.
        var backup = IndexBackupService.Begin(gameData);
        target.Write(destination => CopyReplacement(replacementPath, destination), replacementSize);
        gameData.Save();
        IndexBackupService.Complete(gameData, backup, "replace-file", new Dictionary<string, string>
        {
            ["virtualPath"] = virtualPath,
            ["replacementPath"] = Path.GetFullPath(replacementPath),
            ["size"] = replacementSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        if (target.Size != replacementSize)
            throw new InvalidDataException("替换后文件长度校验失败: " + virtualPath);

        return new FileReplacementResult(
            virtualPath,
            replacementSize,
            backup.BaselinePath,
            target.BundleRecord.Path);
    }

    /// <summary>Replaces an indexed text file while preserving its detected encoding and BOM.</summary>
    public static FileReplacementResult ReplaceText(
        string gameDataPath,
        string virtualPath,
        string content,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
            throw new ArgumentException("目标文件路径不能为空。", nameof(virtualPath));
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);
        cancellationToken.ThrowIfCancellationRequested();

        var body = encoding.GetBytes(content);
        var preamble = encoding.GetPreamble();
        var replacement = new byte[preamble.Length + body.Length];
        preamble.CopyTo(replacement, 0);
        body.CopyTo(replacement, preamble.Length);

        using var gameData = GameDataAccess.Open(gameDataPath);
        if (!gameData.TryGetFile(virtualPath, out var target) || target is null)
            throw new FileNotFoundException("索引中未找到目标文件。", virtualPath);

        var backup = IndexBackupService.Begin(gameData);
        target.Write(replacement);
        gameData.Save();
        IndexBackupService.Complete(gameData, backup, "edit-file", new Dictionary<string, string>
        {
            ["virtualPath"] = virtualPath,
            ["encoding"] = encoding.WebName,
            ["size"] = replacement.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        return new FileReplacementResult(
            virtualPath,
            replacement.Length,
            backup.BaselinePath,
            target.BundleRecord.Path);
    }

    public static void ReplaceTexts(string gameDataPath, IReadOnlyList<PendingTextEdit> edits, CancellationToken cancellationToken = default)
    {
        if (edits.Count == 0) return;
        using var gameData = GameDataAccess.Open(gameDataPath);
        var backup = IndexBackupService.Begin(gameData);
        foreach (var edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!gameData.TryGetFile(edit.VirtualPath, out var target) || target is null)
                throw new FileNotFoundException("索引中未找到目标文件。", edit.VirtualPath);
            var body = edit.Encoding.GetBytes(edit.EditedText);
            var preamble = edit.Encoding.GetPreamble();
            var bytes = new byte[preamble.Length + body.Length];
            preamble.CopyTo(bytes, 0); body.CopyTo(bytes, preamble.Length);
            target.Write(bytes);
        }
        gameData.Save();
        IndexBackupService.Complete(gameData, backup, "edit-files", new Dictionary<string, string>
        {
            ["files"] = string.Join(";", edits.Select(e => e.VirtualPath)),
        });
    }

    private static void ValidateReplacement(string virtualPath, string replacementPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
            throw new ArgumentException("目标文件路径不能为空。", nameof(virtualPath));
        if (!File.Exists(replacementPath))
            throw new FileNotFoundException("替换文件不存在。", replacementPath);
        if (!string.Equals(Path.GetFileName(virtualPath), Path.GetFileName(replacementPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "替换文件名必须与目标文件同名。",
                nameof(replacementPath));
        }
        if (new FileInfo(replacementPath).Length > int.MaxValue)
            throw new IOException("替换文件超过支持的最大大小（2 GB）。");
    }

    private static void CopyReplacement(string replacementPath, Span<byte> destination)
    {
        using var source = new FileStream(replacementPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        source.ReadExactly(destination);
    }
}
