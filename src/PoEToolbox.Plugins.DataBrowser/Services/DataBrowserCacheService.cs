using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LibBundle3.Nodes;
using LibBundle3.Records;
using PoEToolbox.Plugins.DataBrowser.Models;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.DataBrowser.Services;

public sealed record DataSourceSignature(string SourcePath, long Length, long LastWriteUtcTicks);

/// <summary>
/// Persistent tree cache for the data browser. Cache files are source-specific
/// and are discarded logically when the backing GGPK/index changes.
/// </summary>
public static class DataBrowserCacheService
{
    private const int CacheVersion = 2;
    private const string TreeMagic = "POETOOLBOX_TREE_CACHE";

    private static string CacheDirectory => Path.Combine(ConfigService.DataDirectory, "databrowser-cache");

    public static bool TryCreateSignature(string selectedPath, out DataSourceSignature signature)
    {
        signature = null!;
        var sourcePath = ResolveSourcePath(selectedPath);
        if (sourcePath is null) return false;

        var info = new FileInfo(sourcePath);
        if (!info.Exists) return false;

        signature = new DataSourceSignature(
            Path.GetFullPath(sourcePath),
            info.Length,
            info.LastWriteTimeUtc.Ticks);
        return true;
    }

    public static CachedTreeIndex? TryLoadTreeCache(DataSourceSignature signature, int expectedFileCount)
    {
        try
        {
            var cachePath = GetCachePath("tree", signature);
            if (!File.Exists(cachePath)) return null;

            using var stream = File.OpenRead(cachePath);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadString() != TreeMagic || reader.ReadInt32() != CacheVersion)
                return null;
            if (!ReadSignature(reader, signature)) return null;

            var fileCount = reader.ReadInt32();
            var directoryCount = reader.ReadInt32();
            if (fileCount != expectedFileCount || directoryCount < 1)
                return null;

            var directories = new CachedDirectory[directoryCount];
            var cachedFileCount = 0;
            for (var directoryId = 0; directoryId < directoryCount; directoryId++)
            {
                var directoryPath = NormalizeDirectoryPath(reader.ReadString());
                var entryCount = reader.ReadInt32();
                if (entryCount < 0) return null;

                var entries = new CachedTreeEntry[entryCount];
                for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)
                {
                    switch (reader.ReadByte())
                    {
                        case 1:
                            var childDirectoryId = reader.ReadInt32();
                            if ((uint)childDirectoryId >= (uint)directoryCount)
                                return null;
                            entries[entryIndex] = CachedTreeEntry.Directory(childDirectoryId);
                            break;
                        case 0:
                            entries[entryIndex] = CachedTreeEntry.File(reader.ReadUInt64());
                            ++cachedFileCount;
                            break;
                        default:
                            return null;
                    }
                }

                directories[directoryId] = new CachedDirectory(directoryPath, entries);
            }

            if (cachedFileCount != fileCount || directories[0].Path.Length != 0)
                return null;

            return new CachedTreeIndex(fileCount, directories);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveTreeCache(DataSourceSignature signature, IDirectoryNode root)
    {
        string? tempPath = null;
        try
        {
            var cache = CachedTreeIndex.Build(root);
            var cachePath = GetCachePath("tree", signature);
            tempPath = cachePath + ".tmp." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(CacheDirectory);

            using (var stream = File.Create(tempPath))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(TreeMagic);
                writer.Write(CacheVersion);
                WriteSignature(writer, signature);
                writer.Write(cache.FileCount);
                writer.Write(cache.DirectoryCount);

                foreach (var directory in cache.Directories)
                {
                    writer.Write(directory.Path);
                    writer.Write(directory.Entries.Length);
                    foreach (var entry in directory.Entries)
                    {
                        writer.Write(entry.IsDirectory ? (byte)1 : (byte)0);
                        if (entry.IsDirectory)
                            writer.Write(entry.DirectoryId);
                        else
                            writer.Write(entry.FileHash);
                    }
                }
            }

            File.Move(tempPath, cachePath, true);
        }
        catch
        {
            // Cache failure must never affect browsing.
        }
        finally
        {
            try
            {
                if (tempPath is not null && File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Cache cleanup failure must never affect browsing.
            }
        }
    }

