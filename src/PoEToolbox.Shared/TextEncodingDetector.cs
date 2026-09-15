using System.Text;

namespace PoEToolbox.Shared;

/// <summary>
/// 文本编码探测：先看 BOM，再看「无 BOM 的 UTF-16LE」。
/// 后者是外部工具重写过的游戏文件的常态（内容仍是 UTF-16LE，BOM 被丢掉），
/// 若按 UTF-8 解码会得到夹着 \0 的字符串，使所有文本匹配失效（如 uisettings.xml 找不到 &lt;/Props&gt;）。
/// </summary>
public static class TextEncodingDetector
{
    /// <returns>编码与 BOM 字节数（0 = 文件本身不带 BOM，序列化时必须保持不加）。</returns>
    public static (Encoding Encoding, int BomLength) Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);
        if (LooksLikeUtf16Le(bytes))
            return (Encoding.Unicode, 0);
        return (new UTF8Encoding(false), 0);
    }

    /// <summary>无 BOM 的 UTF-16LE 启发式判据：样本里 ≥90% 的字符高字节为 0。
    /// ASCII / 中文 UTF-8 文本的字节对几乎不可能满足该比例。</summary>
    public static bool LooksLikeUtf16Le(ReadOnlySpan<byte> bytes)
    {
        var chars = Math.Min(bytes.Length, 8192) / 2;
        if (chars < 8)
            return false;
        var highZero = 0;
        for (var i = 0; i < chars; i++)
        {
            if (bytes[i * 2 + 1] == 0)
                highZero++;
        }
        return highZero * 10 >= chars * 9;
    }
}
