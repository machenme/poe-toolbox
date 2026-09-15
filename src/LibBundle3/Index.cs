using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using LibBundle3.Nodes;
using LibBundle3.Records;

using SystemExtensions;
using SystemExtensions.Collections;
using SystemExtensions.Spans;
using SystemExtensions.Streams;

[module: SkipLocalsInit]

namespace LibBundle3;

/// <summary>
/// Class to handle the _.index.bin file.
/// </summary>
public class Index : IDisposable {
	// PoEToolbox 修订：原为 readonly；原因同 baseBundle（初始化提取到 Initialize()）。
	protected internal IBundleFactory bundleFactory;
	/// <summary>
	/// <see cref="Bundle"/> instance of "_.index.bin"
	/// </summary>
	// PoEToolbox 修订：原为 readonly；初始化提取到 Initialize() 以便字符串构造函数在
	// 流初始化失败时关闭文件句柄，因此这两个字段需要在构造函数外赋值一次。
	protected Bundle baseBundle;
	/// <summary>
	/// Data for <see cref="ParsePaths"/>
	/// </summary>
	protected byte[] directoryBundleData;

	protected BundleRecord[] _Bundles;
	protected internal DirectoryRecord[] _Directories;
	protected Dictionary<ulong, FileRecord> _Files;

	/// <summary>
	/// Bundles ceated by this library for writing modfied files.
	/// </summary>
	private readonly List<BundleRecord> CustomBundles = [];

	internal Bundle? _BundleToWrite;
	internal MemoryStream? _BundleStreamToWrite;
	internal readonly WeakReference<MemoryStream> WR_BundleStreamToWrite = new(null!);

	public sealed class MutationSnapshot {
		internal readonly BundleRecord[] Bundles;
		internal readonly Dictionary<ulong, (BundleRecord Bundle, int Offset, int Size)> Files;
		internal readonly int BaseUncompressedSize;
		internal readonly DirectoryRecord[] Directories;
		internal readonly byte[] DirectoryBundleData;

		internal MutationSnapshot(BundleRecord[] bundles,
			Dictionary<ulong, (BundleRecord Bundle, int Offset, int Size)> files,
			int baseUncompressedSize,
			DirectoryRecord[] directories,
			byte[] directoryBundleData) {
			Bundles = bundles;
			Files = files;
			BaseUncompressedSize = baseUncompressedSize;
			Directories = directories;
			DirectoryBundleData = directoryBundleData;
		}
	}

	public virtual ReadOnlyMemory<BundleRecord> Bundles => _Bundles;

	/// <summary>Captures file locations and bundle records before a multi-file mutation.</summary>
	public virtual MutationSnapshot CaptureMutationSnapshot() {
		lock (this) {
			EnsureNotDisposed();
			return new MutationSnapshot(
				_Bundles.ToArray(),
				_Files.ToDictionary(p => p.Key, p => (p.Value.BundleRecord, p.Value.Offset, p.Value.Size)),
				baseBundle.UncompressedSize,
				_Directories.ToArray(),
				directoryBundleData.ToArray());
		}
	}

	/// <summary>Restores a mutation snapshot and deletes all Bundles created after it.</summary>
	public virtual void RollbackMutation(MutationSnapshot snapshot) {
		ArgumentNullException.ThrowIfNull(snapshot);
		lock (this) {
			EnsureNotDisposed();
			// The index Bundle may itself be a buffered GGPK stream. Abandon it before
			// disposal so a failed transaction cannot flush a partially serialized index.
			baseBundle.DiscardBufferedChanges();
			if (_BundleToWrite is not null) {
				_BundleToWrite.Abort();
				_BundleToWrite = null;
			}
			_BundleStreamToWrite?.Dispose();
			_BundleStreamToWrite = null;

			foreach (var pair in snapshot.Files) {
				if (_Files.TryGetValue(pair.Key, out var file))
					file.Redirect(pair.Value.Bundle, pair.Value.Offset, pair.Value.Size);
			}
			foreach (var hash in _Files.Keys.Where(h => !snapshot.Files.ContainsKey(h)).ToArray()) {
				var file = _Files[hash];
				file.BundleRecord._Files.Remove(file);
				_Files.Remove(hash);
			}

			var originalBundles = snapshot.Bundles.ToHashSet();
			foreach (var bundle in _Bundles.Where(b => !originalBundles.Contains(b)).ToArray())
				bundleFactory.DeleteBundle(bundle.Path);
			_Bundles = snapshot.Bundles.ToArray();
			for (var i = 0; i < _Bundles.Length; i++)
				_Bundles[i].BundleIndex = i;
			_Directories = snapshot.Directories.ToArray();
			directoryBundleData = snapshot.DirectoryBundleData.ToArray();
			CustomBundles.RemoveAll(b => !originalBundles.Contains(b));
			baseBundle.UncompressedSize = snapshot.BaseUncompressedSize;
			_Root = null;
		}
	}

	/// <summary>Removes custom bundle records that no longer contain indexed files.</summary>
	/// <remarks>Call after reverting a patch to delete physical orphan bundle files.</remarks>
	public virtual int CleanupOrphanCustomBundles(bool saveIndex = true) {
		lock (this) {
			EnsureNotDisposed();
			var count = CustomBundles.Count(b => b._Files.Count == 0);
			if (count != 0 && saveIndex)
				Save();
			return count;
		}
	}

	/// <summary>Removes the files a dedicated PATCHED prefix owns, leaving everything else alone.</summary>
	/// <param name="pathPrefix">Bundle path prefix identifying the patch family.</param>
	/// <param name="ownedPaths">Full paths (compared case-insensitively) that the patch itself created.
	/// <para>
	/// A file under the prefix whose path is not listed here is kept. That guard matters because
	/// editing a base-game file redirects its record into the patch bundle: reverting a patch writes a
	/// restored copy of e.g. <c>data/balance/miscanimated.datc64</c> into the patch bundle, so removing
	/// that record would drop the table from the index entirely instead of restoring it.
	/// </para></param>
	public virtual int PurgeCustomBundles(string pathPrefix, IReadOnlySet<string> ownedPaths, bool saveIndex = true)
		=> PurgeCustomBundles([pathPrefix], ownedPaths, saveIndex);

	/// <inheritdoc cref="PurgeCustomBundles(string, IReadOnlySet{string}, bool)"/>
	/// <param name="pathPrefixes">Bundle path prefixes identifying the patch family; a bundle matching any of them is scanned.</param>
	public virtual int PurgeCustomBundles(IEnumerable<string> pathPrefixes, IReadOnlySet<string> ownedPaths, bool saveIndex = true) {
		ArgumentNullException.ThrowIfNull(ownedPaths);
		ArgumentNullException.ThrowIfNull(pathPrefixes);
		var prefixes = pathPrefixes as IReadOnlyList<string> ?? pathPrefixes.ToArray();
		if (prefixes.Count == 0)
			throw new ArgumentException("At least one prefix is required.", nameof(pathPrefixes));
		foreach (var prefix in prefixes)
			ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
		lock (this) {
			EnsureNotDisposed();
			var bundles = CustomBundles
				.Where(b => b._Path is { } p && prefixes.Any(pre => p.StartsWith(pre, StringComparison.OrdinalIgnoreCase)))
				.ToArray();
			var removed = 0;
			var removedPaths = new HashSet<string>(StringComparer.Ordinal);
			foreach (var bundle in bundles)
				foreach (var file in bundle._Files.ToArray()) {
					if (file.Path is null || !ownedPaths.Contains(file.Path))
						continue;
					bundle._Files.Remove(file);
					_Files.Remove(file.PathHash);
					removedPaths.Add(file.Path);
					++removed;
				}
			if (removed == 0)
				return 0;
			// ⚠ 目录表绝对不能整表重建：客户端按「目录记录 + 路径条目」构建文件系统视图，
			// 重建（坍缩成单条根记录）会让客户端无法按路径解析任何文件，启动即崩。
			// 2026-09-13 事故：purge 后目录记录 94924 -> 1，游戏打不开，只能从基线索引恢复。
			// 只做外科手术式收缩：精确摘除被删文件的路径条目，其余字节与目录记录原样保留。
			_Root = null;
			RemoveDirectoryEntries(removedPaths);
			if (saveIndex)
				Save();
			return removed;
		}
	}

