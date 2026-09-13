using System.IO;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 整包替换型补丁：zip（或目录）里直接带 <c>_.index.bin</c> 与其他 bundle 文件，作者没有给 patch.json。
/// 约定：以 <c>_.index.bin</c> 所在那层为根（作者打包时常带一层 bundles2/），其余文件按相对路径
/// 落进游戏索引目录；应用前先把同名原文件备份到 <c>backup/&lt;补丁名&gt;/</c>，
/// 而 <c>backup/_.index.bin</c> 是原版基线（特殊用途），索引内容一致时直接复用、不重复占磁盘。
/// </summary>
public sealed class FxRawPackPatchTests : IDisposable
{
    private const string OriginalIndex = "ORIG-INDEX";
    private const string OriginalBundle = "ORIG-BUNDLE";
    private const string NewIndex = "NEW-INDEX-CONTENT";
    private const string NewBundle = "NEW-BUNDLE-CONTENT";

    private readonly string _root;
    private readonly string _gameDir;
    private readonly string _indexPath;
    private readonly string _packDir;
    private readonly string _backupDir;

    public FxRawPackPatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-rawpack-" + Guid.NewGuid().ToString("N"));
        _gameDir = Path.Combine(_root, "game", "Bundles2");
        _indexPath = Path.Combine(_gameDir, "_.index.bin");
        // 补丁包保留作者的目录结构：bundles2/_.index.bin + bundles2/Tiny.V0.1.bundle.bin
        _packDir = Path.Combine(_root, "pack", "bundles2");
        _backupDir = Path.Combine(_gameDir, "backup", "Tiny");

        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(_packDir);
        File.WriteAllText(_indexPath, OriginalIndex);
        File.WriteAllText(GamePath("Tiny.V0.1.bundle.bin"), OriginalBundle);
        File.WriteAllText(PackPath("_.index.bin"), NewIndex);
        File.WriteAllText(PackPath("Tiny.V0.1.bundle.bin"), NewBundle);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp directory.
        }
    }

    private string GamePath(string name) => Path.Combine(_gameDir, name);

    private string PackPath(string name) => Path.Combine(_packDir, name);

    private string BackupPath(string name) => Path.Combine(_backupDir, name);

    /// <summary>游戏在运行时会拒绝写入；此时跳过用例而不是误报失败。</summary>
    private static bool GameIsRunning() => PoeDetector.Default.IsPoeRunning();

    [Fact]
    public void TryDetectRawPack_RootsPathsAtTheIndexFileFolder()
    {
        var pack = FxPatchEngine.TryDetectRawPack(Path.Combine(_root, "pack"), "Tiny");

        Assert.NotNull(pack);
        Assert.Equal("Tiny", pack!.PatchId);
        Assert.Equal(new[] { "_.index.bin", "Tiny.V0.1.bundle.bin" }, pack.Files.Select(f => f.RelativePath));
        Assert.Equal(NewIndex, File.ReadAllText(pack.Files[0].SourcePath));
    }

    /// <summary>
    /// 回归：作者只打包索引 + bundle、没有 patch.json 的 zip。
    /// 此前 ExtractZipPatch 在找不到 patch.json 时直接报错返回，整包替换识别永远走不到。
    /// </summary>
    [Fact]
    public void ZipWithoutPatchJson_IsStillExtractedAndDetectedAsRawPack()
    {
        var zipPath = Path.Combine(_root, "[Poe2]_功能补丁_V7.3_正式版.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(_root, "pack"), zipPath);

        var patchJson = FxPatchEngine.ExtractZipPatch(zipPath, out var dir);

        Assert.Null(patchJson);
        Assert.False(string.IsNullOrEmpty(dir));
        var pack = FxPatchEngine.TryDetectRawPack(dir!, Path.GetFileNameWithoutExtension(zipPath));
        Assert.NotNull(pack);
        Assert.Equal(new[] { "_.index.bin", "Tiny.V0.1.bundle.bin" }, pack!.Files.Select(f => f.RelativePath));
        Assert.Equal(NewIndex, File.ReadAllText(pack.Files[0].SourcePath));
        Assert.Equal(NewBundle, File.ReadAllText(pack.Files[1].SourcePath));
    }

    [Fact]
    public void TryDetectRawPack_IgnoresPlainPatchFolders()
    {
        var onlyIndex = Path.Combine(_root, "only-index");
        Directory.CreateDirectory(onlyIndex);
        File.WriteAllText(Path.Combine(onlyIndex, "_.index.bin"), NewIndex);

        Assert.Null(FxPatchEngine.TryDetectRawPack(onlyIndex, "Tiny"));

        var assetsOnly = Path.Combine(_root, "assets-only");
        Directory.CreateDirectory(Path.Combine(assetsOnly, "assets"));
        File.WriteAllText(Path.Combine(assetsOnly, "x.patch.json"), "{}");
        File.WriteAllText(Path.Combine(assetsOnly, "assets", "0001_grd.ao"), "ao");

        Assert.Null(FxPatchEngine.TryDetectRawPack(assetsOnly, "Tiny"));
    }

    [Fact]
    public void Apply_BacksUpOriginalsThenOverwrites()
    {
        if (GameIsRunning())
            return;

        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, Detect(), "apply"));

        Assert.Equal(NewIndex, File.ReadAllText(_indexPath));
        Assert.Equal(NewBundle, File.ReadAllText(GamePath("Tiny.V0.1.bundle.bin")));
        Assert.Equal(OriginalIndex, File.ReadAllText(BackupPath("_.index.bin")));
        Assert.Equal(OriginalBundle, File.ReadAllText(BackupPath("Tiny.V0.1.bundle.bin")));
    }

    [Fact]
    public void Revert_RestoresTheBackedUpOriginals()
    {
        if (GameIsRunning())
            return;

        var pack = Detect();
        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, pack, "apply"));
        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, pack, "revert"));

        Assert.Equal(OriginalIndex, File.ReadAllText(_indexPath));
        Assert.Equal(OriginalBundle, File.ReadAllText(GamePath("Tiny.V0.1.bundle.bin")));
    }

    [Fact]
    public void Revert_DeletesFilesThePackAdded()
    {
        if (GameIsRunning())
            return;

        File.WriteAllText(PackPath("Extra.V0.1.bundle.bin"), "EXTRA");
        var pack = Detect();

        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, pack, "apply"));
        Assert.True(File.Exists(GamePath("Extra.V0.1.bundle.bin")));

        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, pack, "revert"));
        Assert.False(File.Exists(GamePath("Extra.V0.1.bundle.bin")));
    }

    [Fact]
    public void Apply_ReusesTheOriginalIndexBaselineInsteadOfCopyingItAgain()
    {
        if (GameIsRunning())
            return;

        // backup/_.index.bin 是原版基线（特殊用途），内容一致时不该再复制一份到 backup/<补丁名>/
        var baseline = Path.Combine(_gameDir, "backup", "_.index.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, OriginalIndex);

        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, Detect(), "apply"));

        Assert.False(File.Exists(BackupPath("_.index.bin")));
        Assert.Equal(OriginalBundle, File.ReadAllText(BackupPath("Tiny.V0.1.bundle.bin")));
        Assert.Equal(OriginalIndex, File.ReadAllText(baseline));
    }

    [Fact]
    public void Status_ReportsAppliedStateAfterApply()
    {
        if (GameIsRunning())
            return;

        var pack = Detect();
        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, pack, "status"));
        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, pack, "apply"));
        Assert.Equal(0, FxPatchEngine.RunRawPack(_indexPath, pack, "status"));
    }

    private FxPatchEngine.RawPack Detect()
        => FxPatchEngine.TryDetectRawPack(Path.Combine(_root, "pack"), "Tiny")
           ?? throw new InvalidOperationException("测试用的整包补丁未被识别。");
}
