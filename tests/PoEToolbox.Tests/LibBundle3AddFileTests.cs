using System.IO;
using System.Reflection;
using System.Text;
using LibBundle3;
using LibBundle3.Records;
using Xunit;

using Index = LibBundle3.Index;

namespace PoEToolbox.Tests;

/// <summary>
/// Tests for <see cref="Index.AddFile"/> / <see cref="Index.CopyFile"/>: adding a brand-new path to
/// a Bundles2 index without touching any existing file, and keeping the new path resolvable after reopening.
/// </summary>
public sealed class LibBundle3AddFileTests : IDisposable
{
    private const string SeedPath = "metadata/effects/spells/grd_zones/grd_burning01.ao";
    private const string OtherSeedPath = "metadata/effects/spells/grd_zones/grd_burning02.ao";
    private const string NewPath = "metadata/effects/spells/grd_zones/grd_burning01_oil.ao";

    private const string SeedText =
        "{\"name\":\"loop\",\"events\":[{\"type\":\"TimelineParameterEventType\",\"name\":\"grdZone_FADE_CTRLs\",\"curve\":\"2 0 0 Linear 0.25 0 Linear\"}]}";
    private const string OtherSeedText =
        "{\"name\":\"loop\",\"events\":[{\"type\":\"TimelineParameterEventType\",\"name\":\"grdZone_FADE_CTRLs\",\"curve\":\"2 0 0 Linear 0.25 0 Linear\"}]}";

    private readonly string _directory;

    public LibBundle3AddFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "poetoolbox-addfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
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

    [Fact]
    public void AddFile_RoundTrips_AndKeepsExistingFilesUntouched()
    {
        var indexPath = BuildSeedIndex();
        var seedBytes = Encoding.UTF8.GetBytes(SeedText);
        var otherSeedBytes = Encoding.UTF8.GetBytes(OtherSeedText);
        var newContent = Encoding.UTF8.GetBytes("{\"curve\":\"2 0 0 Linear 0.25 1 Linear\"}");

        using (var index = new Index(indexPath, parsePaths: true))
        {
            Assert.True(index.TryGetFile(SeedPath, out var seed));
            Assert.Equal(SeedText, Encoding.UTF8.GetString(seed!.Read().Span));

            var added = index.AddFile(NewPath, newContent);
            Assert.Equal(NewPath, added.Path);
            Assert.Equal(newContent.Length, added.Size);
            index.Save();
        }

        // parsePaths: true throws when any path fails to parse, so reopening also proves the
        // appended directory-table entry is consistent with the file table.
        using var reopened = new Index(indexPath, parsePaths: true);
        Assert.Equal(3, reopened.Files.Count);

        Assert.True(reopened.TryGetFile(NewPath, out var created));
        Assert.Equal(NewPath, created!.Path);
        Assert.Equal(newContent, created.Read().ToArray());

        Assert.True(reopened.TryGetFile(SeedPath, out var original));
        Assert.Equal(SeedPath, original!.Path);
        Assert.Equal(seedBytes, original.Read().ToArray());

        Assert.True(reopened.TryGetFile(OtherSeedPath, out var other));
        Assert.Equal(otherSeedBytes, other!.Read().ToArray());
    }

    [Fact]
    public void CopyFile_ThenEditingCopy_DoesNotAffectOriginal()
    {
        var indexPath = BuildSeedIndex();
        var seedBytes = Encoding.UTF8.GetBytes(SeedText);

        var editedCopy = Encoding.UTF8.GetBytes(SeedText.Replace(
            "Linear 0.25 0 Linear",
            "Linear 0.25 1 Linear",
            StringComparison.Ordinal));

        using (var index = new Index(indexPath, parsePaths: true))
        {
            var copy = index.CopyFile(SeedPath, NewPath);
            Assert.Equal(NewPath, copy.Path);
            Assert.Equal(seedBytes.Length, copy.Size);

            // Edit the copy only; the original record must keep its own content.
            copy.Write(editedCopy);
            index.Save();
        }

        using var reopened = new Index(indexPath, parsePaths: true);

        Assert.True(reopened.TryGetFile(NewPath, out var created));
        Assert.Equal(editedCopy, created!.Read().ToArray());

        Assert.True(reopened.TryGetFile(SeedPath, out var original));
        Assert.Equal(seedBytes, original!.Read().ToArray());
        Assert.Contains("Linear 0.25 0 Linear", Encoding.UTF8.GetString(original.Read().Span));
    }