	/// <summary>
	/// Surgically removes the raw path entries of <paramref name="removedPaths"/> from the directory
	/// table: every other byte, directory record and their order are preserved untouched, only the
	/// affected spans shrink and records whose whole span was removed are dropped (the first record
	/// is always kept — its PathHash marks the name-hash algorithm).
	/// </summary>
	/// <remarks>The caller must have removed the matching file records already, and hold the lock.</remarks>
	private void RemoveDirectoryEntries(IReadOnlySet<string> removedPaths) {
		if (_Directories.Length == 0 || removedPaths.Count == 0)
			return;
		var targets = new HashSet<string>(removedPaths, StringComparer.OrdinalIgnoreCase);
		ReadOnlyMemory<byte> directory;
		using (var bundle = new Bundle(new MemoryStream(directoryBundleData), false))
			directory = bundle.ReadWithoutCache();
		var bytes = directory.Span;

		// Pass 1: locate the byte ranges to remove, per record. Entry layout per the client's
		// parser: an int32 — 0 toggles prefix/base mode, anything else references a prefix and
		// is followed by a null-terminated path suffix.
		var removeRanges = new List<(int Start, int End)>();
		for (var i = 0; i < _Directories.Length; i++) {
			var record = _Directories[i];
			if (record.Offset < 0 || record.Size < 0 || record.Offset > directory.Length - record.Size)
				throw new InvalidDataException("Directory table contains an invalid range; refusing to edit.");

			var end = record.Offset + record.Size;
			var cursor = record.Offset;
			var prefixes = new List<byte[]>();
			var baseMode = false;
			var prefixUsed = false;
			var fileEntries = 0;
			var removedEntries = new List<(int Start, int End)>();
			while (cursor <= end - sizeof(int)) {
				var entryStart = cursor;
				var prefixRef = BitConverter.ToInt32(bytes[cursor..]);
				cursor += sizeof(int);
				if (prefixRef == 0) {
					baseMode = !baseMode;
					if (baseMode)
						prefixes.Clear();
					continue;
				}

				var terminator = bytes[cursor..end].IndexOf((byte)0);
				if (terminator < 0)
					throw new InvalidDataException("Directory table contains an unterminated path; refusing to edit.");
				var suffix = bytes.Slice(cursor, terminator).ToArray();
				cursor += terminator + 1;
				var prefixIndex = prefixRef - 1;
				byte[] path;
				if (prefixIndex >= 0 && prefixIndex < prefixes.Count) {
					prefixUsed = true;
					path = GC.AllocateUninitializedArray<byte>(prefixes[prefixIndex].Length + suffix.Length);
					prefixes[prefixIndex].CopyTo(path, 0);
					suffix.CopyTo(path, prefixes[prefixIndex].Length);
				} else
					path = suffix;
				// Prefix entries are shared definitions — never remove them, only file entries.
				if (baseMode)
					prefixes.Add(path);
				else {
					++fileEntries;
					if (targets.Contains(Encoding.UTF8.GetString(path)))
						removedEntries.Add((entryStart, cursor));
				}
			}
			if (removedEntries.Count == 0)
				continue;
			// When nothing survives in the record — no prefix definitions or references, and
			// every file entry removed — the remaining mode toggles are a no-op husk. Remove
			// the whole span so the record can be dropped entirely (the exact inverse of how
			// AddFile appends a record + entry), instead of leaving an empty shell behind.
			if (!prefixUsed && removedEntries.Count == fileEntries)
				removeRanges.Add((record.Offset, end));
			else
				removeRanges.AddRange(removedEntries);
		}
		if (removeRanges.Count == 0)
			return;

		int RemovedBefore(int position) {
			var count = 0;
			foreach (var (start, end) in removeRanges) {
				if (end > position)
					break;
				count += end - start;
			}
			return count;
		}
		int RemovedWithin(int start, int end) {
			var count = 0;
			foreach (var (rangeStart, rangeEnd) in removeRanges)
				if (rangeStart >= start && rangeEnd <= end)
					count += rangeEnd - rangeStart;
			return count;
		}

		// Copy the blob without the removed ranges; every kept byte keeps its relative order.
		var kept = new MemoryStream(bytes.Length);
		var copied = 0;
		foreach (var (start, end) in removeRanges) {
			kept.Write(bytes[copied..start]);
			copied = end;
		}
		kept.Write(bytes[copied..]);

		var dirs = new List<DirectoryRecord>(_Directories.Length);
		for (var i = 0; i < _Directories.Length; i++) {
			var r = _Directories[i];
			var size = r.Size - RemovedWithin(r.Offset, r.Offset + r.Size);
			var recursive = r.RecursiveSize - RemovedWithin(r.Offset, r.Offset + r.RecursiveSize);
			// A record whose whole span was removed has no purpose left, and the client
			// mis-reads zero spans; drop it. The first record is exempt (algorithm marker).
			if (size == 0 && i != 0)
				continue;
			dirs.Add(new DirectoryRecord(r.PathHash, r.Offset - RemovedBefore(r.Offset), size, recursive));
		}

		using var bundleStream = new MemoryStream();
		using (var newBundle = new Bundle(bundleStream, (BundleRecord?)null))
			newBundle.Save(kept.ToArray());
		directoryBundleData = bundleStream.ToArray();
		_Directories = dirs.ToArray();
	}

