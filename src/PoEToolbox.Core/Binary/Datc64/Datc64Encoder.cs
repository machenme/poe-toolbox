using System.Buffers.Binary;
using System.Text;
using PoEToolbox.Core.Schema;

namespace PoEToolbox.Core.Binary.Datc64;

/// <summary>
/// Encodes rows back to the datc64 binary format.
/// Re-encodes rows using the detected row length and preserves any bytes that
/// belong to schema-unknown trailing columns when the row count is unchanged.
/// </summary>
public sealed class Datc64Encoder
{
    private readonly IReadOnlyList<ColumnDef> _columns;
    private readonly bool _is64Bit;
    private readonly int _rowLength;
    private readonly byte[]? _trailingBytes;

    private readonly MemoryStream _varData = new();
    private int _varOffset = 8;
    private readonly Dictionary<byte[], int> _dedup = new(new BytesComparer());

    /// <param name="rowLength">
    /// Row length in bytes. If 0, computed from schema columns.
    /// Use the binary-detected value to match the original file format
    /// (e.g., PoE1 BaseItemTypes uses 374 even though schema sums to 360).
    /// </param>
    public Datc64Encoder(
        IReadOnlyList<ColumnDef> columns,
        bool is64Bit,
        int rowLength = 0,
        byte[]? trailingBytes = null)
    {
        _columns = columns;
        _is64Bit = is64Bit;
        _rowLength = rowLength > 0 ? rowLength : columns.Sum(c => Datc64Constants.ColumnSize(c, is64Bit));
        _trailingBytes = trailingBytes;
    }