    [Fact]
    public void AddFile_WithExistingPath_Throws_AndLeavesIndexUnchanged()
    {
        var indexPath = BuildSeedIndex();

        using var index = new Index(indexPath, parsePaths: true);
        var before = index.Files.Count;

        Assert.Throws<InvalidOperationException>(() => { index.AddFile(SeedPath, [1, 2, 3]); });
        Assert.Equal(before, index.Files.Count);

        // The original file is still readable and unmodified.
        Assert.True(index.TryGetFile(SeedPath, out var seed));
        Assert.Equal(Encoding.UTF8.GetBytes(SeedText), seed!.Read().ToArray());
    }

    [Fact]
    public void Read_BeforeSave_ReturnsPendingContent_FromWriteBundle()
    {
        var indexPath = BuildSeedIndex();
        var seedBytes = Encoding.UTF8.GetBytes(SeedText);
        var first = Encoding.UTF8.GetBytes("{\"effect\":\"burning\"}");
        var second = Encoding.UTF8.GetBytes("{\"effect\":\"ignition\"}");

        // Reproduces the patch-engine sequence: two new files are appended into the write bundle
        // and a base file is redirected into it, then the next operation reads the redirected
        // file back — all before Save. The pending content lives only in the write buffer, so
        // reads must not go through the bundle's (stale) metadata.
        using (var index = new Index(indexPath, parsePaths: true))
        {
            index.AddFile(NewPath, first);
            var redirectedPath = "metadata/effects/spells/grd_zones/grd_burning02.ao";
            Assert.True(index.TryGetFile(redirectedPath, out var redirected));
            redirected!.Write(second);
            Assert.True(index.TryGetFile(OtherSeedPath, out var other));
            Assert.Equal(second, other!.Read().ToArray());

            Assert.True(index.TryGetFile(NewPath, out var added));
            Assert.Equal(first, added!.Read().ToArray());

            // The untouched seed file keeps its original content.
            Assert.True(index.TryGetFile(SeedPath, out var seed));
            Assert.Equal(seedBytes, seed!.Read().ToArray());

            index.Save();
        }

        using var reopened = new Index(indexPath, parsePaths: true);
        Assert.True(reopened.TryGetFile(NewPath, out var pending));
        Assert.Equal(first, pending!.Read().ToArray());
        Assert.True(reopened.TryGetFile(OtherSeedPath, out var after));
        Assert.Equal(second, after!.Read().ToArray());
        Assert.True(reopened.TryGetFile(SeedPath, out var original));
        Assert.Equal(seedBytes, original!.Read().ToArray());
    }

    [Fact]
    public void DisposeWithoutSave_DoesNotPersistPendingMutation()
    {
        var indexPath = BuildSeedIndex();

        using (var index = new Index(indexPath, parsePaths: true))
        {
            index.AddFile(NewPath, Encoding.UTF8.GetBytes("pending"), saveIndex: false);
        }

        using var reopened = new Index(indexPath, parsePaths: true);
        Assert.Equal(2, reopened.Files.Count);
        Assert.False(reopened.TryGetFile(NewPath, out _));
    }

    [Fact]
    public void PurgeCustomBundles_RemovesPatchFilesAndKeepsBaseFiles()
    {
        var indexPath = BuildSeedIndex();
        var patchPath = "PATCHED/TestPatch_1";
        var patchFile = "metadata/effects/spells/grd_zones/test_patch.ao";

        IReadOnlyList<(ulong PathHash, int Offset, int Size, int RecursiveSize)> pristineRecords;
        int pristineBlobLength;
        using (var index = new Index(indexPath, parsePaths: true))
        {
            pristineRecords = ReadDirectoryRecords(index);
            pristineBlobLength = ReadDirectoryBlobLength(index);

            index.PinnedWriteBundlePath = patchPath;
            index.AddFile(patchFile, Encoding.UTF8.GetBytes("patch"));
            index.Save();
        }

        var bundleFile = Path.Combine(_directory, patchPath.Replace('/', Path.DirectorySeparatorChar) + ".bundle.bin");
        Assert.True(File.Exists(bundleFile));

        using (var index = new Index(indexPath, parsePaths: true))
        {
            var removed = index.PurgeCustomBundles("PATCHED/TestPatch_", Owned(patchFile), saveIndex: true);
            Assert.Equal(1, removed);
            Assert.False(index.TryGetFile(patchFile, out _));
            Assert.True(index.TryGetFile(SeedPath, out _));
        }

        Assert.False(File.Exists(bundleFile));
        // The directory table must come out of purge structurally identical to how it was
        // before the patch was applied. It must never be regenerated wholesale: the game
        // client builds its filesystem view from the per-directory records, and a rebuild
        // collapses it into one root record, which left the client unable to resolve any
        // path (2026-09-13 launch crash).
        using var reopened = new Index(indexPath, parsePaths: true);
        var recordsAfter = ReadDirectoryRecords(reopened);
        Assert.Equal(pristineRecords.Count, recordsAfter.Count); // appended record dropped, not collapsed
        Assert.All(recordsAfter, r => Assert.NotEqual(0, r.RecursiveSize)); // client mis-reads zero spans
        Assert.Equal(pristineBlobLength, ReadDirectoryBlobLength(reopened)); // no orphan entry left behind

        Assert.Equal(2, reopened.Files.Count);
        Assert.False(reopened.TryGetFile(patchFile, out _));
        Assert.True(reopened.TryGetFile(SeedPath, out _));
        Assert.True(reopened.TryGetFile(OtherSeedPath, out _));
    }

