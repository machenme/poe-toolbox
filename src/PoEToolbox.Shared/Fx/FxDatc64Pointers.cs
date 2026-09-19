using System.Text;

namespace PoEToolbox.Shared;

/// <summary>
/// datc64 表的原始字节操作：解析布局、按 Id 定位指针字段、改指目标字符串。
/// 指针公式 <c>abs = v + dataOffset - 8</c>（8 字节指针是相对 0xBB×8 分隔符的偏移）已实测验证。
/// </summary>
/// <remarks>
/// 从 <see cref="FxPatchEngine"/> 分出来的一块：纯字节运算，不碰索引、不碰文件系统。
/// 项目自带的 <see cref="Datc64File"/> 解析器对部分表失败，所以这里走原始字节。
/// </remarks>
internal static class FxDatc64Pointers
{
    internal sealed record DatLayout(int RowCount, int RowLen, int SepPos, int DataOffset);

    internal sealed record PtrField(int Row, int FieldOffset, PatchState State, string CurrentValue, bool Relative);

    internal static DatLayout ParseLayout(byte[] data)
    {
        var rowCount = BitConverter.ToInt32(data, 0);
        if (rowCount <= 0)
            throw new InvalidOperationException($"datc64 行数非法（{rowCount}）——表结构不认识。");
        var best = 0;
        var sepPos = -1;
        for (var i = 4; i <= data.Length - 8; i++)
        {
            if (data[i] != 0xBB || data[i + 1] != 0xBB || data[i + 2] != 0xBB || data[i + 3] != 0xBB
                || data[i + 4] != 0xBB || data[i + 5] != 0xBB || data[i + 6] != 0xBB || data[i + 7] != 0xBB)
                continue;
            var fixedSize = i - 4;
            if (fixedSize > 0 && fixedSize % rowCount == 0 && fixedSize / rowCount > best)
            {
                best = fixedSize / rowCount;
                sepPos = i;
            }
        }
        if (sepPos < 0)
            throw new InvalidOperationException("无法定位 datc64 分隔符（0xBB×8）——表结构不认识。");
        return new DatLayout(rowCount, best, sepPos, sepPos + 8);
    }

    /// <summary>解码 start 处的 UTF-16LE 字符串。必须按 2 字节步进找 NUL-NUL 终止符——
    /// 按单字节步进会在字符第二个 0x00 与终止符首字节处误判，产生奇数字节截断（U+FFFD）。</summary>
    internal static string DecodeUtf16At(byte[] data, int start)
    {
        var end = start;
        while (end + 1 < data.Length && !(data[end] == 0 && data[end + 1] == 0))
            end += 2;
        return Encoding.Unicode.GetString(data, start, end - start);
    }

    /// <summary>在 blob 中找与 value 精确相等的 UTF-16LE 字符串偏移（无 BOM）。</summary>
    private static int FindExactString(byte[] data, int dataOffset, string value)
    {
        var needle = Encoding.Unicode.GetBytes(value);
        for (var i = dataOffset; i + needle.Length <= data.Length; i++)
        {
            if (!data.AsSpan(i, needle.Length).SequenceEqual(needle))
                continue;
            var start = i;
            while (start >= dataOffset + 2 && !(data[start - 1] == 0 && data[start - 2] == 0))
                start -= 2;
            if (DecodeUtf16At(data, start) == value)
                return start;
        }
        return -1;
    }

    private static (long Value, bool Relative)? PointerAt(byte[] data, int pos, DatLayout layout, string expected)
    {
        var v = BitConverter.ToInt64(data, pos);
        var absRel = (long)v + layout.DataOffset - 8;
        var absAbs = (long)v;
        if (absRel >= layout.DataOffset && absRel < data.Length
            && DecodeUtf16At(data, (int)absRel) == expected)
            return (v, true);
        if (absAbs >= layout.DataOffset && absAbs < data.Length
            && DecodeUtf16At(data, (int)absAbs) == expected)
            return (v, false);
        return null;
    }

