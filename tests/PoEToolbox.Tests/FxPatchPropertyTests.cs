using System.Text;
using System.Text.Json;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 补丁引擎的性质测试：覆盖的是<strong>不变式</strong>而不是某个补丁的行为，
/// 所以新增 op 类型时自动生效。这三条对应产品对外承诺的三件事——
/// 可回滚、重复执行安全、多个补丁互不干扰。
/// 它们是拆分 <see cref="FxPatchEngine"/> 的准入条件：先有网再拆。
/// </summary>
[Collection(GameDataTestCollection.Name)]
public sealed class FxPatchPropertyTests : IDisposable
{
    private const string NotesPath = "data/notes.txt";
    private const string OtherPath = "data/other.txt";
    private static readonly byte[] NotesOriginal = Encoding.UTF8.GetBytes("alpha beta gamma");
    private static readonly byte[] OtherOriginal = Encoding.UTF8.GetBytes("one two three");

    private readonly string _root;
    private readonly string _patchDir;

    public FxPatchPropertyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-fxprop-" + Guid.NewGuid().ToString("N"));
        _patchDir = Path.Combine(_root, "patches");
        Directory.CreateDirectory(_patchDir);

        SeedIndex.Build(
            Path.Combine(_root, "Bundles2"),
            (NotesPath, NotesOriginal),
            (OtherPath, OtherOriginal));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ═══ P-1：apply → revert 必须字节级回到原样 ═══

    [Fact]
    public void EditText_ApplyThenRevert_RestoresBytesExactly()
    {
        var patch = WritePatch("edittext", new
        {
            Op = "edittext",
            Path = NotesPath,
            Old = "beta",
            New = "BETA",
        });

        Run(_root, patch, "apply");
        Assert.Equal("alpha BETA gamma", ReadText(NotesPath));

        Run(_root, patch, "revert");
        AssertExactly(NotesOriginal, Read(NotesPath));
    }

    [Fact]
    public void AddFileAsset_ApplyThenRevert_RestoresBytesExactly()
    {
        var json = WritePatch("asset", new
        {
            Op = "addfile-asset",
            Dst = OtherPath,
            Asset = "assets/replacement.txt",
            OriginalAsset = "assets/original.txt",
        });
        WriteAsset("asset", "assets/replacement.txt", "NEW CONTENT");
        WriteAsset("asset", "assets/original.txt", OtherOriginal);

        Run(_root, json, "apply");
        Assert.Equal("NEW CONTENT", ReadText(OtherPath));

        Run(_root, json, "revert");
        AssertExactly(OtherOriginal, Read(OtherPath));
    }

    [Fact]
    public void AddFileDerived_ApplyThenRevert_LeavesTheSourceUntouched()
    {
        var json = WritePatch("derived", new
        {
            Op = "addfile-derived",
            Src = NotesPath,
            Dst = "data/derived.txt",
            Replace = new[] { new { Old = "beta", New = "BETA" } },
        });

        Run(_root, json, "apply");
        Assert.Equal("alpha BETA gamma", ReadText("data/derived.txt"));

        Run(_root, json, "revert");
        // 派生副本按设计保留（revert 豁免），但源文件绝不能被改写。
        AssertExactly(NotesOriginal, Read(NotesPath));
    }

    [Fact]
    public void PatchPtr_ApplyThenRevert_RestoresBytesExactly()
    {
        // 目标串预先放进字符串池但不被任何行引用：此时 apply/revert 都是严格的单字段改写，
        // 不触发追加，因此可以要求整份表逐字节还原。
        var seed = BuildDatc64([(TargetId, PathOriginal), ("Other", "graphics/fx_other")], PathPatched);
        var table = SeedTable(seed);
        var json = WritePatch("ptr", PtrOp(table, PathPatched));

        Run(_root, json, "apply");
        Assert.Equal(PathPatched, PathOfRow(table, TargetId));

        Run(_root, json, "revert");
        AssertExactly(seed, Read(table));
    }

    [Fact]
    public void PatchPtr_ApplyTwice_LeavesTheTableIdentical()
    {
        var seed = BuildDatc64([(TargetId, PathOriginal)], PathPatched);
        var table = SeedTable(seed);
        var json = WritePatch("ptr-twice", PtrOp(table, PathPatched));

        Run(_root, json, "apply");
        var once = Read(table)!;

        Run(_root, json, "apply");
        AssertExactly(once, Read(table));
    }

