using System.IO;
using System.IO.MemoryMappedFiles;
using LibBundle3;
using LibBundledGGPK3;

namespace PoEToolbox.Shared;

/// <summary>
/// Unified game data access layer. Automatically detects and handles both formats:
///   - GGPK container (official client): Content.ggpk → BundledGGPK
///   - Bundles2 directory (Steam/Epic): _.index.bin → LibBundle3.Index
/// </summary>
public sealed class GameDataAccess : IDisposable
{
    private static int _openInstanceCount;
    private bool _isDirectIndex;
    private bool _registeredOpen;
    private bool _disposed;
    private BundledGGPK? _ggpk;
    private LibBundle3.Index? _index;
    private MemoryMappedFile? _mappedIndex;
    private string? _indexPath;
    private string? _gameDataPath;

    private GameDataAccess() { }

    /// <summary>Raised when the process starts or stops holding game data handles.</summary>
    public static event Action<bool>? LocksChanged;

    public static bool HasOpenLocks => Volatile.Read(ref _openInstanceCount) > 0;

    /// <summary>The underlying Index, regardless of format.</summary>
    public LibBundle3.Index Index => _index ?? _ggpk?.Index
        ?? throw new ObjectDisposedException(nameof(GameDataAccess));

    /// <summary>True if this is a Bundles2 (extracted) installation.</summary>
    public bool IsBundles2 => _isDirectIndex;

    /// <summary>The Content.ggpk or _.index.bin file opened by this instance.</summary>
    public string GameDataPath => _gameDataPath
        ?? throw new ObjectDisposedException(nameof(GameDataAccess));

    /// <summary>True when the opened client contains PoE2's BaseItemTypes table.</summary>
    public bool IsPoe2Client => Index.TryGetFile("data/balance/baseitemtypes.datc64", out _);

    // ── Open ───────────────────────────────────────────

    /// <summary>
    /// Open game data from a path. Auto-detects format:
    ///   - *.ggpk → GGPK container
    ///   - *.index.bin → Bundles2 directory
    ///   - directory → looks for Content.ggpk or Bundles2/_.index.bin inside
    /// </summary>
    public static GameDataAccess Open(string path, bool readOnly = false)
        => OpenCore(path, readOnly, memoryMapBundles2Index: false);

    /// <summary>
    /// Opens a data source for browsing only. A direct Bundles2 index is mapped
    /// read-only so the browser does not need a writable file handle.
    /// </summary>
    public static GameDataAccess OpenReadOnlyMapped(string path)
        => OpenCore(path, readOnly: true, memoryMapBundles2Index: true);

    private static GameDataAccess OpenCore(string path, bool readOnly, bool memoryMapBundles2Index)
    {
        var gd = new GameDataAccess();
        path = System.IO.Path.GetFullPath(path);

        if (System.IO.File.Exists(path))
        {
            if (path.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase))
            {
                gd.OpenGgpk(path);
                gd._gameDataPath = path;
                return gd.MarkOpened();
            }
            if (path.EndsWith("_.index.bin", StringComparison.OrdinalIgnoreCase))
            {
                gd.OpenBundles2Index(path, readOnly, memoryMapBundles2Index);
                return gd.MarkOpened();
            }
        }

        if (System.IO.Directory.Exists(path))
        {
            var ggpk = System.IO.Path.Combine(path, "Content.ggpk");
            if (System.IO.File.Exists(ggpk))
            {
                gd.OpenGgpk(ggpk);
                gd._gameDataPath = ggpk;
                return gd.MarkOpened();
            }
            var idx = System.IO.Path.Combine(path, "Bundles2", "_.index.bin");
            if (System.IO.File.Exists(idx))
            {
                gd.OpenBundles2Index(idx, readOnly, memoryMapBundles2Index);
                return gd.MarkOpened();
            }
        }

