using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

using SystemExtensions;
using SystemExtensions.Streams;

namespace LibBundle3.Records;
public class FileRecord {
	/// <summary>
	/// Hash of <see cref="Path"/> which can be caculated from <see cref="Index.NameHash(ReadOnlySpan{char})"/>
	/// </summary>
	public virtual ulong PathHash { get; }
	/// <summary>
	/// Bundle which contains this file
	/// </summary>
	public virtual BundleRecord BundleRecord { get; protected set; }
	/// <summary>
	/// Offset of the file content in data of <see cref="BundleRecord"/>
	/// </summary>
	public virtual int Offset { get; protected set; }
	/// <summary>
	/// Size of the file content in bytes
	/// </summary>
	public virtual int Size { get; protected set; }

	/// <summary>
	/// Full path of the file in <see cref="Index"/>
	/// </summary>
	/// <remarks>
	/// This will be <see langword="null"/> if the <see cref="Index.ParsePaths"/> has never been called.
	/// </remarks>
	public virtual string Path { get; protected internal set; /* For Index.ParsePaths */ }

#pragma warning disable CS8618
	protected internal FileRecord(ulong pathHash, BundleRecord bundleRecord, int offset, int size) {
		PathHash = pathHash;
		BundleRecord = bundleRecord;
		Offset = offset;
		Size = size;
	}

	/// <summary>
	/// Read the content of the file.
	/// </summary>
	/// <param name="bundle">If specified, read from this bundle instance instead of creating a new one</param>
	/// <remarks>
	/// When reading multiple files in batches, use <see cref="Index.Extract(IEnumerable{FileRecord}, Index.FileHandler)"/> instead for better performance.
	/// </remarks>
	public virtual ReadOnlyMemory<byte> Read(Bundle? bundle = null) {
		if (bundle is not null)
			return bundle.Read(Offset, Size);
		// TODO: Bundle cache implementation
		var index = BundleRecord.Index;
		bundle = index._BundleToWrite;
		if (bundle?.Record == BundleRecord) // The bundle being written
			return ReadFromWriteBundle(index, Offset);
		if (BundleRecord.TryGetBundle(out bundle, out var ex))
			using (bundle)
				return bundle.ReadWithoutCache(Offset, Size);
		ex?.ThrowKeepStackTrace();
		throw new FileNotFoundException("Failed to get bundle: " + BundleRecord.Path);
	}

	/// <summary>
	/// Read a part of the content of the file.
	/// </summary>
	/// <param name="range">The range of the content to read</param>
	/// <param name="bundle">If specified, read from this bundle instance instead of creating a new one</param>
	/// <remarks>
	/// When reading multiple files in batches, use <see cref="Index.Extract(IEnumerable{FileRecord}, Index.FileHandler)"/> instead for better performance.
	/// </remarks>
	public virtual ReadOnlyMemory<byte> Read(Range range, Bundle? bundle = null) {
		var (offset, length) = range.GetOffsetAndLength(Size);
		if (bundle is not null)
			return bundle.Read(Offset + offset, length);
		var index = BundleRecord.Index;
		bundle = index._BundleToWrite;
		if (bundle?.Record == BundleRecord) // The bundle being written
			return ReadFromWriteBundle(index, Offset + offset);
		if (BundleRecord.TryGetBundle(out bundle, out var ex))
			using (bundle) // TODO: Bundle cache implementation
				return bundle.ReadWithoutCache(Offset + offset, length);
		ex?.ThrowKeepStackTrace();
		throw new FileNotFoundException("Failed to get bundle: " + BundleRecord.Path);
	}

	/// <summary>
	/// Read this record from the index's bundle being written.
	/// </summary>
	/// <remarks>
	/// The pending content exists only in the write buffer: the bundle's <c>metadata.uncompressed_size</c>
	/// still reports its last saved size until <see cref="Index.Save"/>, so reading through the bundle
	/// would fail for anything appended after it was created. Read from the buffer instead while it is
	/// still pending; once it has been flushed the bundle on disk is up to date again.
	/// </remarks>
	protected internal virtual ReadOnlyMemory<byte> ReadFromWriteBundle(Index index, int absoluteOffset) {
		var ms = index._BundleStreamToWrite;
		if (ms is not null && ms.Length >= (long)absoluteOffset + Size) {
			lock (index) {
				return ms.GetBuffer().AsSpan(absoluteOffset, Size).ToArray();
			}
		}
		return index._BundleToWrite!.ReadWithoutCache(absoluteOffset, Size);
	}

	/// <summary>
	/// Replace the content of the file.
	/// </summary>
	/// <param name="saveIndex">
	/// Whether to call <see cref="Index.Save"/> automatically after writing.
	/// This causes performance penalties when writing multiple files.
	/// </param>
	/// <remarks>
	/// You must call <see cref="Index.Save"/> (unless <paramref name="saveIndex"/>) to save changes after editing all files you want.
	/// </remarks>
	public virtual void Write(scoped ReadOnlySpan<byte> newContent, bool saveIndex = false) {
		var index = BundleRecord.Index;
		lock (index) {
			index.EnsureWriteBundle(out var b, out var ms, newContent.Length);
			Redirect(b.Record!, (int)ms.Length, newContent.Length);
			ms.Write(newContent);
			index.FlushWriteBundle(b, ms);
		}
		if (saveIndex)
			index.Save();
	}
	/// <inheritdoc cref="Write(ReadOnlySpan{byte}, bool)"/>
	/// <param name="writer">
	/// <see langword="delegate"/> that provide a <see cref="Span{T}"/> with <paramref name="newSize"/> length
	/// to let you write the new content of the file
	/// </param>
	/// <param name="newSize">Size in bytes of the new content</param>
#if NET9_0_OR_GREATER
	public virtual void Write(Action<Span<byte>> writer, int newSize, bool saveIndex = false) {
#else
	public virtual void Write(WriteAction writer, int newSize, bool saveIndex = false) {
#endif
		var index = BundleRecord.Index;
		lock (index) {
			index.EnsureWriteBundle(out var b, out var ms, newSize);
			var ibw = ms.AsIBufferWriter();
			writer(ibw.GetSpan(newSize)[..newSize]);
			Redirect(b.Record!, (int)ms.Length, newSize);
			ibw.Advance(newSize);
			index.FlushWriteBundle(b, ms);
		}
		if (saveIndex)
			index.Save();
	}
#if !NET9_0_OR_GREATER
	public delegate void WriteAction(scoped Span<byte> buffer);
#endif

	/// <summary>
	/// Redirect the <see cref="FileRecord"/> to another section in specified bundle.
	/// Must call <see cref="Index.Save"/> to save changes after editing all files you want.
	/// </summary>
	public virtual void Redirect(BundleRecord bundle, int offset, int size) {
		if (BundleRecord != bundle) {
			if (bundle.Index != BundleRecord.Index)
				ThrowHelper.Throw<InvalidOperationException>("Attempt to redirect the file to a bundle in another index");
			BundleRecord._Files.Remove(this);
			BundleRecord = bundle;
			bundle._Files.Add(this);
		}
		Offset = offset;
		Size = size;
	}

	/// <summary>
	/// Size of the content when <see cref="Serialize"/> to <see cref="Index"/>
	/// </summary>
	protected internal const int RecordLength = sizeof(ulong) + sizeof(int) * 3;
	/// <summary>
	/// Function to serialize the record to <see cref="Index"/>
	/// </summary>
	protected internal virtual void Serialize(Stream stream) {
		stream.Write(PathHash);
		stream.Write(BundleRecord.BundleIndex);
		stream.Write(Offset);
		stream.Write(Size);
	}
}
