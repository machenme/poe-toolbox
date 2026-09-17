using System.Text;
using System.Text.RegularExpressions;

namespace PoEToolbox.Shared;

/// <summary>
/// uisettings.xml 的颜色段编辑器：注入/剥离 <c>&lt;Colour id="X" value="r,g,b"/&gt;</c> 定义，
/// 供 csd 内的 <c>&lt;X&gt;{{...}}</X&gt;</c> 颜色标签使用（见 CsdDocument）。
/// 只重写方案拥有的 id，游戏自带颜色与其他补丁的颜色（如 efarm*）原样保留；
/// 其余文本保持不变（round-trip 测试覆盖）。
/// </summary>
public sealed class UISettingsDoc
{
    private static readonly Regex AnyColourRegex = new("<Colour\\b[^>]*\\bid=\"([^\"]+)\"[^>]*/>", RegexOptions.Compiled);

    private string _text;
    private readonly Encoding _encoding;
    private readonly byte[] _bom;

    private UISettingsDoc(string text, Encoding encoding, byte[] bom)
    {
        _text = text;
        _encoding = encoding;
        _bom = bom;
    }

    public static UISettingsDoc Parse(byte[] bytes)
    {
        var (encoding, bom) = DetectEncoding(bytes);
        return new UISettingsDoc(encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length), encoding, bom);
    }

    /// <summary>序列化回字节，编码/BOM 与解析源一致。</summary>
    public byte[] Serialize()
    {
        var payload = _encoding.GetBytes(_text);
        if (_bom.Length == 0)
            return payload;
        var result = new byte[_bom.Length + payload.Length];
        Buffer.BlockCopy(_bom, 0, result, 0, _bom.Length);
        Buffer.BlockCopy(payload, 0, result, _bom.Length, payload.Length);
        return result;
    }

    /// <summary>文件内已有的全部颜色定义（id → value 原文），供颜色修改面板导入参考。</summary>
    public IReadOnlyDictionary<string, string> GetColors()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in AnyColourRegex.Matches(_text))
        {
            var valueMatch = Regex.Match(m.Value, "value=\"([^\"]*)\"");
            dict[m.Groups[1].Value] = valueMatch.Success ? valueMatch.Groups[1].Value : "";
        }
        return dict;
    }

    /// <summary>解析一个颜色值：游戏原生 <c>r,g,b</c> 三段，或带透明度的 <c>a,r,g,b</c> 四段
    /// （第三方配色补丁普遍用四段，如 <c>255,231,179,37</c>）。id 不合法或数值不可解析时返回 null。</summary>
    public static AffixColorDef? TryParseColor(string id, string value)
    {
        if (!CsdDocument.ColorIdPattern.IsMatch(id))
            return null;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
            return null;
        byte alpha = 255;
        if (parts.Length == 4 && !byte.TryParse(parts[0], out alpha))
            return null;
        var rgb = parts.Length == 4 ? parts[1..] : parts;
        if (!byte.TryParse(rgb[0], out var r) || !byte.TryParse(rgb[1], out var g) || !byte.TryParse(rgb[2], out var b))
            return null;
        return new AffixColorDef(id, r, g, b, alpha);
    }

    /// <summary>文件内的全部颜色定义解析成颜色表（无法解析的条目跳过）。
    /// 用于把「游戏里已经存在的颜色」喂给界面预览——第三方补丁的上色靠它才能在列表里显示出来。</summary>
    public IReadOnlyList<AffixColorDef> GetColorDefs()
    {
        var defs = new List<AffixColorDef>();
        foreach (var pair in GetColors())
        {
            if (TryParseColor(pair.Key, pair.Value) is { } def)
                defs.Add(def);
        }
        return defs;
    }

    /// <summary>写入颜色定义：先剥离同 id 的既有定义，再在根元素结束标签前注入。幂等。
    /// alpha = 255 时写游戏原生的 <c>r,g,b</c> 三段格式，否则写 <c>a,r,g,b</c> 四段（游戏两种都认）。</summary>
    public void SetColors(IEnumerable<(string Id, byte R, byte G, byte B, byte A)> colors)
    {
        var list = colors as IList<(string Id, byte R, byte G, byte B, byte A)> ?? [.. colors];
        if (list.Count == 0)
            return;
        var invalid = list.FirstOrDefault(c => !CsdDocument.ColorIdPattern.IsMatch(c.Id));
        if (invalid.Id is not null)
            throw new ArgumentException($"颜色标签 id 不合法：{invalid.Id}");

        StripColors(list.Select(c => c.Id));

        var propsClose = _text.LastIndexOf("</Props>", StringComparison.Ordinal);
        if (propsClose < 0)
            throw new InvalidOperationException("uisettings.xml 缺少 </Props> 结束标签，无法注入颜色定义。");

        var injection = string.Concat(list.Select(c => c.A == 255
            ? $"<Colour id=\"{c.Id}\" value=\"{c.R},{c.G},{c.B}\"/>"
            : $"<Colour id=\"{c.Id}\" value=\"{c.A},{c.R},{c.G},{c.B}\"/>"));
        _text = _text.Insert(propsClose, injection);
    }

    /// <summary>剥离指定 id 的颜色定义。返回删除的元素数。</summary>
    public int StripColors(IEnumerable<string> ids)
    {
        var idSet = ids.Where(i => CsdDocument.ColorIdPattern.IsMatch(i)).Distinct().ToList();
        if (idSet.Count == 0)
            return 0;
        var pattern = "<Colour\\b[^>]*\\bid=\"(" + string.Join("|", idSet.Select(Regex.Escape)) + ")\"[^>]*/>";
        var count = 0;
        _text = new Regex(pattern, RegexOptions.Compiled).Replace(_text, _ => { count++; return ""; });
        return count;
    }

    private static (Encoding Encoding, byte[] Bom) DetectEncoding(byte[] bytes)
    {
        var (encoding, bomLength) = TextEncodingDetector.Detect(bytes);
        return (encoding, BomOf(encoding, bomLength));
    }

    private static byte[] BomOf(Encoding encoding, int bomLength) => bomLength switch
    {
        3 => [0xEF, 0xBB, 0xBF],
        2 => encoding.CodePage == Encoding.BigEndianUnicode.CodePage ? [0xFE, 0xFF] : [0xFF, 0xFE],
        _ => [],
    };
}