        throw new System.IO.FileNotFoundException($"Cannot find game data at: {path}");
    }

    private void OpenGgpk(string path)
    {
        // Some PoE2 indexes contain a few path records that cannot be resolved by
        // the library. Keep the valid paths so client detection and file access work.
        try
        {
            _ggpk = new BundledGGPK(path, parsePathsInIndex: false);
            _ggpk.Index.ParsePaths();
        }
        catch
        {
            _ggpk?.Dispose();
            _ggpk = null;
            throw;
        }
    }

    private GameDataAccess MarkOpened()
    {
        _registeredOpen = true;
        if (Interlocked.Increment(ref _openInstanceCount) == 1)
            LocksChanged?.Invoke(true);
        return this;
    }

    private void OpenBundles2Index(string indexPath, bool readOnly, bool memoryMapIndex)
    {
        var bundleDir = System.IO.Path.GetDirectoryName(indexPath)!;
        var bundleFactory = new DriveBundleFactory(bundleDir, readOnly);

        if (memoryMapIndex)
        {
            LibBundle3.Index? index = null;
            MemoryMappedFile? mappedIndex = null;
            try
            {
                using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                mappedIndex = MemoryMappedFile.CreateFromFile(
                    stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
                var view = mappedIndex.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
                index = new LibBundle3.Index(view, leaveOpen: false, parsePaths: false, bundleFactory);
                // Newer client indexes can include unresolved paths unrelated to game data we use.
                index.ParsePaths();

                _index = index;
                _mappedIndex = mappedIndex;
            }
            catch
            {
                index?.Dispose();
                mappedIndex?.Dispose();
                throw;
            }
        }
        else
        {
            _index = new LibBundle3.Index(indexPath, parsePaths: false, bundleFactory, readOnly);
            // Newer client indexes can include unresolved paths unrelated to game data we use.
            _index.ParsePaths();
        }

        _indexPath = indexPath;
        _gameDataPath = indexPath;
        _isDirectIndex = true;
    }

    // ── File access ────────────────────────────────────

    /// <summary>Try to find a file by path.</summary>
    public bool TryGetFile(string path, out LibBundle3.Records.FileRecord? file)
        => Index.TryGetFile(path, out file);

    /// <summary>Read a file to byte array, or null if not found.</summary>
    public byte[]? ReadFile(string path)
    {
        if (Index.TryGetFile(path, out var fr))
            return fr!.Read().ToArray();
        return null;
    }

    // ── Save ───────────────────────────────────────────

    /// <summary>Persist all changes.</summary>
    public void Save() => Index.Save();

    // ── Backup / Restore ───────────────────────────────

    /// <summary>Read raw index bytes (for backup). Uses ReadWrite share to coexist with open handles.</summary>
    public byte[] ReadIndexBytes()
    {
        if (_isDirectIndex)
        {
            using var fs = new System.IO.FileStream(_indexPath!, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return bytes;
        }
        else
        {
            var b2Dir = (LibGGPK3.Records.DirectoryRecord)_ggpk!.Root["Bundles2"]!;
            var idxNode = (LibGGPK3.Records.FileRecord)b2Dir["_.index.bin"]!;
            return idxNode.Read().ToArray();
        }
    }

    /// <summary>Write raw index bytes (for restore/replace).</summary>
    public void WriteIndexBytes(byte[] data)
    {
        if (_isDirectIndex)
        {
            // Close existing handle before overwriting
            _index?.Dispose();
            _mappedIndex?.Dispose();
            _mappedIndex = null;
            var tempPath = _indexPath! + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                System.IO.File.WriteAllBytes(tempPath, data);
                System.IO.File.Move(tempPath, _indexPath!, true);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
            var bundleDir = System.IO.Path.GetDirectoryName(_indexPath!)!;
            // Match Open's tolerant mode: newer client indexes can contain
            // unresolved paths that are unrelated to the files we access.
            _index = new LibBundle3.Index(_indexPath!, parsePaths: false,
                bundleFactory: new DriveBundleFactory(bundleDir));
            _index.ParsePaths();
        }
        else
        {
            var b2Dir = (LibGGPK3.Records.DirectoryRecord)_ggpk!.Root["Bundles2"]!;
            var idxNode = (LibGGPK3.Records.FileRecord)b2Dir["_.index.bin"]!;
            idxNode.Write(data);
        }
    }

    // ── Dispose ────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ggpk?.Dispose();
        _index?.Dispose();
        _mappedIndex?.Dispose();
        _ggpk = null;
        _index = null;
        _mappedIndex = null;

        if (_registeredOpen && Interlocked.Decrement(ref _openInstanceCount) == 0)
            LocksChanged?.Invoke(false);
    }
}