    public byte[] Encode(List<Dictionary<string, object?>> rows)
    {
        var result = new MemoryStream();
        var w = new BinaryWriter(result);

        w.Write(rows.Count);

        var emptyArrayPatches = new List<ArrayPatch>();
        var rowColVarOffsets = new List<Dictionary<int, int>>();

        foreach (var (row, rowIdx) in rows.Select((r, i) => (r, i)))
        {
            var fixedStart = result.Position;
            var colVarOffsets = new Dictionary<int, int>();

            for (var colIdx = 0; colIdx < _columns.Count; colIdx++)
            {
                var col = _columns[colIdx];
                var value = row.GetValueOrDefault(col.Name);

                if (col.IsArray)
                {
                    var list = value as List<object> ?? [];
                    if (list.Count == 0)
                    {
                        emptyArrayPatches.Add(new ArrayPatch((int)result.Position, rowIdx, colIdx));
                    }
                    else
                    {
                        colVarOffsets[colIdx] = _varOffset;
                    }
                    WriteArray(w, list, col);
                }
                else
                {
                    if (col.Type == "string")
                        colVarOffsets[colIdx] = _varOffset;
                    WriteScalar(w, value, col);
                }
            }

            // Keep bytes which the current schema cannot describe. They remain
            // aligned with their original rows only while the row count matches.
            var written = (int)(result.Position - fixedStart);
            var padding = _rowLength - written;
            if (padding > 0)
            {
                var trailingOffset = rowIdx * padding;
                if (_trailingBytes is not null && _trailingBytes.Length == rows.Count * padding)
                    w.Write(_trailingBytes, trailingOffset, padding);
                else
                    w.Write(new byte[padding]);
            }

            rowColVarOffsets.Add(colVarOffsets);
        }

        // 8-byte separator
        w.Write(Datc64Constants.Separator);

        // Variable data
        w.Write(_varData.ToArray());

        // Patch empty array offsets
        var buf = result.ToArray();
        foreach (var patch in emptyArrayPatches)
        {
            var offsetVal = _varOffset;
            var found = false;

            for (var nextCol = patch.ColIdx + 1; nextCol < _columns.Count; nextCol++)
            {
                if (rowColVarOffsets[patch.RowIdx].TryGetValue(nextCol, out var nextOff))
                {
                    offsetVal = nextOff;
                    found = true;
                    break;
                }
            }

            if (!found && patch.RowIdx + 1 < rowColVarOffsets.Count)
            {
                var nextRowOffsets = rowColVarOffsets[patch.RowIdx + 1];
                if (nextRowOffsets.Count > 0)
                    offsetVal = nextRowOffsets[nextRowOffsets.Keys.Min()];
            }

            if (_is64Bit)
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(patch.Pos + 8), offsetVal);
            else
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(patch.Pos + 4), offsetVal);
        }

        return buf;
    }

    private void WriteScalar(BinaryWriter w, object? value, ColumnDef col)
    {
        if (col.IsInterval)
        {
            var scalarColumn = col.WithIsInterval(false);
            var (min, max) = GetIntervalValues(value);
            WriteScalar(w, min, scalarColumn);
            WriteScalar(w, max, scalarColumn);
            return;
        }

        switch (col.Type)
        {
            case "bool":
                w.Write((byte)(value is true ? 1 : 0));
                break;
            case "i8" or "byte":
                w.Write((byte)Convert.ToInt32(value ?? 0));
                break;
            case "u8":
                w.Write(Convert.ToByte(value ?? 0));
                break;
            case "i16" or "short":
                w.Write(Convert.ToInt16(value ?? 0));
                break;
            case "u16" or "ushort":
                w.Write(Convert.ToUInt16(value ?? 0));
                break;
            case "i32" or "int":
                w.Write(Convert.ToInt32(value ?? 0));
                break;
            case "u32" or "uint" or "enumrow":
                w.Write((int)Convert.ToUInt32(value ?? 0));
                break;
            case "f32" or "float":
                w.Write(value is float f ? f : 0f);
                break;
            case "i64" or "long":
                w.Write(Convert.ToInt64(value ?? 0));
                break;
            case "u64" or "ulong":
                w.Write((long)Convert.ToUInt64(value ?? 0));
                break;
            case "f64" or "double":
                w.Write(value is double d ? d : 0d);
                break;
            case "string":
                WriteStringValue(w, value);
                break;
            case "row":
                WriteRowValue(w, value);
                break;
            case "foreignrow":
                WriteForeignRowValue(w, value);
                break;
            default:
                var size = Datc64Constants.ColumnSize(col, _is64Bit);
                w.Write(new byte[size]);
                break;
        }
    }

    private static (object? Min, object? Max) GetIntervalValues(object? value)
    {
        if (value is System.Collections.IList values)
            return (values.Count > 0 ? values[0] : null, values.Count > 1 ? values[1] : null);
        return (null, null);
    }

    private void WriteStringValue(BinaryWriter w, object? value)
    {
        var s = value as string ?? "";

        // Empty strings use offset=0 (null sentinel), matching original format.
        if (s.Length == 0)
        {
            if (_is64Bit)
            {
                w.Write(0);
                w.Write(0);
            }
            else
            {
                w.Write(0);
            }
            return;
        }

        var encoded = Encoding.Unicode.GetBytes(s);
        var withNull = new byte[encoded.Length + 2];
        Array.Copy(encoded, withNull, encoded.Length);

        var offset = AllocVar(withNull, dedup: true, pad: 2);

        if (_is64Bit)
        {
            w.Write(offset);
            w.Write(0);
        }
        else
        {
            w.Write(offset);
        }
    }

    private void WriteRowValue(BinaryWriter w, object? value)
    {
        var keyVal = Datc64Constants.ParseKey(value);
        if (keyVal.HasValue)
        {
            if (keyVal.Value == Datc64Constants.NullKey)
            {
                if (_is64Bit) w.Write(Datc64Constants.NullU64);
                else w.Write(unchecked((int)Datc64Constants.NullU32));
            }
            else
            {
                if (_is64Bit) w.Write(keyVal.Value);
                else w.Write((int)keyVal.Value);
            }
        }
        else if (value is ulong ul)
        {
            if (_is64Bit) w.Write(ul);
            else w.Write((int)ul);
        }
        else
        {
            if (_is64Bit) w.Write(Datc64Constants.NullU64);
            else w.Write(unchecked((int)Datc64Constants.NullU32));
        }
    }

    private void WriteForeignRowValue(BinaryWriter w, object? value)
    {
        var keyVal = Datc64Constants.ParseKey(value);
        if (keyVal.HasValue)
        {
            if (keyVal.Value == Datc64Constants.NullKey)
            {
                if (_is64Bit) { w.Write(unchecked((long)Datc64Constants.NullU64)); w.Write(unchecked((long)Datc64Constants.NullU64)); }
                else { w.Write(unchecked((int)Datc64Constants.NullU32)); w.Write(unchecked((int)Datc64Constants.NullU32)); }
            }
            else
            {
                if (_is64Bit) { w.Write((long)keyVal.Value); w.Write(0L); }
                else { w.Write((int)keyVal.Value); w.Write(0); }
            }
        }
        else
        {
            if (_is64Bit) { w.Write(unchecked((long)Datc64Constants.NullU64)); w.Write(unchecked((long)Datc64Constants.NullU64)); }
            else { w.Write(unchecked((int)Datc64Constants.NullU32)); w.Write(unchecked((int)Datc64Constants.NullU32)); }
        }
    }

    private void WriteArray(BinaryWriter w, List<object> list, ColumnDef col)
    {
        if (list.Count == 0)
        {
            w.Write(new byte[_is64Bit ? 16 : 8]);
            return;
        }

        if (col.Type == "array")
        {
            var subDescs = new List<(int Count, int Ptr)>();
            foreach (var sub in list)
            {
                if (sub is List<object> subList && subList.Count > 0)
                {
                    var elemData = new byte[subList.Count * 4];
                    for (var i = 0; i < subList.Count; i++)
                        BinaryPrimitives.WriteInt32LittleEndian(elemData.AsSpan(i * 4), Convert.ToInt32(subList[i]));
                    var elemOffset = AllocVar(elemData);
                    subDescs.Add((subList.Count, elemOffset));
                }
                else
                {
                    subDescs.Add((0, _varOffset));
                }
            }

            var descData = new byte[subDescs.Count * (_is64Bit ? 16 : 8)];
            for (var i = 0; i < subDescs.Count; i++)
            {
                var (cnt, ptr) = subDescs[i];
                var off = i * (_is64Bit ? 16 : 8);
                if (_is64Bit)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(descData.AsSpan(off), cnt);
                    BinaryPrimitives.WriteInt32LittleEndian(descData.AsSpan(off + 8), ptr != 0 ? ptr : _varOffset);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(descData.AsSpan(off), cnt);
                    BinaryPrimitives.WriteInt32LittleEndian(descData.AsSpan(off + 4), ptr != 0 ? ptr : _varOffset);
                }
            }
            var descOffset = AllocVar(descData);

            if (_is64Bit)
            {
                w.Write(list.Count); w.Write(0); w.Write(descOffset); w.Write(0);
            }
            else
            {
                w.Write(list.Count); w.Write(descOffset);
            }
        }
        else
        {
            var elemCol = col.WithIsArray(false).WithIsInterval(false);
            var elemData = new MemoryStream();
            foreach (var elem in list)
                WriteScalarToStream(elemData, elem, elemCol);
            var elemBytes = elemData.ToArray();
            var arrOffset = AllocVar(elemBytes);

            if (_is64Bit)
            {
                w.Write(list.Count); w.Write(0); w.Write(arrOffset); w.Write(0);
            }
            else
            {
                w.Write(list.Count); w.Write(arrOffset);
            }
        }
    }

    private void WriteScalarToStream(MemoryStream ms, object? value, ColumnDef col)
    {
        Span<byte> span = stackalloc byte[16];
        int written;

        switch (col.Type)
        {
            case "bool": span[0] = (byte)(value is true ? 1 : 0); written = 1; break;
            case "i8" or "byte" or "u8": span[0] = Convert.ToByte(value ?? 0); written = 1; break;
            case "i16" or "short": BinaryPrimitives.WriteInt16LittleEndian(span, Convert.ToInt16(value ?? 0)); written = 2; break;
            case "u16" or "ushort": BinaryPrimitives.WriteUInt16LittleEndian(span, Convert.ToUInt16(value ?? 0)); written = 2; break;
            case "i32" or "int": BinaryPrimitives.WriteInt32LittleEndian(span, Convert.ToInt32(value ?? 0)); written = 4; break;
            case "u32" or "uint" or "enumrow": BinaryPrimitives.WriteUInt32LittleEndian(span, Convert.ToUInt32(value ?? 0)); written = 4; break;
            case "f32" or "float": BinaryPrimitives.WriteSingleLittleEndian(span, value is float f ? f : 0f); written = 4; break;
            case "i64" or "long": BinaryPrimitives.WriteInt64LittleEndian(span, Convert.ToInt64(value ?? 0)); written = 8; break;
            case "u64" or "ulong": BinaryPrimitives.WriteUInt64LittleEndian(span, Convert.ToUInt64(value ?? 0)); written = 8; break;
            case "f64" or "double": BinaryPrimitives.WriteDoubleLittleEndian(span, value is double d ? d : 0d); written = 8; break;
            case "foreignrow":
                {
                    var keyVal = Datc64Constants.ParseKey(value);
                    ulong v = keyVal ?? (value is ulong ul ? ul : Datc64Constants.NullKey);
                    if (_is64Bit)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(span, v);
                        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], v == Datc64Constants.NullKey ? Datc64Constants.NullU64 : 0);
                        written = 16;
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)v);
                        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], v == Datc64Constants.NullKey ? Datc64Constants.NullU32 : 0);
                        written = 8;
                    }
                }
                break;
            case "row":
                {
                    var keyVal = Datc64Constants.ParseKey(value);
                    ulong v = keyVal ?? (value is ulong ul ? ul : (value is long l ? (ulong)l : Convert.ToUInt64(value ?? 0)));
                    if (_is64Bit) { BinaryPrimitives.WriteUInt64LittleEndian(span, v); written = 8; }
                    else { BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)v); written = 4; }
                }
                break;
            default:
                written = Datc64Constants.ColumnSize(col, _is64Bit);
                span[..written].Clear();
                break;
        }

        ms.Write(span[..written]);
    }

    // ═══ Variable data helpers ═══════════════════════════════

    private int AllocVar(byte[] data, bool dedup = false, int pad = 0)
    {
        if (dedup && _dedup.TryGetValue(data, out var existing))
            return existing;

        var offset = _varOffset;
        _varData.Write(data);
        _varOffset += data.Length;
        if (pad > 0)
        {
            _varData.Write(new byte[pad]);
            _varOffset += pad;
        }
        if (dedup)
            _dedup[data] = offset;
        return offset;
    }

    private readonly record struct ArrayPatch(int Pos, int RowIdx, int ColIdx);

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (x == null || y == null) return x == y;
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            var hc = new HashCode();
            hc.AddBytes(obj);
            return hc.ToHashCode();
        }
    }
}