    /// <summary>
    /// 目标串不在字符串池时 apply 必须**追加**而不是就地覆盖池内字节——旧 EOF 还要留作空串
    /// 哨兵（可能有别的行引用它），所以还原后表只会变长。这条锁住「除了增长和被定位的那条指针，
    /// 什么都不变」，并确认 revert 之后前缀逐字节回到原样。
    /// </summary>
    [Fact]
    public void PatchPtr_AppendingMissingTarget_OnlyTheLocatedPointerChanges()
    {
        var seed = BuildDatc64([(TargetId, PathOriginal), ("Other", "graphics/fx_other")]);
        var table = SeedTable(seed);
        var json = WritePatch("ptr-append", PtrOp(table, PathFresh));

        Run(_root, json, "apply");
        var applied = Read(table)!;
        Assert.True(applied.Length > seed.Length, "缺失的目标串应追加到字符串池末尾");
        Assert.Equal(PathFresh, PathOfRow(table, TargetId));
        Assert.Equal("graphics/fx_other", PathOfRow(table, "Other"));
        AssertPrefixUnchangedExceptPointer(seed, applied, PathFresh);

        Run(_root, json, "revert");
        var reverted = Read(table)!;
        Assert.Equal(PathOriginal, PathOfRow(table, TargetId));
        // 池里追加的串按设计不回收，但原表的全部字节都必须回来了
        Assert.True(seed.AsSpan().SequenceEqual(reverted.AsSpan(0, seed.Length)),
            "还原后原表字节未恢复");
    }

    /// <summary>被改写的字节只有定位到的那条指针：把它换回原始值后，两份表的前缀应完全相同。</summary>
    private static void AssertPrefixUnchangedExceptPointer(byte[] seed, byte[] applied, string appliedTarget)
    {
        var layout = FxDatc64Pointers.ParseLayout(applied);
        var located = FxDatc64Pointers.LocatePtrField(applied, layout, TargetId, PathOriginal, appliedTarget)
            ?? throw new InvalidOperationException("apply 之后定位不到该字段");
        var start = 4 + located.Row * layout.RowLen + located.FieldOffset;

        var normalized = (byte[])applied.Clone();
        Array.Copy(seed, start, normalized, start, 8);

        Assert.True(seed.AsSpan().SequenceEqual(normalized.AsSpan(0, seed.Length)),
            $"除 ROW[{located.Row}] +{located.FieldOffset} 之外的字节被改动了");
    }

    // ═══ P-2：重复 apply 必须无副作用 ═══

    [Fact]
    public void ApplyTwice_LeavesTheTargetIdentical()
    {
        var json = WritePatch("twice", new
        {
            Op = "edittext",
            Path = NotesPath,
            Old = "beta",
            New = "BETA",
        });

        Run(_root, json, "apply");
        var once = Read(NotesPath)!;

        Run(_root, json, "apply");
        AssertExactly(once, Read(NotesPath));
    }

    // ═══ P-3：多个补丁共存，revert 顺序不影响结果 ═══

    [Fact]
    public void TwoIndependentPatches_RevertedInApplyOrder_StillRestoreBoth()
    {
        var first = WritePatch("prop-first", new
        {
            Op = "edittext",
            Path = NotesPath,
            Old = "beta",
            New = "BETA",
        });
        var second = WritePatch("prop-second", new
        {
            Op = "edittext",
            Path = OtherPath,
            Old = "two",
            New = "TWO",
        });

        Run(_root, first, "apply");
        Run(_root, second, "apply");

        Assert.Equal("alpha BETA gamma", ReadText(NotesPath));
        Assert.Equal("one TWO three", ReadText(OtherPath));

        // 与叠加顺序相反地撤销
        Run(_root, first, "revert");
        Run(_root, second, "revert");

        AssertExactly(NotesOriginal, Read(NotesPath));
        AssertExactly(OtherOriginal, Read(OtherPath));
    }

    // ═══ P-4：状态归约层（UI 勾选默认值与 status 输出的依据）═══

    /// <summary>
    /// 落盘之外的另一半承诺：一个补丁里只有一部分操作生效时，整体必须判为「冲突」而不是「已应用」或「未应用」。
    /// P-1~P-3 断言的是文件字节，删掉 <c>OverallOf</c> 的参半分支它们照样全绿，所以这条单独钉住归约逻辑。
    /// </summary>
    [Fact]
    public void HalfAppliedPatch_OverallIsConflict()
    {
        var json = WritePatch("half",
            new { Op = "edittext", Path = NotesPath, Old = "beta", New = "BETA" },
            new { Op = "edittext", Path = OtherPath, Old = "two", New = "TWO" });

        Run(_root, json, "apply");
        Assert.Equal(PatchState.Applied, OverallOf(json));

        // 手工把其中一个文件改回原样，制造「参半」状态
        var undo = WritePatch("half-undo-one", new
        {
            Op = "edittext",
            Path = OtherPath,
            Old = "TWO",
            New = "two",
        });
        Run(_root, undo, "apply");

        Assert.Equal(PatchState.Conflict, OverallOf(json));
    }

    /// <summary>用引擎自己的判定口径读回一个补丁的整体状态。</summary>
    private PatchState OverallOf(string patchFile)
    {
        using var gd = GameDataAccess.OpenReadOnlyMapped(_root);
        return FxPatchState.OverallOf(FxPatchState.ComputeStates(gd, FxPatchPackage.LoadPatchFile(patchFile)));
    }

    // ═══ 夹具 ═══

    private const string TargetId = "Target";
    private const string PathOriginal = "graphics/fx_orig";
    private const string PathPatched = "graphics/fx_patched";
    private const string PathFresh = "graphics/fx_fresh";

