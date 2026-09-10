using System.IO;
using LibBundle3.Records;
using PoEToolbox.Core.Assets;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser.Services;

public sealed record MapNumberWriteResult(
    int UpdatedFiles,
    string BaselinePath,
    string BundlePath);

public static class MapNumberReplacementService
{
    private const string Poe1PathPrefix = "art/2ditems/maps/atlas2maps/new/mapnumbers";
    private const string Poe2PathPrefix = "art/2ditems/maps/endgamemaps/endgamemap";

    public static MapNumberWriteResult Apply(
        GameDataAccess gameData,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        float fontSize = 28f,
        float offsetX = 0f,
        float offsetY = 0f,
        string fontFamily = "Arial",
        IReadOnlyList<DdsRgbColor>? colors = null,
        byte[]? poe2BackgroundImage = null)
    {
        var isPoe2 = gameData.IsPoe2Client;
        var pathPrefix = isPoe2 ? Poe2PathPrefix : Poe1PathPrefix;
        var mapCount = isPoe2 ? 15 : 16;
        if (isPoe2 && poe2BackgroundImage is null)
            throw new ArgumentException("PoE2 地图数字需要底图素材。", nameof(poe2BackgroundImage));
        if (colors is not null && colors.Count < mapCount)
            throw new ArgumentException($"必须为 1 到 {mapCount} 提供完整颜色列表。", nameof(colors));

        var files = new List<(int Number, FileRecord Record, FileRecord HeaderRecord)>();
        for (var number = 1; number <= mapCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pathPrefix + number + ".dds";
            if (!gameData.TryGetFile(path, out var record) || record is null)
                throw new FileNotFoundException("未找到目标文件: " + path);
            var headerPath = path + ".header";
            if (!gameData.TryGetFile(headerPath, out var headerRecord) || headerRecord is null)
                throw new FileNotFoundException("未找到目标文件: " + headerPath);
            files.Add((number, record, headerRecord));
        }

        var changed = new List<(FileRecord Record, byte[] Content)>();
        foreach (var (number, record, headerRecord) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = record.Read().ToArray();
            var replacement = DdsMapNumberRenderer.Render(
                original,
                number,
                fontSize,
                offsetX,
                offsetY,
                fontFamily,
                colors?[number - 1],
                isPoe2 ? poe2BackgroundImage : null);
            changed.Add((record, replacement));

            var header = headerRecord.Read().ToArray();
            var headerReplacement = DdsMapNumberRenderer.RenderSidecar(header, replacement);
            changed.Add((headerRecord, headerReplacement));
            progress?.Report($"已生成地图数字 {number} 及其低 mip");
        }

        var backup = IndexBackupService.Begin(gameData);
        foreach (var (record, content) in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record.Write(content);
        }

        gameData.Save();
        IndexBackupService.Complete(gameData, backup, "map-numbers", new Dictionary<string, string>
        {
            ["game"] = isPoe2 ? "poe2" : "poe1",
            ["fontFamily"] = fontFamily,
            ["fontSize"] = fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["offsetX"] = offsetX.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["offsetY"] = offsetY.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        foreach (var (record, expected) in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = record.Read().ToArray();
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("写入校验失败: " + record.Path);
        }

        return new MapNumberWriteResult(
            files.Count,
            backup.BaselinePath,
            changed[0].Record.BundleRecord.Path);
    }

}