    private static string? ResolveSourcePath(string selectedPath)
    {
        var fullPath = Path.GetFullPath(selectedPath);
        if (File.Exists(fullPath)) return fullPath;
        if (!Directory.Exists(fullPath)) return null;

        var ggpk = Path.Combine(fullPath, "Content.ggpk");
        if (File.Exists(ggpk)) return ggpk;
        var index = Path.Combine(fullPath, "Bundles2", "_.index.bin");
        return File.Exists(index) ? index : null;
    }

    private static string GetCachePath(string kind, DataSourceSignature signature)
    {
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(signature.SourcePath.ToUpperInvariant()))).ToLowerInvariant();
        return Path.Combine(CacheDirectory, $"{kind}-{key}.bin");
    }

    private static void WriteSignature(BinaryWriter writer, DataSourceSignature signature)
    {
        writer.Write(signature.SourcePath);
        writer.Write(signature.Length);
        writer.Write(signature.LastWriteUtcTicks);
    }

    private static bool ReadSignature(BinaryReader reader, DataSourceSignature expected)
    {
        return string.Equals(reader.ReadString(), expected.SourcePath, StringComparison.OrdinalIgnoreCase)
            && reader.ReadInt64() == expected.Length
            && reader.ReadInt64() == expected.LastWriteUtcTicks;
    }

    private static string NormalizeDirectoryPath(string path)
        => path.Trim('/');

    private static string CombinePath(string directory, string name)
        => string.IsNullOrEmpty(directory) ? name : $"{directory}/{name}";

    public sealed class CachedTreeIndex
    {
        private readonly CachedDirectory[] _directories;
        private readonly Dictionary<string, int> _directoryIds;

        internal CachedTreeIndex(int fileCount, CachedDirectory[] directories)
        {
            FileCount = fileCount;
            _directories = directories;
            _directoryIds = new Dictionary<string, int>(directories.Length, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < directories.Length; i++)
            {
                if (!_directoryIds.TryAdd(directories[i].Path, i))
                    throw new InvalidDataException("Duplicate directory in tree cache");
            }
        }

        public int FileCount { get; }
        public int DirectoryCount => _directories.Length;
        internal IReadOnlyList<CachedDirectory> Directories => _directories;

        public bool TryBuildRoot(IReadOnlyDictionary<ulong, FileRecord> files, out ObservableCollection<TreeItemViewModel> items)
            => TryBuildChildren("", files, out items);

        public bool LoadChildren(TreeItemViewModel parent, IReadOnlyDictionary<ulong, FileRecord> files)
        {
            if (parent.IsLoaded) return true;
            if (!TryBuildChildren(parent.FullPath, files, out var children)) return false;
            parent.Children = children;
            parent.IsLoaded = true;
            return true;
        }

        public List<FileRecord> GetFilesUnder(string directoryPath, IReadOnlyDictionary<ulong, FileRecord> files)
        {
            var result = new List<FileRecord>();
            if (!_directoryIds.TryGetValue(NormalizeDirectoryPath(directoryPath), out var rootId))
                return result;

            var pending = new Stack<int>();
            pending.Push(rootId);

            while (pending.Count > 0)
            {
                var entries = _directories[pending.Pop()].Entries;
                foreach (var entry in entries)
                {
                    if (entry.IsDirectory)
                        pending.Push(entry.DirectoryId);
                    else if (files.TryGetValue(entry.FileHash, out var file))
                        result.Add(file);
                }
            }

            return result;
        }

        private bool TryBuildChildren(
            string directoryPath,
            IReadOnlyDictionary<ulong, FileRecord> files,
            out ObservableCollection<TreeItemViewModel> items)
        {
            items = [];
            if (!_directoryIds.TryGetValue(NormalizeDirectoryPath(directoryPath), out var directoryId))
                return false;

            var entries = _directories[directoryId].Entries;
            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    var childDirectory = _directories[entry.DirectoryId];
                    var hasChildren = childDirectory.Entries.Length > 0;
                    items.Add(new TreeItemViewModel
                    {
                        Name = GetDirectoryName(childDirectory.Path),
                        FullPath = childDirectory.Path,
                        IsDirectory = true,
                        Children = hasChildren
                            ? new ObservableCollection<TreeItemViewModel> { TreeItemViewModel.Dummy }
                            : [],
                        IsLoaded = !hasChildren,
                    });
                }
                else
                {
                    if (!files.TryGetValue(entry.FileHash, out var file))
                    {
                        items = [];
                        return false;
                    }

                    items.Add(new TreeItemViewModel
                    {
                        Name = GetFileName(file, entry.FileHash),
                        FullPath = file.Path ?? entry.FileHash.ToString("X16"),
                        IsDirectory = false,
                        Size = file.Size,
                        FileRecord = file,
                        IsLoaded = true,
                    });
                }
            }

            return true;
        }

        internal static CachedTreeIndex Build(IDirectoryNode root)
        {
            var directories = new List<BuildDirectory>();
            var directoryIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AddDirectory(root, "");
            var fileCount = 0;

            for (var directoryId = 0; directoryId < directories.Count; directoryId++)
            {
                var directory = directories[directoryId];
                var entries = new List<CachedTreeEntry>(directory.Node.Children.Count);
                foreach (var child in directory.Node.Children)
                {
                    if (child is IDirectoryNode childDirectory)
                    {
                        var childPath = CombinePath(directory.Path, childDirectory.Name).Trim('/');
                        if (!directoryIds.TryGetValue(childPath, out var childId))
                            childId = AddDirectory(childDirectory, childPath);
                        entries.Add(CachedTreeEntry.Directory(childId));
                    }
                    else if (child is IFileNode fileNode)
                    {
                        entries.Add(CachedTreeEntry.File(fileNode.Record.PathHash));
                        ++fileCount;
                    }
                }

                directory.Entries = entries.ToArray();
            }

            var cachedDirectories = directories
                .Select(directory => new CachedDirectory(directory.Path, directory.Entries))
                .ToArray();
            return new CachedTreeIndex(fileCount, cachedDirectories);

            int AddDirectory(IDirectoryNode node, string path)
            {
                var id = directories.Count;
                directoryIds.Add(path, id);
                directories.Add(new BuildDirectory(node, path));
                return id;
            }
        }

        private static string GetDirectoryName(string path)
        {
            var separator = path.LastIndexOf('/');
            return separator < 0 ? path : path[(separator + 1)..];
        }

        private static string GetFileName(FileRecord file, ulong hash)
            => file.Path is null ? hash.ToString("X16") : Path.GetFileName(file.Path);

        private sealed class BuildDirectory(IDirectoryNode node, string path)
        {
            public IDirectoryNode Node { get; } = node;
            public string Path { get; } = path;
            public CachedTreeEntry[] Entries { get; set; } = [];
        }
    }

    internal sealed class CachedDirectory(string path, CachedTreeEntry[] entries)
    {
        public string Path { get; } = path;
        public CachedTreeEntry[] Entries { get; } = entries;
    }

    internal readonly struct CachedTreeEntry
    {
        private CachedTreeEntry(bool isDirectory, int directoryId, ulong fileHash)
        {
            IsDirectory = isDirectory;
            DirectoryId = directoryId;
            FileHash = fileHash;
        }

        public bool IsDirectory { get; }
        public int DirectoryId { get; }
        public ulong FileHash { get; }

        public static CachedTreeEntry Directory(int directoryId) => new(true, directoryId, 0);
        public static CachedTreeEntry File(ulong hash) => new(false, -1, hash);
    }
}
