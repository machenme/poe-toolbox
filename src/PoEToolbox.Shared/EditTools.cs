using System.Text;
using LibDat2;

namespace PoEToolbox.Shared;

/// <summary>
/// 「国旗交换 / 语言交换」编辑逻辑的唯一实现。
/// 此前同样的代码在 CLI（lang/ui/patch 命令）与 PriceTagger 插件里各有一份且已经漂移
/// （ui 命令漏了负索引守卫、另一处把检测失败当"未劫持"），统一收口避免修一处漏两处。
/// </summary>
public static class EditTools
{
    /// <summary>
    /// 在 UIImages1.txt 的字节里交换 French 与 zhCN 国旗的 1x 图标坐标（各 26 字节）。
    /// 返回是否发生了交换；找不到标记时返回 false，绝不因 IndexOf(-1) 作为 startIndex 而抛异常。
    /// </summary>
    public static bool TrySwapFlagCoords(byte[] flagData)
    {
        var txt = Encoding.Unicode.GetString(flagData);
        var fr = txt.IndexOf("Common/FlagIcons/fr\"", StringComparison.Ordinal);
        var cn = txt.IndexOf("Common/FlagIcons/zhCN\"", StringComparison.Ordinal);
        var fc = fr < 0 ? -1 : txt.IndexOf("1.dds\" ", fr, StringComparison.Ordinal) + 7;
        var cc = cn < 0 ? -1 : txt.IndexOf("1.dds\" ", cn, StringComparison.Ordinal) + 7;
        if (fr <= 0 || cn <= fr || fc <= 7 || cc <= 7)
            return false;

        var fb = fc * 2;
        var cb = cc * 2;
        var tmp = flagData[fb..(fb + 26)].ToArray();
        Array.Copy(flagData, cb, flagData, fb, 26);
        Array.Copy(tmp, 0, flagData, cb, 26);
        return true;
    }

    /// <summary>
    /// 交换 Languages.dat 中 French 与 Traditional Chinese 两行的 Id 与 Text
    /// （PoeChinese3 方案：只换 [1][2] 字段）。返回两行的行号（frn, tch）供调用方展示；
    /// 找不到行时抛出，调用方负责把异常转成用户可读的失败信息。
    /// </summary>
    public static (int FrenchRow, int TcRow) SwapFrenchTraditionalChinese(DatContainer dat)
    {
        int frn = -1, tch = -1;
        for (var i = 0; i < dat.FieldDatas.Count; ++i)
        {
            var name = (string)dat.FieldDatas[i][1].Value;
            if (name == "French") frn = i;
            else if (name == "Traditional Chinese") tch = i;
        }
        if (frn < 0 || tch < 0)
            throw new InvalidOperationException("Languages.dat 中找不到 French / Traditional Chinese 行。");

        (dat.FieldDatas[tch][1], dat.FieldDatas[frn][1]) = (dat.FieldDatas[frn][1], dat.FieldDatas[tch][1]);
        (dat.FieldDatas[tch][2], dat.FieldDatas[frn][2]) = (dat.FieldDatas[frn][2], dat.FieldDatas[tch][2]);
        return (frn, tch);
    }
}
