using System.Buffers.Binary;
using System.IO;
using System.Text;
using LibBundle3;
using LibBundle3.Records;

namespace PoEToolbox.Tests;

/// <summary>
/// Builds a minimal but valid Bundles2 <c>_.index.bin</c> on disk, so tests can exercise code paths
/// that open real game data (<see cref="GameDataAccess"/>) without shipping a client or touching the
/// one installed on the machine.
/// </summary>
/// <remarks>
/// The layout matches what LibBundle3 expects: a directory table holding one full-path entry per
/// file, one seed bundle holding the contents, and an index body with the bundle/file/directory
/// tables followed by the directory bundle data.
/// </remarks>
internal static class SeedIndex
{
    /// <summary>
    /// Writes <c>_.index.bin</c> plus its <c>seedbundle.bundle.bin</c> into <paramref name="directory"/>
    /// and returns the fully qualified index path.
    /// </summary>
    public static string Build(string directory, params (string Path, byte[] Content)[] seedFiles)
    {
        if (seedFiles.Length == 0)
            throw new ArgumentException("At least one seed file is required.", nameof(seedFiles));

        Directory.CreateDirectory(directory);

        // 1. The directory table data: one "full path" entry per seed file, one record each.
        var directoryData = new List<byte>();
        var directoryRecords = new List<(int Offset, int Size)>();
        foreach (var (path, _) in seedFiles)
        {
            var offset = directoryData.Count;
            AppendFullPathEntry(directoryData, path);
            directoryRecords.Add((offset, directoryData.Count - offset));
        }
        var directoryBundleData = BuildBundleBlob(directoryData.ToArray());

        // 2. One seed bundle holding all seed files back to back.
        var bundleContent = new List<byte>();
        var fileOffsets = new List<(int Offset, int Size)>();
        foreach (var (_, content) in seedFiles)
        {
            fileOffsets.Add((bundleContent.Count, content.Length));
            bundleContent.AddRange(content);
        }
        File.WriteAllBytes(
            Path.Combine(directory, "seedbundle.bundle.bin"),
            BuildBundleBlob(bundleContent.ToArray()));

        // 3. The index body: bundles, files, directory records, then the directory bundle data.
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1); // bundle count
            const string bundleName = "seedbundle";
            writer.Write(bundleName.Length);
            writer.Write(Encoding.ASCII.GetBytes(bundleName));
            writer.Write(bundleContent.Count); // uncompressed size of the bundle

            writer.Write(seedFiles.Length); // file count
            for (var i = 0; i < seedFiles.Length; i++)
            {
                writer.Write(MurmurHash64A(Encoding.UTF8.GetBytes(seedFiles[i].Path)));
                writer.Write(0); // bundle index
                writer.Write(fileOffsets[i].Offset);
                writer.Write(fileOffsets[i].Size);
            }

            writer.Write(directoryRecords.Count); // directory count
            foreach (var (offset, size) in directoryRecords)
            {
                writer.Write(0xF42A94E69CFF42FEul); // marks the post-3.21.2 MurmurHash algorithm
                writer.Write(offset);
                writer.Write(size);
                writer.Write(size); // recursive size; a leaf record has recursive == size
            }

            writer.Write(directoryBundleData);
        }

        var indexPath = Path.Combine(directory, "_.index.bin");
        File.WriteAllBytes(indexPath, BuildBundleBlob(body.ToArray()));
        return Path.GetFullPath(indexPath);
    }

    /// <summary>Appends the directory-table encoding of a full path: 0, 0, 1, &lt;utf8 path&gt;, '\0'.</summary>
    private static void AppendFullPathEntry(List<byte> destination, string lowercasedPath)
    {
        var entry = new byte[(sizeof(int) * 3) + Encoding.UTF8.GetByteCount(lowercasedPath) + 1];
        var entrySpan = entry.AsSpan();
        BitConverter.TryWriteBytes(entrySpan, 0);
        BitConverter.TryWriteBytes(entrySpan[sizeof(int)..], 0);
        BitConverter.TryWriteBytes(entrySpan[(sizeof(int) * 2)..], 1);
        Encoding.UTF8.GetBytes(lowercasedPath, entrySpan[(sizeof(int) * 3)..]);
        destination.AddRange(entry);
    }

    /// <summary>Compresses <paramref name="content"/> into a bundle blob using the library's own bundle format.</summary>
    private static byte[] BuildBundleBlob(byte[] content)
    {
        var stream = new MemoryStream();
        var bundle = new SeedBundle(stream);
        bundle.Save(content);
        var bytes = stream.ToArray();
        bundle.Dispose();
        return bytes;
    }

    private sealed class SeedBundle(Stream stream) : Bundle(stream, (BundleRecord?)null);

    /// <summary>MurmurHash64A with the seed used by LibBundle3 for post-3.21.2 client indexes.</summary>
    private static ulong MurmurHash64A(ReadOnlySpan<byte> utf8Name)
    {
        const ulong m = 0xC6A4A7935BD1E995ul;
        const int r = 47;

        var h = 0x1337B33Ful ^ ((ulong)utf8Name.Length * m);
        var i = 0;
        while (i + sizeof(ulong) <= utf8Name.Length)
        {
            var k = BinaryPrimitives.ReadUInt64LittleEndian(utf8Name.Slice(i, sizeof(ulong)));
            k *= m;
            k ^= k >> r;
            k *= m;
            h ^= k;
            h *= m;
            i += sizeof(ulong);
        }

        var remaining = utf8Name.Length - i;
        if (remaining != 0)
        {
            ulong tail = 0;
            for (var j = 0; j < remaining; j++)
                tail |= (ulong)utf8Name[i + j] << (8 * j);
            h ^= tail;
            h *= m;
        }

        h ^= h >> r;
        h *= m;
        h ^= h >> r;
        return h;
    }
}
