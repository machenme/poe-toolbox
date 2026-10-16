using System.Buffers.Binary;
using System.Text.Json;
using PoEToolbox.Core.Schema;

namespace PoEToolbox.Core.Binary.Datc64;

/// <summary>
/// Represents a PoE .datc64 (or .dat) table file: parse → edit rows → encode back.
/// Supports both 32-bit (.dat) and 64-bit (.datc64) formats.
/// </summary>
public sealed class Datc64File
{
    public string TableName { get; }
    public bool Is64Bit { get; }
    public TableSchema Schema { get; }
    public IReadOnlyList<ColumnDef> Columns { get; }
    public List<Dictionary<string, object?>> Rows { get; set; }
    /// <summary>Actual row length detected from binary (may differ from schema sum due to unnamed columns)</summary>
    public int RowLength { get; private set; }
    /// <summary>Raw trailing bytes per row (unnamed columns), preserved for lossless re-encode</summary>
    private byte[]? _trailingBytes;
    private int _schemaSize;

    public int Count => Rows.Count;

    private Datc64File(string tableName, bool is64Bit, TableSchema schema)
    {
        TableName = tableName;
        Is64Bit = is64Bit;
        Schema = schema;
        Columns = schema.Columns;
        RowLength = schema.Columns.Sum(c => Datc64Constants.ColumnSize(c, is64Bit));
        Rows = [];
    }

    // ═══ Construction ═════════════════════════════════════════

    public static Datc64File FromBytes(byte[] data, string tableName, bool is64Bit = true, int? validFor = null)
    {
        var vf = validFor ?? (is64Bit ? 2 : 1);
        var schema = SchemaManager.LoadTable(tableName, vf);
        var instance = new Datc64File(tableName, is64Bit, schema);
        instance._Parse(data);
        return instance;
    }

    /// <summary>
    /// Converts a DAT file to JSON without materializing an editable table.
    /// This is intended for extraction only; use <see cref="FromBytes"/> when rows must be modified.
    /// </summary>
    public static void ExportJson(byte[] data, string tableName, string path, bool is64Bit = true, int? validFor = null)
    {
        var vf = validFor ?? (is64Bit ? 2 : 1);
        var schema = SchemaManager.LoadTable(tableName, vf);
        var instance = new Datc64File(tableName, is64Bit, schema);
        instance.WriteJsonFromData(data, path);
    }

    /// <summary>
    /// Auto-detect bitness and validFor from file extension:
    ///   .datc64 → 64-bit, validFor=2 (PoE2 unified)
    ///   .datcl64 → 64-bit, validFor=1 (PoE1 legacy)
    ///   .dat64 → 64-bit, validFor=2
    ///   .dat → 32-bit, validFor=1
    /// </summary>
    public static (bool is64Bit, int validFor) DetectFromExtension(string path)
    {
        // PoE1 paths are under "data/", PoE2 paths under "data/balance/"
        var normalizedPath = path.Replace('\\', '/');
        var isPoE2 = normalizedPath.Contains("/balance/", StringComparison.OrdinalIgnoreCase);

        if (normalizedPath.EndsWith(".datcl64", StringComparison.OrdinalIgnoreCase))
            return (true, 1);
        if (normalizedPath.EndsWith(".datc64", StringComparison.OrdinalIgnoreCase)
         || normalizedPath.EndsWith(".dat64", StringComparison.OrdinalIgnoreCase))
            return (true, isPoE2 ? 2 : 1);
        return (false, 1); // .dat → 32-bit PoE1
    }

    public static Datc64File FromFile(string path)
    {
        var tableName = Datc64Constants.TableNameFromPath(path);
        var (is64Bit, validFor) = DetectFromExtension(path);
        var data = File.ReadAllBytes(path);
        return FromBytes(data, tableName, is64Bit, validFor);
    }

    // FromJson 已删除：它是死代码（全仓只有定义、无调用者），而 <object> 的多态反序列化
    // 在 Native AOT 下走不通（反射式序列化被禁用）。若要恢复，须为 object 多态手写读取器。
    // ⚠️ 语义坑备忘：反射式 Deserialize 把 JSON 值填进 object? 时得到的是 JsonElement，
    // 而不是 List<object?> / 原生数值 —— 手写读取器必须复现这一点，否则下游 Datc64Encoder 行为会变。

    // ═══ Parse ═══════════════════════════════════════════════