    [Fact]
    public void PurgeCustomBundles_KeepsBaseGameFilesThatWereRedirectedIntoThePatchBundle()
    {
        var indexPath = BuildSeedIndex();
        var patchBundle = "PATCHED/TestPatch_v1";
        var seedBytes = Encoding.UTF8.GetBytes(SeedText);

        // Reproduces the state a reverted patch leaves behind: editing a base-game file redirects its
        // record into the patch bundle, so the base file's only content carrier is the patch bundle.
        using (var index = new Index(indexPath, parsePaths: true))
        {
            index.PinnedWriteBundlePath = patchBundle;
            Assert.True(index.TryGetFile(SeedPath, out var seed));
            seed!.Write(seedBytes);
            index.AddFile(NewPath, Encoding.UTF8.GetBytes("patch copy"));
            index.Save();
        }

        using (var index = new Index(indexPath, parsePaths: true))
        {
            Assert.True(index.TryGetFile(SeedPath, out var moved));
            Assert.Equal(patchBundle + ".bundle.bin", moved!.BundleRecord.Path);

            // Only the path the patch created may be removed; the redirected base file must survive.
            var removed = index.PurgeCustomBundles("PATCHED/TestPatch_", Owned(NewPath), saveIndex: true);

            Assert.Equal(1, removed);
            Assert.False(index.TryGetFile(NewPath, out _));
            Assert.True(index.TryGetFile(SeedPath, out var kept));
            Assert.Equal(seedBytes, kept!.Read().ToArray());
        }

        // The base file must still resolve — and still carry its content — after reopening.
        // parsePaths: true also proves the purged path's directory entry was removed cleanly
        // (any unresolved path would throw here).
        using var reopened = new Index(indexPath, parsePaths: true);
        Assert.True(reopened.TryGetFile(SeedPath, out var after));
        Assert.Equal(seedBytes, after!.Read().ToArray());
        Assert.False(reopened.TryGetFile(NewPath, out _));
    }

