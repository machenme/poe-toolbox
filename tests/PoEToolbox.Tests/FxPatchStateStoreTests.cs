using System.IO;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// The applied-patch ledger (<c>backup/applied-patches.json</c>) is what the UI reads to show which
/// patches are already on — it is display-only metadata, so it must never throw at the caller and
/// must survive a missing/corrupt file.
/// </summary>
public sealed class FxPatchStateStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _indexPath;

    public FxPatchStateStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "poetoolbox-fxstate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _indexPath = Path.Combine(_directory, "_.index.bin");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp directory.
        }
    }

    private FxPatchStateStore.AppliedPatch Entry(string id, string kind = FxPatchStateStore.KindBuiltIn)
        => new(id, "显示名-" + id, kind, "PATCHED/" + id, DateTimeOffset.UtcNow);

    [Fact]
    public void MissingFile_ReadsAsEmpty()
    {
        Assert.Empty(FxPatchStateStore.Read(_indexPath));
    }

    [Fact]
    public void CorruptFile_ReadsAsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FxPatchStateStore.FilePathOf(_indexPath))!);
        File.WriteAllText(FxPatchStateStore.FilePathOf(_indexPath), "{ this is not json");

        Assert.Empty(FxPatchStateStore.Read(_indexPath));
    }

    [Fact]
    public void MarkApplied_IsIdempotentPerId()
    {
        FxPatchStateStore.MarkApplied(_indexPath, Entry("oil-ground-fx-lite"));
        FxPatchStateStore.MarkApplied(_indexPath, Entry("oil-grenade-fx-lite"));
        FxPatchStateStore.MarkApplied(_indexPath, Entry("oil-ground-fx-lite"));

        var ids = FxPatchStateStore.Read(_indexPath).Select(p => p.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains("oil-ground-fx-lite", ids);
        Assert.Contains("oil-grenade-fx-lite", ids);
    }

    [Fact]
    public void MarkRemoved_DropsOnlyThatId()
    {
        FxPatchStateStore.MarkApplied(_indexPath, Entry("a"));
        FxPatchStateStore.MarkApplied(_indexPath, Entry("b"));

        FxPatchStateStore.MarkRemoved(_indexPath, "a");

        var ids = FxPatchStateStore.Read(_indexPath).Select(p => p.Id).ToList();
        Assert.Equal(["b"], ids);
    }

    [Fact]
    public void Clear_RemovesTheLedger()
    {
        FxPatchStateStore.MarkApplied(_indexPath, Entry("a"));
        Assert.NotEmpty(FxPatchStateStore.Read(_indexPath));

        FxPatchStateStore.Clear(_indexPath);

        Assert.Empty(FxPatchStateStore.Read(_indexPath));
    }

    [Fact]
    public void RoundTrip_KeepsKindAndName()
    {
        FxPatchStateStore.MarkApplied(_indexPath, Entry("my-pack", FxPatchStateStore.KindRawPack));

        var only = Assert.Single(FxPatchStateStore.Read(_indexPath));
        Assert.Equal("my-pack", only.Id);
        Assert.Equal(FxPatchStateStore.KindRawPack, only.Kind);
    }
}
