using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
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
    private static long _openSequence;
    private bool _isDirectIndex;
    private bool _registeredOpen;
    private bool _disposed;
    private bool _pinWrites;
    private BundledGGPK? _ggpk;
    private LibBundle3.Index? _index;
    private MemoryMappedFile? _mappedIndex;
    private string? _indexPath;
    private string? _gameDataPath;

    private GameDataAccess() { }

    /// <summary>Raised when the process starts or stops holding game data handles.</summary>
    public static event Action<bool>? LocksChanged;

    public static bool HasOpenLocks => Volatile.Read(ref _openInstanceCount) > 0;

    /// <summary>
    /// Builds the guard to hand to <see cref="MemoryReclaimer.Reclaim(Func{bool})"/> after releasing
    /// game data: stop reclaiming while another module still holds data open, so it does not get a
    /// heavy blocking collection in the middle of its work.
    /// </summary>
    /// <remarks>
    /// A data source opened after this call does not count as somebody else working — that is the
    /// caller reopening (switching files), and the memory that has to go belongs to the file that was
    /// just released. The new index is strongly referenced, so the collection cannot touch it.
    /// </remarks>
    public static Func<bool> CreateAbortCheck()
    {
        var openedAtRelease = Volatile.Read(ref _openSequence);
        return () => HasOpenLocks && Volatile.Read(ref _openSequence) == openedAtRelease;
    }

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
    /// <param name="bundleDirectory">
    /// Optional override for where bundle files live. Defaults to the index's own directory.
    /// Needed when the index is a snapshot copy (e.g. a backup) whose bundles remain in the game's Bundles2 directory.
    /// </param>
    public static GameDataAccess OpenReadOnlyMapped(string path, string? bundleDirectory = null)
        => OpenCore(path, readOnly: true, memoryMapBundles2Index: true, bundleDirectory);

    private static GameDataAccess OpenCore(string path, bool readOnly, bool memoryMapBundles2Index, string? bundleDirectory = null)
    {
        var resolved = ResolvePath(path);
        FileLogger.App.Info($"Opening game data: {resolved} ({(readOnly ? "read-only" : "read-write")})");
        var gd = new GameDataAccess();
        gd._pinWrites = !readOnly;

        try
        {
            if (resolved.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase))
            {
                gd.OpenGgpk(resolved);
                gd._gameDataPath = resolved;
                return gd.MarkOpened();
            }

            gd.OpenBundles2Index(resolved, readOnly, memoryMapBundles2Index, bundleDirectory);
            return gd.MarkOpened();
        }
        catch (Exception ex)
        {
            FileLogger.App.Error($"Failed to open game data: {resolved}", ex);
            throw;
        }
    }

    /// <summary>
    /// Resolves a user-supplied path to the game data file itself: an explicit Content.ggpk or
    /// _.index.bin, or a directory holding one of them. Returns the fully qualified file path.
    /// </summary>
    /// <remarks>
    /// Kept as the single home for this rule so callers that only need the path (and not a handle)
    /// cannot drift away from what <see cref="Open"/> actually accepts.
    /// </remarks>
    /// <exception cref="ArgumentException">The path is null or blank.</exception>
    /// <exception cref="FileNotFoundException">Nothing usable is at the path.</exception>
    public static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Game data path is empty.", nameof(path));

        var full = System.IO.Path.GetFullPath(path);

        if (System.IO.File.Exists(full)
            && (full.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)
                || full.EndsWith(".index.bin", StringComparison.OrdinalIgnoreCase)))
            return full;

        if (System.IO.Directory.Exists(full))
        {
            var ggpk = System.IO.Path.Combine(full, "Content.ggpk");
            if (System.IO.File.Exists(ggpk))
                return ggpk;

            var idx = System.IO.Path.Combine(full, "Bundles2", "_.index.bin");
            if (System.IO.File.Exists(idx))
                return idx;
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
        // Every modification made by the toolbox must land in our own single custom bundle
        // (LibGGPK3/0.bundle.bin), never split into LibGGPK3/1, 2, 3, ... when it grows past MaxBundleSize.
        if (_pinWrites)
            PinAllWritesToSingleBundle();

        _registeredOpen = true;
        Interlocked.Increment(ref _openSequence);
        if (Interlocked.Increment(ref _openInstanceCount) == 1)
            LocksChanged?.Invoke(true);
        FileLogger.App.Info($"Game data opened: {_gameDataPath} ({(_isDirectIndex ? "Bundles2 index" : "GGPK")}, open instances: {_openInstanceCount})");
        return this;
    }

    private void OpenBundles2Index(string indexPath, bool readOnly, bool memoryMapIndex, string? bundleDirectory = null)
    {
        var bundleDir = bundleDirectory ?? System.IO.Path.GetDirectoryName(indexPath)!;
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

    /// <summary>Whether a file path exists in the index.</summary>
    public bool FileExists(string path) => Index.TryGetFile(path, out _);

    /// <summary>
    /// Copy an existing indexed file to <paramref name="destPath"/> with an independent content,
    /// so that editing the copy affects neither the original nor the other files referring to it.
    /// Call <see cref="Save"/> (or use this method, which saves automatically) to persist.
    /// </summary>
    /// <exception cref="System.IO.FileNotFoundException"><paramref name="sourcePath"/> is not found</exception>
    /// <exception cref="InvalidOperationException"><paramref name="destPath"/> already exists</exception>
    public LibBundle3.Records.FileRecord CopyFileAs(string sourcePath, string destPath)
    {
        EnsureGameStoppedForMutation();
        FileLogger.App.Info($"CopyFileAs: {sourcePath} -> {destPath}");
        var file = Index.CopyFile(sourcePath, destPath);
        Save();
        return file;
    }

    /// <summary>
    /// Add a brand-new file at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> already exists</exception>
    public LibBundle3.Records.FileRecord AddFile(string path, byte[] content, bool saveIndex = true)
    {
        if (saveIndex)
            EnsureGameStoppedForMutation();
        FileLogger.App.Info($"AddFile: {path} ({content.Length} bytes)");
        return Index.AddFile(path, content, saveIndex);
    }

    /// <summary>Deletes empty custom Bundle records and their physical files.</summary>
    public int CleanupOrphanCustomBundles(bool saveIndex = true)
    {
        if (saveIndex)
            EnsureGameStoppedForMutation();
        return Index.CleanupOrphanCustomBundles(saveIndex);
    }

    /// <summary>Removes the files a patch created under its own prefix, plus any bundle left empty.</summary>
    /// <param name="bundlePrefix">The patch's bundle name (without the PATCHED/ directory).</param>
    /// <param name="ownedPaths">Paths the patch itself created; base-game files are never removed.</param>
    public int PurgePatchBundles(string bundlePrefix, IReadOnlySet<string> ownedPaths, bool saveIndex = true)
    {
        if (saveIndex)
            EnsureGameStoppedForMutation();
        return Index.PurgeCustomBundles($"PATCHED/{bundlePrefix}_", ownedPaths, saveIndex);
    }

    // ── Save ───────────────────────────────────────────

    /// <summary>Persist all changes.</summary>
    public void Save()
    {
        EnsureGameStoppedForMutation("saving");
        Index.Save();
    }

    /// <summary>
    /// When set, all writes go into this single custom bundle instead of being spread over several ones.
    /// See <see cref="LibBundle3.Index.PinnedWriteBundlePath"/>.
    /// </summary>
    public string? PinnedWriteBundlePath
    {
        get => Index.PinnedWriteBundlePath;
        set => Index.PinnedWriteBundlePath = value;
    }

    /// <summary>
    /// Pins every write to the single bundle <c>LibGGPK3/0.bundle.bin</c>, so that a mod stays one
    /// predictable file instead of being split into LibGGPK3/1, 2, 3, ... when it grows large.
    /// </summary>
    public void PinAllWritesToSingleBundle()
        => Index.PinnedWriteBundlePath = LibBundle3.Index.DefaultWriteBundlePath;

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

    /// <summary>
    /// Opens a forward-only reader over the raw index content: the Bundles2 <c>_.index.bin</c> on disk,
    /// or the same file stored as a record inside a GGPK. Nothing is buffered by this call.
    /// </summary>
    /// <remarks>
    /// For callers that only read the index through — hashing it or copying it out. A real index is a
    /// few hundred MB, so they should not go via <see cref="ReadIndexBytes"/> and materialise it.
    /// <para>
    /// A GGPK cannot lend out its index node as a <see cref="LibGGPK3.GGFileStream"/>, because the open
    /// container already owns the one stream a <c>FileRecord</c> allows. The reader therefore reads the
    /// record in blocks, clamped to its own length.
    /// </para>
    /// </remarks>
    public IndexReader OpenIndexReader()
    {
        if (_isDirectIndex)
            return new IndexReader(new System.IO.FileStream(_indexPath!, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite), null);

        var b2Dir = (LibGGPK3.Records.DirectoryRecord)_ggpk!.Root["Bundles2"]!;
        var idxNode = (LibGGPK3.Records.FileRecord)b2Dir["_.index.bin"]!;
        return new IndexReader(null, idxNode);
    }

    /// <summary>
    /// Forward-only reader over the raw index content, so a few hundred MB can be hashed or copied
    /// without ever being held in memory as one array.
    /// </summary>
    public sealed class IndexReader : IDisposable
    {
        private readonly System.IO.FileStream? _file;
        private readonly LibGGPK3.Records.FileRecord? _record;
        private long _position;

        internal IndexReader(System.IO.FileStream? file, LibGGPK3.Records.FileRecord? record)
        {
            _file = file;
            _record = record;
            Length = file?.Length ?? record!.DataLength;
        }

        /// <summary>Size of the raw index content in bytes.</summary>
        public long Length { get; }

        /// <summary>Bytes handed out so far.</summary>
        public long Position => _position;

        /// <summary>Reads the next block, or returns 0 once the whole content has been read.</summary>
        /// <exception cref="EndOfStreamException">The content ended before <see cref="Length"/> bytes.</exception>
        public int Read(Span<byte> destination)
        {
            var remaining = Length - _position;
            if (remaining <= 0 || destination.IsEmpty)
                return 0;

            var wanted = (int)Math.Min(destination.Length, remaining);
            if (_file is not null)
            {
                var read = _file.Read(destination[..wanted]);
                if (read <= 0)
                    throw new EndOfStreamException("The index file ended earlier than its length reports.");
                _position += read;
                return read;
            }

            _record!.Read(destination[..wanted], (int)_position);
            _position += wanted;
            return wanted;
        }

        public void Dispose() => _file?.Dispose();
    }

    /// <summary>Write raw index bytes (for restore/replace).</summary>
    public void WriteIndexBytes(byte[] data)
    {
        EnsureGameStoppedForMutation("replacing game data");
        FileLogger.App.Info($"Index replaced externally: {_gameDataPath} ({data.Length} bytes)");
        if (_isDirectIndex)
        {
            DetachDirectIndex();
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
            ReopenDirectIndex();
        }
        else
        {
            WriteIndexIntoGgpk(data);
        }
    }

    /// <summary>
    /// Write raw index bytes taken from <paramref name="sourcePath"/> (for restore).
    /// </summary>
    /// <remarks>
    /// A Bundles2 index is copied file to file, so a hundreds-of-MB index is never materialised as a
    /// byte array. A GGPK stores its index as a record inside the container, so that path still reads
    /// the file back in.
    /// </remarks>
    public void WriteIndexBytesFrom(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        EnsureGameStoppedForMutation("replacing game data");
        var length = new System.IO.FileInfo(sourcePath).Length;
        FileLogger.App.Info($"Index replaced externally: {_gameDataPath} (from {sourcePath}, {length} bytes)");
        if (_isDirectIndex)
        {
            DetachDirectIndex();
            var tempPath = _indexPath! + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                System.IO.File.Copy(sourcePath, tempPath, overwrite: true);
                System.IO.File.Move(tempPath, _indexPath!, true);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
            ReopenDirectIndex();
        }
        else
        {
            WriteIndexIntoGgpk(System.IO.File.ReadAllBytes(sourcePath));
        }
    }

    /// <summary>Closes the handles on a direct Bundles2 index so the file can be replaced.</summary>
    private void DetachDirectIndex()
    {
        _index?.Dispose();
        _mappedIndex?.Dispose();
        _index = null;
        _mappedIndex = null;
    }

    private void ReopenDirectIndex()
    {
        var bundleDir = System.IO.Path.GetDirectoryName(_indexPath!)!;
        // Match Open's tolerant mode: newer client indexes can contain
        // unresolved paths that are unrelated to the files we access.
        _index = new LibBundle3.Index(_indexPath!, parsePaths: false,
            bundleFactory: new DriveBundleFactory(bundleDir));
        _index.ParsePaths();
        if (_pinWrites)
            PinAllWritesToSingleBundle();
    }

    private void WriteIndexIntoGgpk(byte[] data)
    {
        var b2Dir = (LibGGPK3.Records.DirectoryRecord)_ggpk!.Root["Bundles2"]!;
        var idxNode = (LibGGPK3.Records.FileRecord)b2Dir["_.index.bin"]!;
        idxNode.Write(data);
    }

    private static void EnsureGameStoppedForMutation(string action = "modifying game data")
    {
        if (PoeDetector.Default.IsPoeRunning())
            throw new InvalidOperationException(
                $"Path of Exile is running. Close the game before {action}.");
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
        {
            LocksChanged?.Invoke(false);
            FileLogger.App.Info("Game data locks released (no open instances).");
        }
    }
}