    /// <summary>夹具表的行宽：Id 与 AOFile 各一个 int64 指针。</summary>
    private const int RowLen = 16;

    private object PtrOp(string table, string newPath) => new
    {
        Op = "patchptr-byid",
        Table = table,
        Id = TargetId,
        OriginalPath = PathOriginal,
        NewPath = newPath,
    };

    /// <summary>把表内容作为一个额外文件写进 seed 索引（重建 Bundles2，原有两个文本文件保留）。</summary>
    private string SeedTable(byte[] content)
    {
        const string path = "data/table.datc64";
        SeedIndex.Build(
            Path.Combine(_root, "Bundles2"),
            (NotesPath, NotesOriginal),
            (OtherPath, OtherOriginal),
            (path, content));
        return path;
    }

    /// <summary>
    /// 最小可解析的 datc64 表：行数（int32）+ 定长行区（每行 Id、AOFile 两个 int64 指针）
    /// + 0xBB×8 分隔符 + UTF-16LE 字符串池。指针按引擎实测的相对公式存：v = abs - dataOffset + 8。
    /// <paramref name="unreferenced"/> 里的字符串进池但不被任何行引用。
    /// </summary>
    private static byte[] BuildDatc64((string Id, string Path)[] rows, params string[] unreferenced)
    {
        var dataOffset = 4 + rows.Length * RowLen + 8;
        var pool = new MemoryStream();
        var offsets = new Dictionary<string, int>();

        int OffsetOf(string text)
        {
            if (offsets.TryGetValue(text, out var known))
                return known;
            var abs = dataOffset + (int)pool.Length;
            pool.Write(Encoding.Unicode.GetBytes(text));
            pool.Write(stackalloc byte[] { 0, 0 });
            offsets[text] = abs;
            return abs;
        }

        foreach (var text in unreferenced)
            OffsetOf(text);

        var fixedArea = new byte[4 + rows.Length * RowLen];
        BitConverter.GetBytes(rows.Length).CopyTo(fixedArea, 0);
        for (var i = 0; i < rows.Length; i++)
        {
            var rowStart = 4 + i * RowLen;
            BitConverter.GetBytes((long)OffsetOf(rows[i].Id) - dataOffset + 8).CopyTo(fixedArea, rowStart);
            BitConverter.GetBytes((long)OffsetOf(rows[i].Path) - dataOffset + 8).CopyTo(fixedArea, rowStart + 8);
        }

        var result = new byte[fixedArea.Length + 8 + pool.Length];
        fixedArea.CopyTo(result, 0);
        for (var i = 0; i < 8; i++)
            result[fixedArea.Length + i] = 0xBB;
        pool.ToArray().CopyTo(result, fixedArea.Length + 8);
        return result;
    }

    /// <summary>按 Id 定位行，读回该行 AOFile 字段当前指向的字符串。</summary>
    private string PathOfRow(string table, string id)
    {
        var dat = Read(table)!;
        var layout = FxDatc64Pointers.ParseLayout(dat);
        for (var q = 4; q + 8 <= layout.SepPos; q++)
        {
            var abs = BitConverter.ToInt64(dat, q) + layout.DataOffset - 8;
            if (abs < layout.DataOffset || abs >= dat.Length
                || FxDatc64Pointers.DecodeUtf16At(dat, (int)abs) != id)
                continue;

            var pathAbs = BitConverter.ToInt64(dat, 4 + ((q - 4) / layout.RowLen) * layout.RowLen + 8)
                + layout.DataOffset - 8;
            return FxDatc64Pointers.DecodeUtf16At(dat, (int)pathAbs);
        }
        throw new InvalidOperationException($"表中找不到 Id={id}");
    }

    private string WritePatch(string id, params object[] operations)
    {
        var dir = Path.Combine(_patchDir, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".patch.json");
        var json = JsonSerializer.Serialize(
            new { PatchId = id, BundleName = id, Operations = operations },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }

    private void WriteAsset(string patchId, string relative, byte[] content)
    {
        var path = Path.Combine(_patchDir, patchId, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void WriteAsset(string patchId, string relative, string content)
        => WriteAsset(patchId, relative, Encoding.UTF8.GetBytes(content));

    private byte[]? Read(string path)
    {
        using var gd = GameDataAccess.OpenReadOnlyMapped(_root);
        return gd.ReadFile(path);
    }

    private string ReadText(string path)
        => Encoding.UTF8.GetString(Read(path)!);

    private static void AssertExactly(byte[] expected, byte[]? actual)
    {
        Assert.NotNull(actual);
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            "文件内容没有逐字节还原：\n期望 " + Convert.ToHexString(expected) + "\n实际 " + Convert.ToHexString(actual));
    }

    private static void Run(params string[] args)
    {
        var log = new List<string>();
        FxPatchEngine.LogSink = log.Add;
        try
        {
            var code = FxPatchEngine.Run(args, null);
            Assert.Equal(0, code);
        }
        finally
        {
            FxPatchEngine.LogSink = null;
        }
    }
}
