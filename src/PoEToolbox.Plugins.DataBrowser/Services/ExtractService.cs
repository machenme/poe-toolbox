using System.IO;
using LibBundle3;
using LibBundle3.Nodes;
using LibBundle3.Records;
using BundleIndex = LibBundle3.Index;
using PoEToolbox.Core.Binary.Datc64;

namespace PoEToolbox.Plugins.DataBrowser.Services;

public sealed record ExtractionProgress(
    int CompletedFiles,
    int TotalFiles,
    int FailedFiles,
    string? CurrentPath);

public sealed record ExtractionResult(
    int CompletedFiles,
    int FailedFiles,
    bool IsCancelled,
    IReadOnlyList<string> FailedPaths);

public static class ExtractService
{
    /// <summary>Extract a single file to an exact destination path.</summary>
    public static async Task<ExtractionResult> ExtractFileToPathAsync(
        FileRecord file,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(destPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            var data = await Task.Run(() => file.Read().ToArray(), cancellationToken);
            await WriteBytesAtomicallyAsync(destPath, data, cancellationToken);
            return new ExtractionResult(1, 0, false, []);
        }
        catch (OperationCanceledException)
        {
            return new ExtractionResult(0, 0, true, []);
        }
        catch
        {
            return new ExtractionResult(0, 1, false, [file.Path ?? file.PathHash.ToString("X16")]);
        }
    }

    internal static async Task WriteBytesAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var temporaryPath = destinationPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                await stream.WriteAsync(data, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>Decode a dat table and write its rows as a JSON file.</summary>
    public static async Task<ExtractionResult> ExtractDatc64AsJsonAsync(
        FileRecord file,
        string sourcePath,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(destPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            var data = await Task.Run(() => file.Read().ToArray(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var tableName = Datc64Constants.TableNameFromPath(sourcePath);
            var (is64Bit, validFor) = Datc64File.DetectFromExtension(sourcePath);
            var temporaryPath = destPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                await Task.Run(
                    () => Datc64File.ExportJson(data, tableName, temporaryPath, is64Bit, validFor),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, destPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            return new ExtractionResult(1, 0, false, []);
        }
        catch (OperationCanceledException)
        {
            return new ExtractionResult(0, 0, true, []);
        }
        catch
        {
            return new ExtractionResult(0, 1, false, [sourcePath]);
        }
    }

    public static async Task<ExtractionResult> ExtractDirectoryAsync(
        IDirectoryNode dirNode,
        string destDir,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = BundleIndex.Recursefiles(dirNode).Select(node => node.Record);
        return await ExtractFilesAsync(files, ITreeNode.GetPath(dirNode), destDir, progress, cancellationToken);
    }

    public static async Task<ExtractionResult> ExtractFilesAsync(
        IEnumerable<FileRecord> sourceFiles,
        string sourceRoot,
        string destDir,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = sourceFiles.ToList();
        var total = files.Count;
        var completed = 0;
        var failed = 0;
        var failedPaths = new List<string>();
        var normalizedRoot = NormalizeVirtualPath(sourceRoot);

        if (total == 0)
            return new ExtractionResult(0, 0, false, []);

        Directory.CreateDirectory(destDir);
        try
        {
            // ExtractParallel groups records by Bundle and keeps files from the same
            // Bundle on one worker, so each Bundle is decompressed only once while
            // independent Bundles can be processed concurrently.
            await Task.Run(() =>
            {
                BundleIndex.ExtractParallel(files, (file, content) =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return true;

                    var fullPath = file.Path ?? file.PathHash.ToString("X16");
                    var virtualPath = GetRelativePath(fullPath, normalizedRoot);
                    try
                    {
                        if (content is null || virtualPath is null || !TryGetSafeDestination(destDir, virtualPath, out var targetPath))
                        {
                            Interlocked.Increment(ref failed);
                            lock (failedPaths) failedPaths.Add(fullPath);
                        }
                        else
                        {
                            var parent = Path.GetDirectoryName(targetPath);
                            if (parent is not null) Directory.CreateDirectory(parent);
                            WriteBytesAtomically(targetPath, content.Value.Span);
                            Interlocked.Increment(ref completed);
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref failed);
                        lock (failedPaths) failedPaths.Add(fullPath);
                    }

                    var processed = Volatile.Read(ref completed) + Volatile.Read(ref failed);
                    if (processed == 1 || processed == total || processed % 64 == 0)
                    {
                        progress?.Report(new ExtractionProgress(
                            Volatile.Read(ref completed), total, Volatile.Read(ref failed), fullPath));
                    }
                    return false;
                });
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new ExtractionResult(completed, failed, true, failedPaths);
        }

        return new ExtractionResult(completed, failed, cancellationToken.IsCancellationRequested, failedPaths);
    }

    private static void WriteBytesAtomically(string destinationPath, ReadOnlySpan<byte> data)
    {
        var temporaryPath = destinationPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string NormalizeVirtualPath(string path)
        => path.Replace('\\', '/').Trim('/');

    private static string? GetRelativePath(string fullPath, string sourceRoot)
    {
        var normalizedPath = NormalizeVirtualPath(fullPath);
        if (string.IsNullOrEmpty(sourceRoot)) return normalizedPath;
        if (normalizedPath.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(normalizedPath);

        var prefix = sourceRoot + "/";
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedPath[prefix.Length..]
            : null;
    }

    public static bool TryGetSafeDestination(string rootDirectory, string virtualPath, out string destination)
    {
        destination = "";
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(virtualPath))
            return false;

        var normalizedVirtualPath = virtualPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedVirtualPath)) return false;

        var segments = normalizedVirtualPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) return false;

        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, normalizedVirtualPath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

        destination = candidate;
        return true;
    }
}