    /// <summary>Path set (case-insensitive) that a patch declares as its own output.</summary>
    private static HashSet<string> Owned(params string[] paths)
        => new(paths, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AddFile_RejectsInvalidPaths()
    {
        var indexPath = BuildSeedIndex();

        using var index = new Index(indexPath, parsePaths: true);

        Assert.Throws<ArgumentException>(() => { index.AddFile("", [1]); });
        Assert.Throws<ArgumentException>(() => { index.AddFile("/absolute/path.ao", [1]); });
        Assert.Throws<ArgumentException>(() => { index.AddFile("trailing/slash/", [1]); });
        Assert.Throws<ArgumentException>(() => { index.AddFile("back\\slash.ao", [1]); });
        Assert.Equal(2, index.Files.Count);
    }

    [Fact]
    public void AddFile_MultipleFiles_SavedAtOnce_AndFlushesAcrossMaxBundleSize()
    {
        var indexPath = BuildSeedIndex();
        var contents = new List<(string Path, byte[] Content)>();

        using (var index = new Index(indexPath, parsePaths: true))
        {
            // Force a split into several custom bundles while adding.
            index.MaxBundleSize = 512;

            for (var i = 0; i < 4; i++)
            {
                var path = $"metadata/effects/spells/grd_zones/generated_{i}.ao";
                var content = Encoding.UTF8.GetBytes(new string((char)('a' + i), 400));
                contents.Add((path, content));
                index.AddFile(path, content);
            }

            Assert.Equal(6, index.Files.Count); // 2 seeds + 4 new files
            index.Save();
        }

        using var reopened = new Index(indexPath, parsePaths: true);
        Assert.Equal(6, reopened.Files.Count);

        foreach (var (path, content) in contents)
        {
            Assert.True(reopened.TryGetFile(path, out var file));
            Assert.Equal(path, file!.Path);
            Assert.Equal(content, file.Read().ToArray());
        }

        // The seed files still resolve and are still intact.
        Assert.True(reopened.TryGetFile(SeedPath, out var seed));
        Assert.Equal(Encoding.UTF8.GetBytes(SeedText), seed!.Read().ToArray());
    }

    [Fact]
    public void PinnedWriteBundle_KeepsEveryChangeInOneBundle_EvenAcrossMaxBundleSize()
    {
        var indexPath = BuildSeedIndex();

        using (var index = new Index(indexPath, parsePaths: true))
        {
            index.PinnedWriteBundlePath = Index.DefaultWriteBundlePath;
            index.MaxBundleSize = 256; // force several flushes to disk while writing

            for (var i = 0; i < 6; i++)
            {
                var content = Encoding.UTF8.GetBytes(new string((char)('a' + i), 200));
                index.AddFile($"metadata/effects/spells/grd_zones/pinned_{i}.ao", content);
            }

            index.Save();
        }

        using var reopened = new Index(indexPath, parsePaths: true);
        Assert.Equal(8, reopened.Files.Count); // 2 seeds + 6 new files

        // No LibGGPK3/1, 2, 3, ... may be created: one single write bundle, exactly the pinned one.
        var writeBundles = reopened.Bundles.ToArray()
            .Where(b => b.Path.StartsWith("LibGGPK3/", StringComparison.Ordinal))
            .ToList();
        var only = Assert.Single(writeBundles);
        Assert.Equal("LibGGPK3/0.bundle.bin", only.Path);

        for (var i = 0; i < 6; i++)
        {
            var path = $"metadata/effects/spells/grd_zones/pinned_{i}.ao";
            Assert.True(reopened.TryGetFile(path, out var file));
            Assert.Equal("LibGGPK3/0.bundle.bin", file!.BundleRecord.Path);
            Assert.Equal(path, file.Path);
            Assert.Equal(Encoding.UTF8.GetBytes(new string((char)('a' + i), 200)), file.Read().ToArray());
        }

        // The seed files must still live in their original bundle and be untouched.
        Assert.True(reopened.TryGetFile(SeedPath, out var seed));
        Assert.Equal("seedbundle.bundle.bin", seed!.BundleRecord.Path);
        Assert.Equal(Encoding.UTF8.GetBytes(SeedText), seed.Read().ToArray());
    }

    [Fact]
    public void PinnedWriteBundle_AllowsSafeSubdirectories_AndRejectsUnsafePaths()
    {
        var indexPath = BuildSeedIndex();

        using var index = new Index(indexPath, parsePaths: true);

        // Safe relative subdirectory paths are allowed (per-patch bundles like PATCHED/<name>).
        index.PinnedWriteBundlePath = "PATCHED/OilGrenade";
        Assert.Equal("PATCHED/OilGrenade", index.PinnedWriteBundlePath);
        index.PinnedWriteBundlePath = Index.DefaultWriteBundlePath;
        Assert.Equal(Index.DefaultWriteBundlePath, index.PinnedWriteBundlePath);

        // Unsafe paths are rejected: absolute, escaping, no subdirectory, or raw file extension.
        Assert.Throws<ArgumentException>(() => { index.PinnedWriteBundlePath = "/Patch/0"; });
        Assert.Throws<ArgumentException>(() => { index.PinnedWriteBundlePath = "../evil"; });
        Assert.Throws<ArgumentException>(() => { index.PinnedWriteBundlePath = "0"; });
        Assert.Throws<ArgumentException>(() => { index.PinnedWriteBundlePath = "LibGGPK3/0.bundle.bin"; });

        // Rejected assignments must not clobber the previously accepted value.
        Assert.Equal(Index.DefaultWriteBundlePath, index.PinnedWriteBundlePath);
    }

    [Fact]
    public void PinnedWriteBundle_ResumesAnExistingBundleAcrossSessions()
    {
        var indexPath = BuildSeedIndex();

        AddPinnedFiles(indexPath, 0, 2);
        AddPinnedFiles(indexPath, 2, 2); // second session must resume LibGGPK3/0, not create LibGGPK3/1

        using var reopened = new Index(indexPath, parsePaths: true);
        var writeBundles = reopened.Bundles.ToArray()
            .Where(b => b.Path.StartsWith("LibGGPK3/", StringComparison.Ordinal))
            .ToList();
        var only = Assert.Single(writeBundles);
        Assert.Equal("LibGGPK3/0.bundle.bin", only.Path);

        for (var i = 0; i < 4; i++)
        {
            var path = $"metadata/effects/spells/grd_zones/resume_{i}.ao";
            Assert.True(reopened.TryGetFile(path, out var file));
            Assert.Equal("LibGGPK3/0.bundle.bin", file!.BundleRecord.Path);
            Assert.Equal(Encoding.UTF8.GetBytes(new string((char)('a' + i), 300)), file.Read().ToArray());
        }

        // Previous sessions' data must survive the appends.
        Assert.True(reopened.TryGetFile(SeedPath, out var seed));
        Assert.Equal(Encoding.UTF8.GetBytes(SeedText), seed!.Read().ToArray());
    }

    /// <summary>Opens the index, pins writes to LibGGPK3/0, adds files and saves (one "session").</summary>
    private static void AddPinnedFiles(string indexPath, int from, int count)
    {
        using var index = new Index(indexPath, parsePaths: true);
        index.PinnedWriteBundlePath = Index.DefaultWriteBundlePath;
        index.MaxBundleSize = 400; // force flushes so the next session has to resume from disk
        for (var i = from; i < from + count; i++)
        {
            var content = Encoding.UTF8.GetBytes(new string((char)('a' + i), 300));
            index.AddFile($"metadata/effects/spells/grd_zones/resume_{i}.ao", content);
        }
        index.Save();
    }

    [Fact]
    public void AddFile_AppendsADirectoryRecordWithAConsistentRecursiveSize()
    {
        var indexPath = BuildSeedIndex();
        int rootRecursiveBefore;
        using (var index = new Index(indexPath, parsePaths: true))
        {
            rootRecursiveBefore = ReadDirectoryRecords(index)[0].RecursiveSize;
            index.AddFile(NewPath, Encoding.UTF8.GetBytes("x"));
            index.Save();
        }

        using var reopened = new Index(indexPath, parsePaths: true);
        var records = ReadDirectoryRecords(reopened);

        // A real index never contains RecursiveSize == 0, and the game client mis-reads the
        // directory table when it does (it surfaced as "Unknown token" for unrelated resources).
        Assert.All(records, r => Assert.NotEqual(0, r.RecursiveSize));

        var appended = records[^1];
        Assert.Equal(appended.Size, appended.RecursiveSize); // leaf record
        Assert.Equal(rootRecursiveBefore + appended.Size, records[0].RecursiveSize); // root span covers the appended data
    }

    /// <summary>Reads the directory table of an index (there's no public accessor for it).</summary>
    private static IReadOnlyList<(ulong PathHash, int Offset, int Size, int RecursiveSize)> ReadDirectoryRecords(Index index)
    {
        var array = (Array?)typeof(Index)
            .GetField("_Directories", BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(index) ?? throw new InvalidOperationException("cannot read _Directories");
        var recordType = array.GetType().GetElementType()!;
        var fHash = recordType.GetField("PathHash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fOffset = recordType.GetField("Offset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fSize = recordType.GetField("Size", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fRecursive = recordType.GetField("RecursiveSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

        var records = new List<(ulong, int, int, int)>(array.Length);
        for (var i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i)!;
            records.Add((
                (ulong)fHash.GetValue(value)!,
                (int)fOffset.GetValue(value)!,
                (int)fSize.GetValue(value)!,
                (int)fRecursive.GetValue(value)!));
        }
        return records;
    }

    /// <summary>Reads the raw directory-blob length of an index (there's no public accessor for it).</summary>
    private static int ReadDirectoryBlobLength(Index index)
    {
        var data = (byte[]?)typeof(Index)
            .GetField("directoryBundleData", BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(index) ?? throw new InvalidOperationException("cannot read directoryBundleData");
        return data.Length;
    }

    // ── Tiny synthetic index builder ───────────────────────────

    /// <summary>Builds a minimal but valid Bundles2 "_.index.bin" containing two seed files.</summary>
    private string BuildSeedIndex() => SeedIndex.Build(
        _directory,
        (SeedPath, Encoding.UTF8.GetBytes(SeedText)),
        (OtherSeedPath, Encoding.UTF8.GetBytes(OtherSeedText)));
}