    /// <summary>
    /// 按 Id 精确匹配定位行，再找行内解码 == originalPath/newPath 的指针字段。
    /// Id 字符串可能被多行/多位置引用（实测 miscanimated 中 BaseOilGroundBurningEffect 有 2 处引用），
    /// 因此 id 引用与 AOFile 字段是联合约束：逐个引用行验证，取同时满足两者的行。
    /// 若多行都满足联合约束（Id 是跨界解码碎片等），定位不唯一 → 返回 null（拒绝歧义定位）。
    /// </summary>
    internal static PtrField? LocatePtrField(byte[] data, DatLayout layout, string id, string originalPath, string newPath)
    {
        PtrField? found = null;
        for (var q = 4; q + 8 <= layout.SepPos; q++)
        {
            var v = BitConverter.ToInt64(data, q);
            var absRel = (long)v + layout.DataOffset - 8;
            if (absRel < layout.DataOffset || absRel >= data.Length)
                continue;
            if (DecodeUtf16At(data, (int)absRel) != id)
                continue;
            var row = (q - 4) / layout.RowLen;
            if (row >= layout.RowCount)
                continue;

            var rowStart = 4 + row * layout.RowLen;
            for (var g = 0; g + 8 <= layout.RowLen; g++)
            {
                var pos = rowStart + g;
                if (PointerAt(data, pos, layout, originalPath) is { } hitOld)
                {
                    if (found is not null)
                        return null; // 歧义：多行同时满足联合约束，拒绝定位
                    found = new PtrField(row, g, PatchState.NotApplied, originalPath, hitOld.Relative);
                    break;
                }
                if (PointerAt(data, pos, layout, newPath) is { } hitNew)
                {
                    if (found is not null)
                        return null;
                    found = new PtrField(row, g, PatchState.Applied, newPath, hitNew.Relative);
                    break;
                }
            }
            // 该行没有匹配的 AOFile 字段 → 继续找下一个 id 引用行
        }
        return found;
    }

    /// <summary>
    /// 把指针改指 targetString（存在则复用，缺失则追加）。
    /// 只修改定位到的字段，不重写其他行的 foreign reference。
    /// 这样 apply/revert 都是严格成对的单字段变换，不会把其他行改成不可逆的哨兵引用。
    /// </summary>
    internal static (byte[] Data, bool Changed) RepointString(byte[] data, DatLayout layout, PtrField field, string targetString)
    {
        var targetAbs = FindExactString(data, layout.DataOffset, targetString);
        if (targetAbs < 0)
        {
            // Preserve the old EOF as an empty-string sentinel. Some foreign rows may
            // legitimately point at that offset; appending the target directly would
            // silently retarget those rows and make apply/revert non-reversible.
            targetAbs = data.Length + 2;
            var strBytes = Encoding.Unicode.GetBytes(targetString);
            var append = new byte[strBytes.Length + 4]; // empty sentinel + string + NUL
            strBytes.CopyTo(append, 2);
            var bigger = new byte[data.Length + append.Length];
            Array.Copy(data, bigger, data.Length);
            Array.Copy(append, 0, bigger, data.Length, append.Length);
            data = bigger;
            FxPatchEngine.Log($"[ptr] blob 追加空串哨兵 + \"{targetString}\" @0x{targetAbs:X}（+{append.Length} B）");
        }

        var rowStart = 4 + field.Row * layout.RowLen;
        var pos = rowStart + field.FieldOffset;
        var newValue = field.Relative
            ? targetAbs - (layout.DataOffset - 8)
            : targetAbs;
        var oldValue = BitConverter.ToInt64(data, pos);
        var changed = false;
        if (oldValue != newValue)
        {
            BitConverter.GetBytes(newValue).CopyTo(data, pos);
            FxPatchEngine.Log($"[ptr] ROW[{field.Row}] +{field.FieldOffset}: 0x{oldValue:X} -> 0x{newValue:X} (\"{targetString}\")");
            changed = true;
        }

        return (data, changed);
    }
}
