using PoEToolbox.Core.Schema;

namespace PoEToolbox.Core.Binary.Datc64;

/// <summary>
/// Shared constants and utilities for the Datc64 binary format.
/// </summary>
public static class Datc64Constants
{
    /// <summary>Null sentinel for 32-bit row references.</summary>
    public const uint NullU32 = 0xFEFEFEFE;

    /// <summary>Null sentinel for 64-bit row references.</summary>
    public const ulong NullU64 = 0xFEFEFEFE_FEFEFEFE;

    /// <summary>Key(N) null value (= u64::MAX).</summary>
    public const ulong NullKey = 18446744073709551615;

    /// <summary>Separator between fixed rows and variable data section.</summary>
    public static readonly byte[] Separator = [0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB];

    /// <summary>
    /// Calculate the byte size of a column in the fixed row section.
    /// </summary>
    public static int ColumnSize(ColumnDef col, bool is64Bit)
    {
        if (col.IsArray)
            return is64Bit ? 16 : 8;

        var scalarSize = col.Type switch
        {
            "bool" => 1,
            "i8" or "u8" or "byte" => 1,
            "i16" or "u16" or "short" or "ushort" => 2,
            "i32" or "u32" or "enumrow" or "f32" or "int" or "uint" or "float" => 4,
            "i64" or "u64" or "f64" or "long" or "ulong" or "double" => 8,
            "string" or "row" => is64Bit ? 8 : 4,
            "foreignrow" => is64Bit ? 16 : 8,
            _ => is64Bit ? 8 : 4,
        };
        return col.IsInterval ? scalarSize * 2 : scalarSize;
    }

    /// <summary>
    /// Parse "Key(N)" string to integer, or return null.
    /// </summary>
    public static ulong? ParseKey(object? value)
    {
        if (value is string s && s.StartsWith("Key(") && s.EndsWith(')'))
        {
            if (ulong.TryParse(s[4..^1], out var num))
                return num;
        }
        return null;
    }

    /// <summary>
    /// Extract table name from a file path by stripping directory and suffixes.
    /// </summary>
    public static string TableNameFromPath(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.Split('-')[0].Split('_')[0];
    }

    /// <summary>
    /// Calculate column offset within a row.
    /// </summary>
    public static int ColumnOffset(IReadOnlyList<ColumnDef> columns, int colIdx, bool is64Bit)
    {
        var idx = 0;
        for (var i = 0; i < colIdx; i++)
            idx += ColumnSize(columns[i], is64Bit);
        return idx;
    }
}