	private static bool IsCustomBundlePath(string path)
		=> path.StartsWith(CUSTOM_BUNDLE_BASE_PATH, StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("PATCHED/", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Files with their <see cref="FileRecord.PathHash"/> as key.
	/// </summary>
	public virtual ReadOnlyDictionary<ulong, FileRecord> Files => new(_Files);

	/// <summary>
	/// Size to limit each bundle when writing, default to 200MiB
	/// </summary>
	public virtual int MaxBundleSize { get; set; } = 200 * 1024 * 1024;

	private string? _pinnedWriteBundlePath;
	/// <summary>
	/// When set, every file written by <see cref="FileRecord.Write(ReadOnlySpan{byte}, bool)"/> or
	/// <see cref="AddFile"/> goes into the custom bundle with this path
	/// (<see cref="BundleRecord.Path"/> without ".bundle.bin"), created on demand,
	/// instead of being spread over several custom bundles when <see cref="MaxBundleSize"/> is reached.
	/// </summary>
	/// <remarks>
	/// PoEToolbox 修订：允许任意安全子目录（如 <c>PATCHED/OilGrenade_v1</c>），不再强制 <c>LibGGPK3/</c> 前缀——
	/// 运行时 <see cref="GetBundleToWrite"/> 会把钉扎 bundle 动态加入 CustomBundles，前缀不是必要条件。
	/// The bundle is still flushed to disk when it exceeds <see cref="MaxBundleSize"/>, so memory usage
	/// stays bounded while all changes keep landing in the same file.
	/// <para>Use <see cref="DefaultWriteBundlePath"/> to pin all changes to a single, predictable bundle.</para>
	/// </remarks>
	/// <exception cref="ArgumentException">The value is not a safe relative subdirectory path</exception>
	public virtual string? PinnedWriteBundlePath {
		get => _pinnedWriteBundlePath;
		set {
			if (value is { Length: > 0 }) {
				if (value.StartsWith('/') || value.StartsWith('\\') || value.Contains(".."))
					throw new ArgumentException("Pinned write bundle path must be a relative path without '..': " + value, nameof(value));
				if (!value.Contains('/'))
					throw new ArgumentException("Pinned write bundle path must include a subdirectory (e.g. 'LibGGPK3/0' or 'PATCHED/<name>'): " + value, nameof(value));
				if (value.EndsWith(".bundle.bin", StringComparison.OrdinalIgnoreCase))
					throw new ArgumentException("Pinned write bundle path must not include the '.bundle.bin' extension: " + value, nameof(value));
			}
			_pinnedWriteBundlePath = value;
		}
	}

	protected DirectoryNode? _Root;

#pragma warning disable CS1734
    /// <summary>
    /// Root node of the tree (This will call <see cref="BuildTree(bool)"/> with default implementation when first calling).
    /// You can also implement your custom class and use <see cref="BuildTree(CreateDirectoryInstance, CreateFileInstance, bool)"/> instead of using this.
	/// <para>This will throw when any file have a null path (See <see cref="ParsePaths"/>)</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="ParsePaths"/> haven't been called</exception>
    /// <exception cref="NullReferenceException">Thrown when a file has null path. See <paramref name="ignoreNullPath"/> of <see cref="ParsePaths"/>.</exception>
#pragma warning restore CS1734
    public virtual DirectoryNode Root => _Root ??= BuildTree();

    public delegate IDirectoryNode CreateDirectoryInstance(string name, IDirectoryNode? parent);
	public delegate IFileNode CreateFileInstance(FileRecord record, IDirectoryNode parent);
	/// <summary>
	/// Default implementation of <see cref="BuildTree(CreateDirectoryInstance, CreateFileInstance, bool)"/> which <see cref="Root"/> use.
	/// </summary>
	/// <param name="ignoreNullPath">Whether to ignore files with <see cref="FileRecord.Path"/> as <see langword="null"/> instead of throwing.
	/// This happens when <see cref="ParsePaths"/> has not been called or failed to parse some paths (returns not 0).
	/// <para>The ignored files won't appear in the returned tree</para></param>
	public DirectoryNode BuildTree(bool ignoreNullPath = false) => (DirectoryNode)BuildTree(DirectoryNode.CreateInstance, FileNode.CreateInstance, ignoreNullPath);
	/// <summary>
	/// Build a tree to represent the file and directory structure in bundles
	/// </summary>
	/// <param name="createDirectory">Function to create a instance of <see cref="IDirectoryNode"/></param>
	/// <param name="createFile">Function to create a instance of <see cref="IFileNode"/></param>
	/// <param name="ignoreNullPath">Whether to ignore files with <see cref="FileRecord.Path"/> as <see langword="null"/> instead of throwing.
	/// This happens when <see cref="ParsePaths"/> has not been called or failed to parse some paths (returns not 0).
	/// <para>The ignored files won't appear in the returned tree</para></param>
	/// <returns>The root node of the built tree</returns>
	/// <remarks>
	/// You can implement your custom class and call this, or just use the default implementation by calling <see cref="Root"/>.
	/// </remarks>
	/// <exception cref="InvalidOperationException">Thrown when <see cref="ParsePaths"/> haven't been called.</exception>
	/// <exception cref="NullReferenceException">Thrown when a file has null path. See <paramref name="ignoreNullPath"/>.</exception>
	public virtual IDirectoryNode BuildTree(CreateDirectoryInstance createDirectory, CreateFileInstance createFile, bool ignoreNullPath = false) {
		EnsureNotDisposed();
		if (!ignoreNullPath && !pathsParsed)
			ThrowHelper.Throw<InvalidOperationException>("ParsePaths() must be called before building the tree");
		var root = createDirectory("", null);
		// Build in one pass. Sorting every file path first is expensive for large game indexes;
		// per-directory lookup keeps construction close to O(file count), then sorts only direct children.
		var directoryChildren = new Dictionary<IDirectoryNode, Dictionary<string, IDirectoryNode>>();
		var directories = new List<IDirectoryNode> { root };
		directoryChildren[root] = new(StringComparer.InvariantCultureIgnoreCase);

		foreach (var f in _Files.Values) {
			if (string.IsNullOrEmpty(f.Path)) {
				if (!ignoreNullPath)
					ThrowHelper.Throw<NullReferenceException>("A file has null or empty path, the Index may be broken");
				continue;
			}
			var splittedPath = SpanExtensions.Split(f.Path.AsSpan(), '/');
			var parent = root;
			if (splittedPath.MoveNext())
				while (true) {
					var name = splittedPath.Current;
					if (!splittedPath.MoveNext()) // Last one is the file name
						break;
					var directoryName = name.ToString();
					var children = directoryChildren[parent];
					if (!children.TryGetValue(directoryName, out var dr)) {
						dr = createDirectory(directoryName, parent);
						children.Add(directoryName, dr);
						parent.Children.Add(dr);
						directoryChildren.Add(dr, new(StringComparer.InvariantCultureIgnoreCase));
						directories.Add(dr);
					}
					parent = dr;
				}
			parent.Children.Add(createFile(f, parent));
		}

		foreach (var directory in directories)
			directory.Children.Sort((left, right) =>
			string.Compare(left.Name, right.Name, StringComparison.InvariantCultureIgnoreCase));

		return root;
	}

	/// <summary>
	/// Initialize with a _.index.bin file on disk. (For Steam/Epic version)
	/// </summary>
	/// <param name="filePath">Path to _.index.bin on disk</param>
	/// <param name="parsePaths">
	/// Whether to call <see cref="ParsePaths"/> automatically.
	/// <see langword="false"/> to speed up reading, but all <see cref="FileRecord.Path"/> in each of <see cref="Files"/> will be <see langword="null"/>,
	/// and <see cref="Root"/> and <see cref="BuildTree(CreateDirectoryInstance, CreateFileInstance, bool)"/> will be unable to use until you call <see cref="ParsePaths"/> manually.
	/// </param>
	/// <param name="bundleFactory">Factory to handle .bin files of <see cref="Bundle"/></param>
	/// <exception cref="FileNotFoundException" />
	public Index(string filePath, bool parsePaths = true, IBundleFactory? bundleFactory = null, bool readOnly = false) {
		filePath = Utils.ExpandPath(filePath);
		var stream = File.Open(filePath, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.ReadWrite);
		try {
			// 手动展开初始化而不是链式构造：流构造失败（索引损坏等）时必须关掉已打开的
			// 文件句柄，否则文件会一直被占用，直到 GC 终结器才释放（PoEToolbox 修订）。
			Initialize(stream, false, parsePaths, bundleFactory ?? new DriveBundleFactory(Path.GetDirectoryName(Path.GetFullPath(filePath))!));
		} catch {
			stream.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Initialize with <paramref name="stream"/>.
	/// </summary>
	/// <param name="stream">Stream of the _.index.bin file</param>
	/// <param name="leaveOpen">If false, close the <paramref name="stream"/> when this instance is disposed</param>
	/// <param name="parsePaths">
	/// Whether to call <see cref="ParsePaths"/> automatically.
	/// <see langword="false"/> to speed up reading, but all <see cref="FileRecord.Path"/> in each of <see cref="Files"/> will be <see langword="null"/>,
	/// and <see cref="Root"/> and <see cref="BuildTree(CreateDirectoryInstance, CreateFileInstance, bool)"/> will be unable to use until you call <see cref="ParsePaths"/> manually.
	/// </param>
	/// <param name="bundleFactory">Factory to handle .bin files of <see cref="Bundle"/></param>
	/// <remarks>
    /// For Steam/Epic version, use <see cref="Index(string, bool, IBundleFactory?, bool)"/> instead,
	/// or you must set <see cref="Environment.CurrentDirectory"/> to the directory where the _.index.bin file is before calling this constructor.
	/// </remarks>
	public unsafe Index(Stream stream, bool leaveOpen = false, bool parsePaths = true, IBundleFactory? bundleFactory = null) {
		ArgumentNullException.ThrowIfNull(stream);
		Initialize(stream, leaveOpen, parsePaths, bundleFactory);
	}

	private unsafe void Initialize(Stream stream, bool leaveOpen, bool parsePaths, IBundleFactory? bundleFactory) {
		this.bundleFactory = bundleFactory ?? new DriveBundleFactory(string.Empty);
		lock (this) {
			baseBundle = new(stream, leaveOpen);
			var data = baseBundle.ReadWithoutCache();
			fixed (byte* p = data) {
				var ptr = (int*)p;

				var bundleCount = *ptr++;
				_Bundles = new BundleRecord[bundleCount];
				for (var i = 0; i < bundleCount; i++) {
					var pathLength = *ptr++;
					var path = new string((sbyte*)ptr, 0, pathLength);
					ptr = (int*)((byte*)ptr + pathLength);
					var uncompressedSize = *ptr++;
					_Bundles[i] = new BundleRecord(path, uncompressedSize, this, i);
					if (IsCustomBundlePath(path))
						CustomBundles.Add(_Bundles[i]);
				}

				var fileCount = *ptr++;
				_Files = new(fileCount);
				for (var i = 0; i < fileCount; i++) {
					var nameHash = *(ulong*)ptr;
					ptr += 2;
					var bundle = _Bundles[*ptr++];
					var f = new FileRecord(nameHash, bundle, *ptr++, *ptr++);
					_Files.Add(nameHash, f);
					bundle._Files.Add(f);
				}

				var directoryCount = *ptr++;
				_Directories = new ReadOnlySpan<DirectoryRecord>(ptr, directoryCount).ToArray();
				ptr = (int*)((DirectoryRecord*)ptr + directoryCount);

				directoryBundleData = data[(int)((byte*)ptr - p)..];
			}
		}

		if (parsePaths) {
			var failed = ParsePaths();
			if (failed != 0) // To ignore these files, pass false to parsePaths and call ParsePaths manually.
				ThrowHelper.Throw<InvalidDataException>($"Parsing path failed for {failed} files");
		}
	}

	/// <summary>
	/// Whether <see cref="ParsePaths"/> has been called.
	/// </summary>
	protected bool pathsParsed;
	/// <summary>
	/// Parses all the <see cref="FileRecord.Path"/> of each <see cref="Files"/>.
	/// </summary>
	/// <returns>Number of paths failed to parse, these files will have <see cref="FileRecord.Path"/> as <see langword="null"/><br />
	/// If this method has been called before, skips parsing and returns 0 always no matter how many files have failed last time.</returns>
	/// <remarks>This will automatically be called by constructor if <see langword="true"/> passed to the parsePaths parameter (default to <see langword="true"/>),
	/// and throw if the returned value is not 0.</remarks>
	public virtual unsafe int ParsePaths() {
		EnsureNotDisposed();
		if (pathsParsed)
			return 0;
		ReadOnlySpan<byte> directory;
		using (var directoryBundle = new Bundle(new MemoryStream(directoryBundleData), false))
			directory = directoryBundle.ReadWithoutCache();
		var failed = 0;
		fixed (byte* p = directory) {
			foreach (var d in _Directories) {
				var temp = new List<byte[]>();
				var Base = false;
				var offset = p + d.Offset;
				var ptr = offset;
				while (ptr - offset <= d.Size - 4) {
					var index = *(int*)ptr;
					ptr += 4;
					if (index == 0) {
						Base = !Base;
						if (Base)
							temp.Clear();
					} else {
						index -= 1;
						var str = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(ptr);
						if (index < temp.Count) {
							var prefix = temp[index];
							var path = GC.AllocateUninitializedArray<byte>(prefix.Length + str.Length);
							prefix.CopyTo(path, 0);
							str.CopyTo(path.AsSpan(prefix.Length));
							if (Base)
								temp.Add(path);
							else {
								fixed (byte* pathPtr = path)
									if (_Files.TryGetValue(NameHash(path), out var f))
										f.Path = new string((sbyte*)pathPtr, 0, path.Length);
									else
										++failed;
							}
						} else {
							if (Base)
								temp.Add(str.ToArray());
							else {
								if (_Files.TryGetValue(NameHash(str), out var f))
									f.Path = new string((sbyte*)ptr, 0, str.Length);
								else
									++failed;
							}
						}
						ptr += str.Length + 1; // '\0'
					}
				}
			}
		}
		pathsParsed = true;
		return failed;
	}

	/// <summary>
	/// Save the _.index.bin file.
	/// Call this after modifying the files or bundles.
	/// </summary>
	/// <param name="compressor">Compressor to use, <see cref="Oodle.Compressor.Invalid"/> to use the what it used last time</param>
	/// <param name="compressionLevel">Compression level to use</param>
	public virtual void Save(Oodle.Compressor compressor = Oodle.Compressor.Mermaid, Oodle.CompressionLevel compressionLevel = Oodle.CompressionLevel.Normal) {
		lock (this) {
			if (_BundleToWrite is not null) {
				_BundleToWrite.Save(new(_BundleStreamToWrite!.GetBuffer(), 0, (int)_BundleStreamToWrite.Length));
				_BundleToWrite.Dispose();
				_BundleToWrite = null;
				_BundleStreamToWrite.SetLength(0);
			}
			_BundleStreamToWrite = null;

			EnsureNotDisposed();

			using var removed = new ValueList<BundleRecord>();
			for (int i = 0; i < CustomBundles.Count; ++i) {
				var br = CustomBundles[i];
				if (br.Files.Count == 0) { // Empty bundle
					CustomBundles.RemoveAt(i--);
					var bundleIndex = Array.IndexOf(_Bundles, br);
					if (bundleIndex >= 0)
						_Bundles.RemoveAt(bundleIndex);
					baseBundle.UncompressedSize -= br.RecordLength;
					removed.Add(br);
				}
			}
			for (var i = 0; i < _Bundles.Length; i++)
				_Bundles[i].BundleIndex = i;

			using var ms = new MemoryStream(baseBundle.UncompressedSize);
			ms.Write(_Bundles.Length);
			foreach (var b in _Bundles)
				b.Serialize(ms);

			var cap = (int)ms.Length + (sizeof(int) + sizeof(int)) + _Files.Count * FileRecord.RecordLength + _Directories.Length * DirectoryRecord.RecordLength + directoryBundleData.Length;
			if (ms.Capacity < cap)
				ms.Capacity = cap;

			ms.Write(_Files.Count);
			foreach (var f in _Files.Values)
				f.Serialize(ms);

			ms.Write(_Directories.Length);
			ms.Write(_Directories);

			ms.Write(directoryBundleData, 0, directoryBundleData.Length);
			baseBundle.Save(new(ms.GetBuffer(), 0, (int)ms.Length), compressor, compressionLevel);

			foreach (var br in removed)
				bundleFactory.DeleteBundle(br.Path);
		}
	}

	/// <summary>
	/// Get a FileRecord from its absolute path (This won't cause the tree building).
	/// The separator of the <paramref name="path"/> must be forward slash '/'
	/// </summary>
	/// <param name="path"><see cref="FileRecord.Path"/> </param>
	/// <returns>Null when not found</returns>
	public virtual bool TryGetFile(scoped ReadOnlySpan<char> path, [NotNullWhen(true)] out FileRecord? file) {
		EnsureNotDisposed();
		return _Files.TryGetValue(NameHash(path), out file);
	}

	/// <summary>
	/// Find a node in the tree. You should use <see cref="TryGetFile"/> instead of this if you have the absolute path of the file
	/// </summary>
	/// <param name="path">Relative path (with forward slashes) under <paramref name="root"/></param>
	/// <param name="node">The node found, or null when not found, or <paramref name="root"/> if <paramref name="path"/> is empty</param>
	/// <param name="root">Node to start searching, or <see langword="null"/> for <see cref="Root"/></param>
	/// <returns>Whether found a node</returns>
	public virtual bool TryFindNode(scoped ReadOnlySpan<char> path, [NotNullWhen(true)] out ITreeNode? node, DirectoryNode? root = null) {
		EnsureNotDisposed();
		root ??= Root;
		if (path == string.Empty) {
			node = root;
			return true;
		}
		foreach (var name in SpanExtensions.Split(path.TrimEnd('/'), '/')) {
			var next = root[name];
			if (next is not DirectoryNode dn)
				return (node = next) is not null;
			root = dn;
		}
		node = root;
		return true;
	}

	#region Extract/Replace
	/// <summary>
	/// Function for <see cref="Index"/>.Extract() to handle the extracted content of each file.
	/// </summary>
	/// <param name="record">Record of the file which the <paramref name="content"/> belongs to</param>
	/// <param name="content">Content of the file, or <see langword="null"/> if failed to get the bundle of the file</param>
	/// <returns><see langword="true"/> to cancel processing remaining files.</returns>
	/// <remarks>Do not Read/Write the <paramref name="record"/> during the extraction in this method.</remarks>
	public delegate bool FileHandler(FileRecord record, ReadOnlyMemory<byte>? content);
	/// <summary>
	/// Function for <see cref="Index"/>.Replace() to be called right after replacing each file.
	/// </summary>
	/// <param name="record">Record of the file replaced</param>
	/// <param name="path">Full path of the file replaced on disk or in zip</param>
	/// <returns><see langword="true"/> to cancel processing remaining files.</returns>
	/// <remarks>Do not Read/Write the <paramref name="record"/> during the replacement in this method.</remarks>
	public delegate bool FileCallback(FileRecord record, string path);

	/// <summary>
	/// Extract files in batch (out of order).
	/// </summary>
	/// <param name="files">Files to extract</param>
	/// <param name="callback">Function to execute on each file, see <see cref="FileHandler"/></param>
	/// <returns>Number of files extracted successfully.</returns>
	public static int Extract(IEnumerable<FileRecord> files, FileHandler callback) {
		var groups = files.GroupBy(f => f.BundleRecord);
		var count = 0;
		foreach (var g in groups) {
			if (g.Key.TryGetBundle(out var bd))
				using (bd)
					foreach (var f in g) {
						++count;
						if (callback(f, f.Read(bd)))
							break;
					}
			else
				foreach (var f in g) {
					if (callback(f, null))
						break;
				}
		}
		return count;
	}

	/// <summary>
	/// Extract files under a <paramref name="node"/> recursively (out of order).
	/// </summary>
	/// <param name="node">Node to extract</param>
	/// <param name="callback">Function to execute on each file, see <see cref="FileHandler"/></param>
	/// <returns>Number of files extracted successfully.</returns>
	public static int Extract(ITreeNode node, FileHandler callback) => Extract(Recursefiles(node).Select(n => n.Record), callback);

	/// <summary>
	/// Extract files under a <paramref name="node"/> to disk recursively (out of order).
	/// </summary>
	/// <param name="node">Node to extract</param>
	/// <param name="path">Path on disk to extract to</param>
	/// <param name="callback">See <see cref="FileCallback"/></param>
	/// <returns>Number of files extracted successfully.</returns>
	public static int Extract(ITreeNode node, string path, FileCallback? callback = null) {
		path = Path.GetFullPath(Utils.ExpandPath(path.TrimEnd('/', '\\'))) + Path.DirectorySeparatorChar;
		var trim = Path.GetDirectoryName(ITreeNode.GetPath(node).TrimEnd('/'))!.Length;

		Task? lastTask = null;
		FileRecord lastFr = null!;
		string lastPath = null!;
		var result = Extract(Recursefiles(node, path).Select(n => n.Record), (fr, data) => {
			if (!data.HasValue)
				return false;
			if (lastTask is not null) {
				lastTask.GetAwaiter().GetResult();
				if (callback?.Invoke(lastFr, lastPath) ?? false)
					return true;
			}
			lastPath = path + fr.Path[trim..];
			lastTask = SystemExtensions.System.IO.File.WriteAllBytesAsync(lastPath, data.Value).AsTask();
			lastFr = fr;
			return false;
		});
		if (lastTask is not null) {
			lastTask.GetAwaiter().GetResult();
			callback?.Invoke(lastFr, lastPath);
		}
		return result;
	}

	/// <summary>
	/// Extract files parallelly (out of order).
	/// </summary>
	/// <param name="files">Files to extract</param>
	/// <param name="callback">
	/// Action to execute on each file, see <see cref="FileHandler"/>
	/// <para>Note that this may be executed parallelly.</para>
	/// </param>
	/// <returns>Number of files extracted successfully.</returns>
	/// <remarks>
	/// This method is experimental and may not be faster than <see cref="Extract(IEnumerable{FileRecord}, FileHandler)"/>.
	/// <para>Files in the same bundle will be extracted sequentially in the same thread.</para>
	/// </remarks>
	public static int ExtractParallel(IEnumerable<FileRecord> files, FileHandler callback) {
		var count = 0;
		var cancelled = false;
		files.GroupBy(f => f.BundleRecord).AsParallel().ForAll(g => {
			if (cancelled)
				return;
			if (g.Key.TryGetBundle(out var bd))
				using (bd)
					foreach (var f in g) {
						if (cancelled)
							return;
						cancelled = callback(f, f.Read(bd));
						Interlocked.Increment(ref count);
					}
			else
				foreach (var f in g) {
					if (cancelled)
						return;
					callback(f, null);
				}
		});
		return count;
	}

	/// <summary>
	/// Extract files under a <paramref name="node"/> recursively (out of order) parallelly.
	/// </summary>
	/// <param name="node">Node to extract</param>
	/// <param name="callback">
	/// Action to execute on each file, see <see cref="FileHandler"/>
	/// <para>Note that this may be executed parallelly.</para>
	/// </param>
	/// <returns>Number of files extracted successfully.</returns>
	/// <remarks>
	/// This method is experimental and may not be faster than <see cref="Extract(ITreeNode, FileHandler)"/>.
	/// <para>Files in the same bundle will be extracted sequentially in the same thread.</para>
	/// </remarks>
	public static int ExtractParallel(ITreeNode node, FileHandler callback) => ExtractParallel(Recursefiles(node).Select(n => n.Record), callback);

	/// <summary>
	/// Extract files under a <paramref name="node"/> to disk recursively (out of order) parallelly.
	/// </summary>
	/// <param name="node">Node to extract</param>
	/// <param name="path">Path on disk to extract to</param>
	/// <param name="callback">
	/// See <see cref="FileCallback"/>
	/// <para>Note that this may be executed parallelly.</para>
	/// </param>
	/// <returns>Number of files extracted successfully.</returns>
	/// <remarks>
	/// This method is experimental and may not be faster than <see cref="Extract(ITreeNode, string, FileCallback?)"/>.
	/// <para>Files in the same bundle will be extracted sequentially in the same thread.</para>
	/// </remarks>
	public static int ExtractParallel(ITreeNode node, string path, FileCallback? callback = null) {
		path = Path.GetFullPath(Utils.ExpandPath(path.TrimEnd('/', '\\'))) + Path.DirectorySeparatorChar;
		var trim = Path.GetDirectoryName(ITreeNode.GetPath(node).TrimEnd('/'))!.Length;
		return ExtractParallel(Recursefiles(node, path).Select(n => n.Record), (fr, data) => {
			var p = path + fr.Path[trim..];
			if (data.HasValue)
				SystemExtensions.System.IO.File.WriteAllBytes(p, data.Value.Span);
			return callback?.Invoke(fr, p) ?? false;
		});
	}

	/// <summary>
	/// Patch with a zip file.
	/// Throw when a file in <paramref name="zipEntries"/> couldn't be found in <paramref name="index"/>.
	/// </summary>
	/// <param name="zipEntries">Entries to read files to replace</param>
	/// <param name="callback">See <see cref="FileCallback"/></param>
	/// <param name="saveIndex">Whether to call <see cref="Save"/> automatically after replacement done</param>
	/// <returns>Number of files replaced.</returns>
	public static int Replace(Index index, IEnumerable<ZipArchiveEntry> zipEntries, FileCallback? callback = null, bool saveIndex = true) {
		index.EnsureNotDisposed();
		var count = 0;
		foreach (var e in zipEntries) {
			if (e.FullName.EndsWith('/')) // dir
				continue;

			if (!index._Files.TryGetValue(index.NameHash(e.FullName), out var fr))
				ThrowHelper.Throw<FileNotFoundException>("Could not found file in Index: " + e.FullName);

			var length = (int)e.Length;
			using (var fs = e.Open())
				fr.Write(span => fs.ReadAtLeast(span, length), length);
			++count;

			if (callback?.Invoke(fr, e.FullName) ?? false)
				break;
		}

		if (saveIndex && count != 0)
			index.Save();
		return count;
	}

	/// <summary>
	/// Write files under a <paramref name="node"/> recursively (DFS).
	/// The search is based on <paramref name="node"/>, and skip any file not exist in <paramref name="path"/>.
	/// </summary>
	/// <param name="node">Node to replace</param>
	/// <param name="path">Path of a folder on disk to read files to replace</param>
	/// <param name="callback">See <see cref="FileCallback"/></param>
	/// <param name="saveIndex">Whether to call <see cref="Save"/> automatically after replacement done</param>
	/// <returns>Number of files replaced.</returns>
	public static int Replace(ITreeNode node, string path, FileCallback? callback = null, bool saveIndex = true) {
		path = Path.GetFullPath(Utils.ExpandPath(path.TrimEnd('/', '\\'))) + Path.DirectorySeparatorChar;
		var trim = ITreeNode.GetPath(node).Length;

		Index? index = null;
		var count = 0;
		foreach (var fn in Recursefiles(node)) {
			var fr = fn.Record;
			if (index is null) {// first file
				index = fr.BundleRecord.Index;
				index.EnsureNotDisposed();
			} else if (fr.BundleRecord.Index != index)
				ThrowHelper.Throw<InvalidOperationException>("Attempt to mixedly use FileRecords come from different Index");

			var p = path + fn.Record.Path[trim..];
			if (File.Exists(p)) {
				fr.Write(File.ReadAllBytes(p));
				++count;

				if (callback?.Invoke(fr, p) ?? false)
					break;
			}
		}

		if (saveIndex)
			index?.Save(); // count != 0
		return count;
	}

	/// <summary>
	/// Write files under a node recursively (DFS).
	/// The search is based on files in <paramref name="pathOnDisk"/>, and skip any file not exist under <paramref name="nodePath"/>.
	/// </summary>
	/// <param name="nodePath">Path of a <see cref="ITreeNode"/> in <paramref name="index"/> to replace. (with forward slashes, and not starting with slash)</param>
	/// <param name="pathOnDisk">Path of a folder on disk to read files to replace</param>
	/// <param name="callback">See <see cref="FileCallback"/></param>
	/// <param name="saveIndex">Whether to call <see cref="Save"/> automatically after replacement done</param>
	/// <returns>Number of files replaced.</returns>
	/// <remarks>
	/// This method won't check if a <see cref="ITreeNode"/> with <paramref name="nodePath"/> is exist.
	/// If not, the search still runs but always return 0.
	/// <para>
	/// Although there's a <paramref name="nodePath"/> parameter,
	/// this method doesn't require an actual <see cref="ITreeNode"/> (which requires <see cref="ParsePaths"/> and <see cref="BuildTree(CreateDirectoryInstance, CreateFileInstance, bool)"/>) to work.
	/// </para>
	/// </remarks>
	public static int Replace(Index index, string nodePath, string pathOnDisk, FileCallback? callback = null, bool saveIndex = true) {
		nodePath = nodePath.TrimEnd('/');
		pathOnDisk = Path.GetFullPath(Utils.ExpandPath(pathOnDisk.TrimEnd('/', '\\')));

		var count = 0;
		if (index.TryGetFile(nodePath, out var fr) && File.Exists(pathOnDisk)) {
			fr.Write(File.ReadAllBytes(pathOnDisk), saveIndex);
			callback?.Invoke(fr, pathOnDisk);
			count = 1;
		}

		if (!Directory.Exists(pathOnDisk))
			return count;

		if (Path.DirectorySeparatorChar != '/')
			pathOnDisk = pathOnDisk.Replace(Path.DirectorySeparatorChar, '/');
		var trim = pathOnDisk.Length;


		foreach (var p in Directory.EnumerateFiles(pathOnDisk, "*", SearchOption.AllDirectories))
			if (index.TryGetFile(nodePath + p[trim..], out fr)) {
				fr.Write(File.ReadAllBytes(p));
				++count;

				if (callback?.Invoke(fr, p) ?? false)
					break;
			}

		if (saveIndex && count != 0)
			index.Save();
		return count;
	}
	#endregion Extract/Replace

	/// <summary>
	/// Path to create bundle (Must end with slash)
	/// </summary>
	private const string CUSTOM_BUNDLE_BASE_PATH = "LibGGPK3/";
	/// <summary>
	/// Path of the default custom bundle. Assign it to <see cref="PinnedWriteBundlePath"/> to keep
	/// every change in one single, predictable bundle file <c>LibGGPK3/0.bundle.bin</c>.
	/// </summary>
	public const string DefaultWriteBundlePath = CUSTOM_BUNDLE_BASE_PATH + "0";
	/// <summary>
	/// Get an available bundle with size &lt; <see cref="MaxBundleSize"/>) to write under "Bundles2" with name start with <see cref="CUSTOM_BUNDLE_BASE_PATH"/>.
	/// Or create one if not found.
	/// Note that the returned bundle may contain existing data (with size: <paramref name="originalSize"/>) that should not be overwritten.
	/// </summary>
	/// <param name="originalSize">Size of the existing data in the bundle</param>
	/// <remarks>
	/// Since LibBundle3_v2.0.0, all changes to files should be written to a new bundle from this function instead of the old behavior (write to the original bundle or the smallest in Bundles).
	/// Remember to call <see cref="Bundle.Dispose"/> after use to prevent memory leak.
	/// </remarks>
	internal Bundle GetBundleToWrite(out int originalSize) {
		lock (this) {
			EnsureNotDisposed();
			originalSize = 0;

			static int GetSize(BundleRecord br) {
				var f = br._Files.MaxBy(f => f.Offset);
				if (f is null)
					return 0;
				return f.Offset + f.Size;
			}

			if (_pinnedWriteBundlePath is { Length: > 0 } pinned) {
				// All changes must land in one predictable bundle: never create LibGGPK3/1, 2, 3, ...
				// Flushing at MaxBundleSize still happens, but the write target stays the same bundle.
				var record = CustomBundles.Find(br => br._Path == pinned);
				if (record is null) {
					var created = CreateBundle(pinned);
					CustomBundles.Add(created.Record!);
					originalSize = GetSize(created.Record!);
					return created;
				}
				if (!record.TryGetBundle(out var pinnedBundle, out var exception)) {
					exception?.ThrowKeepStackTrace();
					throw new FileNotFoundException("Failed to get bundle: " + record.Path);
				}
				originalSize = GetSize(record);
				return pinnedBundle;
			}

			Bundle? b = null;
			foreach (var cb in CustomBundles) {
				if ((originalSize = GetSize(cb)) < MaxBundleSize && cb.TryGetBundle(out b))
					break;
			}

			if (b is null) { // Create one
				var path = CUSTOM_BUNDLE_BASE_PATH + CustomBundles.Count;
				if (CustomBundles.Exists(br => br._Path == path)) {
					for (var i = 0; i < CustomBundles.Count; i++) {
						var exists = false;
						for (var j = 0; j < CustomBundles.Count; j++)
							if (CustomBundles[j]._Path == CUSTOM_BUNDLE_BASE_PATH + i) {
								exists = true;
								break;
							}
						if (!exists) {
							path = CUSTOM_BUNDLE_BASE_PATH + i;
							break;
						}
					}
				}
				b = CreateBundle(path);
				CustomBundles.Add(b.Record!);
				originalSize = GetSize(b.Record!);
			}
			return b;
		}
	}

	/// <summary>
	/// Create a new bundle and add it to <see cref="Bundles"/> using <see cref="IBundleFactory.CreateBundle"/>
	/// </summary>
	/// <param name="bundlePath">Relative path of the bundle without ".bundle.bin"</param>
	protected virtual Bundle CreateBundle(string bundlePath) {
		lock (this) {
			EnsureNotDisposed();
			var len = _Bundles.Length;
			var br = new BundleRecord(bundlePath, 0, this, len);
			var b = new Bundle(bundleFactory.CreateBundle(bundlePath + ".bundle.bin"), br);
			Array.Resize(ref _Bundles, len + 1);
			_Bundles[len] = br;
			baseBundle.UncompressedSize += br.RecordLength; // Hack to prevent MemoryStream from reallocating when saving
			return b;
		}
	}

	/// <summary>
	/// Ensure there's a bundle being written and return it along with the buffer of its content.
	/// Shared by <see cref="FileRecord.Write(ReadOnlySpan{byte}, bool)"/> and <see cref="AddFile"/>.
	/// </summary>
	/// <param name="bundle">The bundle being written</param>
	/// <param name="stream">Buffer of the content being written, already containing the original data of <paramref name="bundle"/></param>
	/// <param name="extraCapacity">Hint of the size of the content to be appended</param>
	/// <remarks>Caller must hold the lock of this instance.</remarks>
	internal void EnsureWriteBundle(out Bundle bundle, out MemoryStream stream, int extraCapacity = 0) {
		var b = _BundleToWrite;
		var ms = _BundleStreamToWrite;
		if (b is null) {
			_BundleToWrite = b = GetBundleToWrite(out var originalSize);
			if (!WR_BundleStreamToWrite.TryGetTarget(out ms)) {
				_BundleStreamToWrite = ms = new(originalSize + extraCapacity);
				WR_BundleStreamToWrite.SetTarget(ms);
			} else
				_BundleStreamToWrite = ms;
			ms.Write(b.ReadWithoutCache(0, originalSize)); // Read original data of bundle
		}
		bundle = b!;
		stream = ms!;
	}

	/// <summary>
	/// Save the bundle being written when it grows beyond <see cref="MaxBundleSize"/> and start a new one.
	/// </summary>
	/// <remarks>Caller must hold the lock of this instance.</remarks>
	internal void FlushWriteBundle(Bundle bundle, MemoryStream stream) {
		if (stream.Length < MaxBundleSize)
			return;
		bundle.Save(new(stream.GetBuffer(), 0, (int)stream.Length));
		bundle.Dispose();
		_BundleToWrite = null;
		stream.SetLength(0);
		_BundleStreamToWrite = null;
	}

	/// <summary>
	/// Add a brand-new file to this index at <paramref name="path"/> with <paramref name="content"/>.
	/// </summary>
	/// <param name="path">
	/// Full path of the new file using forward slashes '/'.
	/// It will be lowercased because paths in Path of Exile are case-insensitive.
	/// </param>
	/// <param name="content">Content of the new file</param>
	/// <param name="saveIndex">Whether to call <see cref="Save"/> automatically after adding</param>
	/// <returns>The <see cref="FileRecord"/> created for the new file</returns>
	/// <remarks>
	/// The content is written to a bundle created by this library (see <see cref="GetBundleToWrite"/>),
	/// so no existing file (including the one you may copy from) is modified.
	/// <para>A path entry is also appended to the directory table of the index, so that
	/// <see cref="FileRecord.Path"/> of the new file can still be resolved by <see cref="ParsePaths"/> after reopening.</para>
	/// </remarks>
	/// <exception cref="ArgumentException"><paramref name="path"/> is not a valid relative path</exception>
	/// <exception cref="InvalidOperationException">A file with the same <see cref="FileRecord.PathHash"/> already exists</exception>
	public virtual FileRecord AddFile(scoped ReadOnlySpan<char> path, scoped ReadOnlySpan<byte> content, bool saveIndex = false) {
		FileRecord file;
		lock (this) {
			EnsureNotDisposed();
			var fullPath = NormalizeNewFilePath(path);
			var hash = NameHash(fullPath);
			if (_Files.ContainsKey(hash))
				throw new InvalidOperationException("A file with the same path hash already exists in the index: " + fullPath);

			EnsureWriteBundle(out var bundle, out var ms, content.Length);
			file = new FileRecord(hash, bundle.Record!, (int)ms.Length, content.Length) { Path = fullPath };
			ms.Write(content);
			_Files.Add(hash, file);
			bundle.Record!._Files.Add(file);
			AppendPathEntry(fullPath);
			FlushWriteBundle(bundle, ms);
			_Root = null; // The cached tree (if any) doesn't contain the new file yet
		}
		if (saveIndex)
			Save();
		return file;
	}

	/// <summary>
	/// Copy an existing file of this index to a new <paramref name="destPath"/> with an independent content,
	/// so that editing the copy affects neither the original file nor the other files referring to it.
	/// </summary>
	/// <param name="sourcePath">Path of the existing file to copy</param>
	/// <param name="destPath">Path of the new file, see <see cref="AddFile(ReadOnlySpan{char}, ReadOnlySpan{byte}, bool)"/></param>
	/// <param name="saveIndex">Whether to call <see cref="Save"/> automatically after copying</param>
	/// <returns>The <see cref="FileRecord"/> created for the new file</returns>
	/// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> is not found in this index</exception>
	/// <inheritdoc cref="AddFile(ReadOnlySpan{char}, ReadOnlySpan{byte}, bool)"/>
	public virtual FileRecord CopyFile(scoped ReadOnlySpan<char> sourcePath, scoped ReadOnlySpan<char> destPath, bool saveIndex = false) {
		EnsureNotDisposed();
		if (!TryGetFile(sourcePath, out var source) || source is null)
			throw new FileNotFoundException("Could not find file in Index: " + sourcePath.ToString(), sourcePath.ToString());
		return AddFile(destPath, source.Read().Span, saveIndex);
	}

	/// <summary>
	/// Validate and normalize the path of a file to be added.
	/// </summary>
	/// <returns>The lowercased path, which is what Path of Exile uses and what <see cref="NameHash(ReadOnlySpan{byte})"/> expects</returns>
	protected virtual string NormalizeNewFilePath(scoped ReadOnlySpan<char> path) {
		if (path.IsEmpty)
			throw new ArgumentException("Path of the new file must not be empty.", nameof(path));
		if (path[0] == '/' || path[^1] == '/')
			throw new ArgumentException("Path of the new file must be relative and must not start or end with '/': " + path.ToString(), nameof(path));
		foreach (var c in path)
			if (c is '\\' or ':' or '\0')
				throw new ArgumentException("Path of the new file must use '/' as separator and must not contain invalid characters: " + path.ToString(), nameof(path));
		return path.ToString().ToLowerInvariant();
	}

	/// <summary>
	/// Append a path entry of a newly added file to the directory table of this index,
	/// so that <see cref="ParsePaths"/> can still resolve <see cref="FileRecord.Path"/> for it after reopening.
	/// </summary>
	/// <param name="lowercasedPath"><see cref="FileRecord.Path"/> of the new file, already lowercased</param>
	/// <remarks>Caller must hold the lock of this instance.</remarks>
	protected virtual void AppendPathEntry(scoped ReadOnlySpan<char> lowercasedPath) {
		// Entry layout (little-endian ints): 0, 0, 1, <lowercased path in UTF8>, '\0'
		// The first 0 toggles Base on, the second toggles Base off, then index 1 (which is 0 after the -1)
		// is outside of the (empty) prefix table, so ParsePaths treats the following string as a full path.
		var pathBytes = Encoding.UTF8.GetBytes(lowercasedPath.ToString());
		var entry = new byte[(sizeof(int) * 3) + pathBytes.Length + 1]; // entry[^1] stays 0 as the terminator
		var entrySpan = entry.AsSpan();
		BitConverter.TryWriteBytes(entrySpan, 0);
		BitConverter.TryWriteBytes(entrySpan[sizeof(int)..], 0);
		BitConverter.TryWriteBytes(entrySpan[(sizeof(int) * 2)..], 1);
		pathBytes.AsSpan().CopyTo(entrySpan[(sizeof(int) * 3)..]);

		byte[] directory;
		using (var directoryBundle = new Bundle(new MemoryStream(directoryBundleData), false))
			directory = directoryBundle.ReadWithoutCache();

		var combined = GC.AllocateUninitializedArray<byte>(directory.Length + entry.Length);
		directory.CopyTo(combined, 0);
		entry.CopyTo(combined, directory.Length);

		using (var ms = new MemoryStream()) {
			using (var bundle = new Bundle(ms, (BundleRecord?)null))
				bundle.Save(combined);
			directoryBundleData = ms.ToArray();
		}

		var slash = lowercasedPath.LastIndexOf('/');
		var directoryPath = slash > 0 ? lowercasedPath[..slash] : ReadOnlySpan<char>.Empty;

		// RecursiveSize is the span of the directory's whole subtree inside the directory data.
		// Every record of a real index has a non-zero value (a leaf has RecursiveSize == Size),
		// and the client's parser mis-reads the table when it is 0.
		var record = new DirectoryRecord(NameHash(directoryPath), directory.Length, entry.Length, entry.Length);
		Array.Resize(ref _Directories, _Directories.Length + 1);
		_Directories[^1] = record;

		// The entry is appended at the very end of the directory data, so the root's span must grow to cover it.
		var rootHash = NameHash(ReadOnlySpan<char>.Empty);
		for (var i = 0; i < _Directories.Length - 1; i++)
			if (_Directories[i].PathHash == rootHash) {
				_Directories[i].RecursiveSize += entry.Length;
				break;
			}
	}

	#region NameHashing
	/// <summary>
	/// Get the hash of a file path
	/// </summary>
	[SkipLocalsInit]
	public unsafe ulong NameHash(scoped ReadOnlySpan<char> name) {
		if (_Directories[0].PathHash == 0xF42A94E69CFF42FEul) { // since poe 3.21.2 patch
			Span<char> span = stackalloc char[name.Length];
			name.ToLowerInvariant(span); // changing case does not affect length
			var utf8 = MemoryMarshal.AsBytes(span);
			return NameHash(utf8[..Encoding.UTF8.GetBytes(span, utf8)]);
		} else {
			Span<byte> utf8 = stackalloc byte[name.Length];
			return NameHash(utf8[..Encoding.UTF8.GetBytes(name, utf8)]);
		}
	}

	/// <summary>
	/// Get the hash of a file path,
	/// <paramref name="utf8Name"/> must be lowercased unless it comes from ggpk before patch 3.21.2
	/// </summary>
	public unsafe ulong NameHash(scoped ReadOnlySpan<byte> utf8Name) {
		EnsureNotDisposed();
		switch (_Directories[0].PathHash) {
			case 0xF42A94E69CFF42FE:
				return MurmurHash64A(utf8Name); // since poe 3.21.2 patch
			case 0x07E47507B4A92E53:
				return FNV1a64Hash(utf8Name);
		}
		ThrowHelper.Throw<Exception>("Unable to detect the namehash algorithm");
		return 0;
	}

	/// <summary>
	/// Get the hash of a file path, <paramref name="utf8Name"/> must be lowercased
	/// </summary>
	protected static unsafe ulong MurmurHash64A(scoped ReadOnlySpan<byte> utf8Name, ulong seed = 0x1337B33F) {
		if (utf8Name.IsEmpty)
			return 0xF42A94E69CFF42FEul;

		ref ulong p = ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(utf8Name));
		if (Unsafe.AddByteOffset(ref p, utf8Name.Length - 1) == '/')
			utf8Name = utf8Name.SliceUnchecked(0, utf8Name.Length - 1); // TrimEnd('/')

		const ulong m = 0xC6A4A7935BD1E995ul;
		const int r = 47;

		unchecked {
			seed ^= (ulong)utf8Name.Length * m;

			if (utf8Name.Length >= sizeof(ulong)) {
				ref ulong pEnd = ref Unsafe.Add(ref p, utf8Name.Length / sizeof(ulong));
				do {
					ulong k = p * m;
					seed = (seed ^ (k ^ (k >> r)) * m) * m;
					p = ref Unsafe.Add(ref p, 1);
				} while (Unsafe.IsAddressLessThan(ref p, ref pEnd));
			}

			int remainingBytes = utf8Name.Length % sizeof(ulong);
			if (remainingBytes != 0)
				seed = (seed ^ (p & (ulong.MaxValue >> ((sizeof(ulong) - remainingBytes) * 8)))) * m;

			seed = (seed ^ (seed >> r)) * m;
			return seed ^ (seed >> r);
		}
	}

	/// <summary>
	/// Get the hash of a file path with ggpk before patch 3.21.2
	/// </summary>
	protected static unsafe ulong FNV1a64Hash(scoped ReadOnlySpan<byte> utf8Str) {
		var hash = 0xCBF29CE484222325ul;
		const ulong FNV_prime = 0x100000001B3ul;
		unchecked {
			if (utf8Str[^1] == '/') {
				utf8Str = utf8Str[..^1]; // TrimEnd('/')
				foreach (ulong ch in utf8Str)
					hash = (hash ^ ch) * FNV_prime;
			} else
				foreach (ulong ch in utf8Str) {
					if (ch is >= 'A' and <= 'Z')
						hash = (hash ^ (ch + (ulong)('A' - 'a'))) * FNV_prime; // ToLower
					else
						hash = (hash ^ ch) * FNV_prime;
				}
			return (((hash ^ '+') * FNV_prime) ^ '+') * FNV_prime; // filenames end with two '+'
		}
	}
	#endregion NameHashing

	#region Helpers
	/// <summary>
	/// Enumerate all files under a node (DFS).
	/// </summary>
	/// <param name="node">Node to start recursive</param>
	public static IEnumerable<IFileNode> Recursefiles(ITreeNode node) {
		if (node is IFileNode fn)
			yield return fn;
		else if (node is IDirectoryNode dn)
			foreach (var n in dn.Children)
				foreach (var f in Recursefiles(n))
					yield return f;
	}
	/// <summary>
	/// Enumerate all files under a node (DFS), and call <see cref="Directory.CreateDirectory(string)"/> for each folder.
	/// </summary>
	/// <param name="node">Node to start recursive</param>
	/// <param name="path">Path on disk</param>
	protected static IEnumerable<IFileNode> Recursefiles(ITreeNode node, string path) {
		Directory.CreateDirectory(path);
		if (node is IFileNode fn)
			yield return fn;
		else if (node is IDirectoryNode dn)
			foreach (var n in dn.Children)
				foreach (var f in Recursefiles(n, $@"{path}/{dn.Name}"))
					yield return f;
	}

	/// <summary>
	/// Sort files by index of their bundle (<see cref="BundleRecord.BundleIndex"/>) with CountingSort (stable) to get better performance for reading.
	/// </summary>
	/// <remarks>
	/// <code>IEnumerable&lt;FileRecord&gt;.GroupBy(f => f.BundleRecord);</code> can also achieve similar purposes in some case.
	/// </remarks>
	/// <seealso cref="Enumerable.GroupBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/>
	public static unsafe FileRecord[] SortByBundle(IEnumerable<FileRecord> files) {
		var list = files is IReadOnlyList<FileRecord> irl ? irl : files is IList<FileRecord> il ? il.AsIReadOnly() : files.ToList();
		if (list.Count <= 0)
			return [];
		var index = list[0].BundleRecord.Index;
		var bundleCount = index._Bundles.Length;
		var count = new int[bundleCount];
		foreach (var f in list) {
			if (f.BundleRecord.Index != index)
				ThrowHelper.Throw<InvalidOperationException>("Attempt to mixedly use FileRecords come from different Index");
			++count[f.BundleRecord.BundleIndex];
		}
		for (var i = 1; i < bundleCount; ++i)
			count[i] += count[i - 1];
		var sorted = new FileRecord[list.Count];
		for (var i = sorted.Length - 1; i >= 0; --i)
			sorted[--count[list[i].BundleRecord.BundleIndex]] = list[i];
		return sorted;
	}
	#endregion Helpers

	protected virtual void EnsureNotDisposed() {
		baseBundle.EnsureNotDisposed();
	}

	/// <summary>
	/// Get the field of the base stream of this instance.
	/// Using this method may cause dangerous unexpected behavior.
	/// </summary>
	[EditorBrowsable(EditorBrowsableState.Advanced)]
	public ref Stream UnsafeGetStream() {
		return ref baseBundle.UnsafeGetStream();
	}

	public virtual void Dispose() {
		GC.SuppressFinalize(this);
		lock (this) {
			if (_BundleToWrite is not null) {
				Debug.Fail("There're still changes haven't been saved when disposing Index. Did you forget to call Save()?");
				// Disposal is not a commit boundary. If the caller exits through an
				// exception without Save(), discard the buffered bundle instead of
				// flushing a partial mutation into the game data.
				_BundleToWrite.Abort();
				_BundleToWrite = null;
			}
			_BundleStreamToWrite?.Dispose();
			_BundleStreamToWrite = null;
			WR_BundleStreamToWrite.SetTarget(null!);
			baseBundle.Dispose();
			_Root = null;

			// Drop the big managed blobs as well, not just the underlying stream.
			// Every FileRecord reaches back to this Index through BundleRecord.Index, so a single
			// record left alive by a caller (a UI selection, a cached view model, ...) keeps the whole
			// index graph reachable and nothing gets collected. Clearing here makes a disposed index
			// an empty shell no matter who still holds a reference to one of its records.
			_Files.Clear();
			_Files.TrimExcess();
			foreach (var bundle in _Bundles)
				bundle._Files.Clear();
			_Bundles = [];
			_Directories = [];
			directoryBundleData = [];
			CustomBundles.Clear();
		}
	}

	/// <summary>
	/// Currently unused
	/// </summary>
	[Serializable]
	[StructLayout(LayoutKind.Sequential, Size = RecordLength, Pack = 4)]
	protected internal struct DirectoryRecord(ulong pathHash, int offset, int size, int recursiveSize) {
		public ulong PathHash = pathHash;
		public int Offset = offset;
		public int Size = size;
		public int RecursiveSize = recursiveSize;

		/// <summary>
		/// Size of the content when serializing to <see cref="Index"/>
		/// </summary>
		public const int RecordLength = sizeof(ulong) + sizeof(int) * 3;
	}
}
