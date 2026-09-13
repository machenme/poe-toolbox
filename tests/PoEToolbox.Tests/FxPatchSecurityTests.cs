using PoEToolbox.Shared;
using System.IO.Compression;
using Xunit;

namespace PoEToolbox.Tests;

public sealed class FxPatchSecurityTests
{
    [Theory]
    [InlineData("..\\outside.txt")]
    [InlineData("../outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("/outside.txt")]
    public void PatchFilePath_RejectsEscapingPaths(string relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "poe-toolbox-test-root", Guid.NewGuid().ToString("N"));

        Assert.False(FxPatchEngine.TryGetSafeChildPath(root, relativePath, out _));
    }

    [Fact]
    public void PatchFilePath_AllowsChildAndKeepsItUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "poe-toolbox-test-root", Guid.NewGuid().ToString("N"));

        Assert.True(FxPatchEngine.TryGetSafeChildPath(root, "assets\\effect.ao", out var destination));
        Assert.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            destination, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractZipPatch_RejectsTraversalEntry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "poe-toolbox-zip-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var zipPath = Path.Combine(tempRoot, "malicious.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntry("patch.json").Open().Dispose();
                archive.CreateEntry("../outside.txt").Open().Dispose();
            }

            Assert.Null(FxPatchEngine.ExtractZipPatch(zipPath));
            Assert.False(File.Exists(Path.Combine(tempRoot, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