    private void _Parse(byte[] data)
    {
        var layout = GetLayout(data);
        RowLength = layout.RowLength;
        _schemaSize = layout.SchemaSize;
        var trailingPerRow = RowLength - _schemaSize;
        if (trailingPerRow > 0)
            _trailingBytes = new byte[layout.RowCount * trailingPerRow];

        var rows = new List<Dictionary<string, object?>>(layout.RowCount);

        for (var rowIdx = 0; rowIdx < layout.RowCount; rowIdx++)
        {
            var rowStart = 4 + rowIdx * layout.RowLength;
            var row = ParseRow(data, rowStart, layout.DataSectionOffset);

            if (_trailingBytes is not null)
            {
                var trailingStart = rowStart + _schemaSize;
                if (trailingStart + trailingPerRow <= data.Length)
                    Array.Copy(data, trailingStart, _trailingBytes, rowIdx * trailingPerRow, trailingPerRow);
            }

            rows.Add(row);
        }

        Rows = rows;
    }

    private void WriteJsonFromData(byte[] data, string path)
    {
        var layout = GetLayout(data);
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, JsonWriterOptions);
        writer.WriteStartArray();
        for (var rowIdx = 0; rowIdx < layout.RowCount; rowIdx++)
        {
            var rowStart = 4 + rowIdx * layout.RowLength;
            WriteRow(writer, ParseRow(data, rowStart, layout.DataSectionOffset));
        }
        writer.WriteEndArray();
        writer.Flush();
    }

    private ParsedLayout GetLayout(byte[] data)
    {
        if (data.Length < sizeof(int))
            throw new InvalidDataException("DAT file is smaller than its row-count header.");

        var rowCount = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (rowCount < 0)
            throw new InvalidDataException("DAT file has a negative row count.");

        var schemaSize = Columns.Sum(c => Datc64Constants.ColumnSize(c, Is64Bit));
        var rowLength = schemaSize;
        var dataSectionOffset = 4;
        if (rowCount > 0)
        {
            var bestRowLength = 0;
            for (var i = 4; i <= data.Length - Datc64Constants.Separator.Length; i++)
            {
                if (!data.AsSpan(i, Datc64Constants.Separator.Length).SequenceEqual(Datc64Constants.Separator))
                    continue;

                var fixedSize = i - 4;
                if (fixedSize > 0 && fixedSize % rowCount == 0)
                {
                    var candidate = fixedSize / rowCount;
                    if (candidate > bestRowLength)
                    {
                        bestRowLength = candidate;
                        dataSectionOffset = i + Datc64Constants.Separator.Length;
                    }
                }
            }

            if (bestRowLength == 0)
                throw new InvalidDataException("DAT file has no aligned fixed-data boundary.");
            rowLength = bestRowLength;
        }
        else if (data.Length >= 12 && data.AsSpan(4, 8).SequenceEqual(Datc64Constants.Separator))
        {
            dataSectionOffset = 12;
        }

        return new ParsedLayout(rowCount, rowLength, dataSectionOffset, schemaSize);
    }

    private Dictionary<string, object?> ParseRow(byte[] data, int rowStart, int dataSectionOffset)
    {
        var off = rowStart;
        var row = new Dictionary<string, object?>(Columns.Count);
        foreach (var col in Columns)
        {
            if (off >= data.Length)
            {
                row[col.Name] = null;
                continue;
            }

            var (value, newOff) = ReadColumn(data, off, col, dataSectionOffset);
            row[col.Name] = value is ArrayPlaceholder arr
                ? ReadArray(data, arr.Count, arr.Offset, col, dataSectionOffset)
                : FormatValue(value, col.Type);
            off = newOff;
        }
        return row;
    }

    // ═══ Column Reading ══════════════════════════════════════

    private (object? value, int newOff) ReadColumn(byte[] data, int off, ColumnDef col, int dataSecOff)
    {
        var size = Datc64Constants.ColumnSize(col, Is64Bit);
        if (off < 0 || off > data.Length - size)
            return (null, data.Length);

        if (col.IsArray)
        {
            if (Is64Bit)
            {
                var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off));
                _ = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 4));
                var arrOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 8));
                _ = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 12));
                if (count == unchecked((int)Datc64Constants.NullU32))
                    return (new ArrayPlaceholder(0, 0), off + 16);
                return (new ArrayPlaceholder(count, arrOffset), off + 16);
            }
            else
            {
                var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off));
                var arrOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 4));
                if (count == unchecked((int)Datc64Constants.NullU32))
                    return (new ArrayPlaceholder(0, 0), off + 8);
                return (new ArrayPlaceholder(count, arrOffset), off + 8);
            }
        }

        if (col.IsInterval)
        {
            var scalarColumn = col.WithIsInterval(false);
            var (min, minEnd) = ReadColumn(data, off, scalarColumn, dataSecOff);
            var (max, maxEnd) = ReadColumn(data, minEnd, scalarColumn, dataSecOff);
            return (new IntervalValue(min, max), maxEnd);
        }

        return col.Type switch
        {
            "bool" => (data[off] != 0, off + 1),
            "i8" or "byte" or "u8" => ((int)data[off], off + 1),
            "i16" or "short" => (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(off)), off + 2),
            "u16" or "ushort" => (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off)), off + 2),
            "i32" or "int" => (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off)), off + 4),
            "u32" or "uint" or "enumrow" => (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off)), off + 4),
            "f32" or "float" => (BitConverter.ToSingle(data, off), off + 4),
            "i64" or "long" => (BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(off)), off + 8),
            "u64" or "ulong" => (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off)), off + 8),
            "f64" or "double" => (BitConverter.ToDouble(data, off), off + 8),
            "string" => ReadStringColumn(data, off, dataSecOff),
            "row" => ReadRowColumn(data, off),
            "foreignrow" => ReadForeignRowColumn(data, off),
            _ => (null, off + Datc64Constants.ColumnSize(col, Is64Bit)),
        };
    }

    private (object?, int) ReadStringColumn(byte[] data, int off, int dataSecOff)
    {
        int strOffset;
        if (Is64Bit)
        {
            strOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off));
            off += 8;
        }
        else
        {
            strOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off));
            off += 4;
        }

        if (strOffset == 0) return ("", off);
        var absOff = strOffset >= 8 ? dataSecOff + (strOffset - 8) : dataSecOff;
        if (absOff >= data.Length) return ("", off);
        return (ReadUtf16String(data, absOff), off);
    }

    private (object?, int) ReadRowColumn(byte[] data, int off)
    {
        if (Is64Bit)
        {
            var val = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off));
            return val == Datc64Constants.NullU64
                ? (new RowNull(), off + 8)
                : ((object)val, off + 8);
        }
        else
        {
            var val = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            return val == Datc64Constants.NullU32
                ? (new RowNull(), off + 4)
                : ((object)val, off + 4);
        }
    }

    private (object?, int) ReadForeignRowColumn(byte[] data, int off)
    {
        if (Is64Bit)
        {
            var lo = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off));
            var hi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 8));
            return lo == Datc64Constants.NullU64 && hi == Datc64Constants.NullU64
                ? (new ForeignRowNull(), off + 16)
                : ((object)lo, off + 16);
        }
        else
        {
            var lo = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            var hi = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
            return ((ulong)hi << 32 | lo) == Datc64Constants.NullU64
                ? (new ForeignRowNull(), off + 8)
                : ((object)lo, off + 8);
        }
    }

    private static string ReadUtf16String(byte[] data, int offset)
    {
        var chars = new List<char>();
        var i = offset;
        while (i + 1 < data.Length)
        {
            var code = BitConverter.ToUInt16(data, i);
            if (code == 0) break;
            chars.Add((char)code);
            i += 2;
            if (chars.Count > 10000) break;
        }
        return new string([.. chars]);
    }

    // ═══ Array Reading ═══════════════════════════════════════

    private List<object> ReadArray(byte[] data, int count, int arrOffset, ColumnDef col, int dataSecOff)
    {
        if (count == 0) return [];

        var absOff = arrOffset >= 8 ? dataSecOff + (arrOffset - 8) : dataSecOff;
        if (absOff >= data.Length) return [];

        if (col.Type == "array")
        {
            var elemSize = Is64Bit ? 16 : 8;
            if (absOff + elemSize * count > data.Length) return [];

            var result = new List<object>();
            var off = absOff;
            for (var _ = 0; _ < count; _++)
            {
                int subCount, subArrOffset;
                if (Is64Bit)
                {
                    subCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off));
                    _ = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 4));
                    subArrOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 8));
                    _ = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 12));
                    off += 16;
                }
                else
                {
                    subCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off));
                    subArrOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 4));
                    off += 8;
                }

                if (subCount == unchecked((int)Datc64Constants.NullU32) || subCount == 0)
                {
                    result.Add(new List<object>());
                }
                else
                {
                    var subAbs = subArrOffset >= 8 ? dataSecOff + (subArrOffset - 8) : dataSecOff;
                    var subVals = new List<object>();
                    for (var j = 0; j < subCount && subAbs + (j + 1) * 4 <= data.Length; j++)
                        subVals.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(subAbs + j * 4)));
                    result.Add(subVals);
                }
            }
            return result;
        }

        var elemCol = col.WithIsArray(false).WithIsInterval(false);
        var elemColSize = Datc64Constants.ColumnSize(elemCol, Is64Bit);
        if (elemColSize == 0) return [];
        if (absOff + elemColSize * count > data.Length) return [];

        var values = new List<object>();
        var pos = absOff;
        for (var _ = 0; _ < count; _++)
        {
            if (pos + elemColSize > data.Length) { values.Add(null!); continue; }
            var (val, newPos) = ReadColumn(data, pos, elemCol, dataSecOff);
            values.Add(val is RowNull or ForeignRowNull ? null! : val!);
            pos = newPos;
        }
        return values;
    }

    private static object? FormatValue(object? val, string colType)
    {
        if (val is IntervalValue interval)
            return new List<object?>
            {
                FormatValue(interval.Min, colType),
                FormatValue(interval.Max, colType),
            };
        if (val is RowNull or ForeignRowNull)
            return null;
        if (colType is "foreignrow" or "row" && val is ulong num)
            return $"Key({num})";
        return val;
    }

    // ═══ Encode ══════════════════════════════════════════════

    public byte[] ToBytes()
    {
        var encoder = new Datc64Encoder(Schema.Columns, Is64Bit, RowLength, _trailingBytes);
        return encoder.Encode(Rows);
    }

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    public void ToJson(string path)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, JsonWriterOptions);
        writer.WriteStartArray();
        foreach (var row in Rows)
            WriteRow(writer, row);
        writer.WriteEndArray();
        writer.Flush();
    }

    // ═══ JSON Writing (AOT-safe, hand-rolled) ════════════════
    //
    // 为什么手写而不是源生成：行值是 object 的多态分发（运行时类型决定怎么写），
    // 源生成做不到；改 [JsonDerivedType] 又要给值域里每个类型打标签、读回类型还会变。
    //
    // 值域是封闭的，由 FormatValue / ReadColumn 决定：
    //   null / bool / short / ushort / int / uint / long / ulong / float / double / string
    //   / List<object>（数组列，可再嵌一层 List<object>）
    //
    // 必须逐字节复现改动前的反射式输出（否则 extract-all 的 JSON 会漂）：
    //   · 键序 = 字典枚举顺序（= 列的声明顺序），不要排序；
    //   · 数值按各自的运行时类型写（WriteNumberValue((int)v) 等）。⛔ 绝不先提升成 double：
    //     (float)0.1 一旦变 double 就是 0.100000001490116 —— 与 DatRowValueComparer 里同一个坑；
    //   · float.NaN / Infinity：由 Utf8JsonWriter 抛异常（与反射式路径同一处抛），保持抛，别改成写字符串；
    //   · 缩进与宽松转义（中文不写成 \uXXXX）由 JsonWriterOptions 统一保证。

    private static void WriteRow(Utf8JsonWriter writer, Dictionary<string, object?> row)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in row)
        {
            writer.WritePropertyName(key);
            WriteRowValue(writer, value);
        }
        writer.WriteEndObject();
    }

    private static void WriteRowValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case short n: writer.WriteNumberValue(n); break;
            case ushort n: writer.WriteNumberValue(n); break;
            case int n: writer.WriteNumberValue(n); break;
            case uint n: writer.WriteNumberValue(n); break;
            case long n: writer.WriteNumberValue(n); break;
            case ulong n: writer.WriteNumberValue(n); break;
            case float n: writer.WriteNumberValue(n); break;
            case double n: writer.WriteNumberValue(n); break;
            case string s: writer.WriteStringValue(s); break;
            case List<object> list:
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteRowValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unexpected row value type '{value.GetType()}': the hand-written writer must be kept in sync with FormatValue/ReadColumn.");
        }
    }

    public override string ToString() => $"Datc64File({TableName}, rows={Rows.Count}, 64bit={Is64Bit})";

    private readonly record struct ArrayPlaceholder(int Count, int Offset);
    private readonly record struct IntervalValue(object? Min, object? Max);
    private readonly record struct ParsedLayout(int RowCount, int RowLength, int DataSectionOffset, int SchemaSize);
    private readonly record struct RowNull;
    private readonly record struct ForeignRowNull;

    // 原先这里还有一份 JsonSerializerOptions（只设了宽松转义）供 JsonSerializer.Serialize 用；
    // 改手写 writer 后序列化选项不再需要，缩进 + 宽松转义全部由 JsonWriterOptions 承担。
    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
