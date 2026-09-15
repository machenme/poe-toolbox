using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.AffixWorkbench;

/// <summary>
/// 颜色修改对话框：新增/改色/删除方案颜色（RGBA 滑条，或直填 #RRGGBB / #AARRGGBB / R,G,B / A,R,G,B）。
/// id 是游戏内标签名（uisettings 的 Colour id + csd 包裹标签），保存前做合法性校验；id 可直接改。
/// 「新增下一级」以选中颜色为基础生成同色系、透明度 −50、id 末位数字 +1 的下一等级
/// ——对应游戏里"同一色系靠透明度区分等阶深浅"的用法。
/// 删除仍被引用的颜色时按 PRD 提示，确认后连带清理引用保持方案自洽。
/// 新颜色随「应用词缀修改」写入游戏（uisettings.xml），不需要单独操作。
/// </summary>
public partial class ColorEditorWindow : Window
{
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; } = 255;

        /// <summary>色块画刷：直接由本行 RGBA 生成，拖动滑条即时可见。</summary>
        public Brush Swatch => new SolidColorBrush(Color.FromArgb(A, R, G, B));

        public string Hex => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }

    private readonly AffixColorScheme _scheme;
    private readonly List<Row> _rows = [];
    private bool _updatingSliders;
    private bool _saved;

    public ColorEditorWindow(AffixColorScheme scheme)
    {
        _scheme = scheme;
        InitializeComponent();
        _rows.AddRange(scheme.Colors.Select(c => new Row { Id = c.Id, R = c.R, G = c.G, B = c.B, A = c.A }));
        ColorList.ItemsSource = _rows;
        SliderPanel.IsEnabled = false;
    }

    /// <summary>用户点了确定并保存成功（调用方据此刷新预览）。</summary>
    public bool Saved => _saved;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        for (var i = 1; ; i++)
        {
            var candidate = $"CustomColor{i}";
            if (_rows.Any(r => r.Id == candidate))
                continue;
            var row = new Row { Id = candidate, R = 200, G = 200, B = 60, A = 255 };
            _rows.Add(row);
            ColorList.Items.Refresh();
            ColorList.SelectedItem = row; // 新增后直接选中，省一步
            break;
        }
    }

    /// <summary>以选中颜色为基础生成下一等级：同色系、透明度 −50、id 末位数字 +1（LUK1 → LUK2）。</summary>
    private void AddLevel_Click(object sender, RoutedEventArgs e)
    {
        if (ColorList.SelectedItem is not Row row)
        {
            MessageBox.Show(this, "先在上方列表里选中一个颜色，再点「新增下一级」。", "新增下一级",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var nextId = NextLevelId(row.Id);
        if (_rows.Any(r => r.Id == nextId))
        {
            MessageBox.Show(this, $"颜色 {nextId} 已存在，请先删除它或改用其他 id。", "新增下一级",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var next = new Row
        {
            Id = nextId,
            R = row.R,
            G = row.G,
            B = row.B,
            A = row.A > 55 ? (byte)(row.A - 50) : (byte)55,
        };
        _rows.Add(next);
        ColorList.Items.Refresh();
        ColorList.SelectedItem = next;
    }

    /// <summary>id 末尾数字 +1（LUK1 → LUK2）；本身没有数字时直接追加 2。</summary>
    private static string NextLevelId(string id)
    {
        var match = Regex.Match(id, "^(.*?)(\\d+)$");
        if (!match.Success)
            return id + "2";
        var prefix = match.Groups[1].Value;
        var digits = match.Groups[2].Value;
        var next = (int.Parse(digits, CultureInfo.InvariantCulture) + 1)
            .ToString("D" + digits.Length, CultureInfo.InvariantCulture);
        return prefix + next;
    }

    private void ColorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = ColorList.SelectedItem as Row;
        SliderPanel.IsEnabled = row is not null;
        EditingLabel.Text = row is null
            ? "选中下方颜色后拖动滑条调整（或直接填 #RRGGBB / #AARRGGBB）"
            : $"正在调整：{row.Id}（{row.Hex}）";
        if (row is not null)
            SyncSliders(row);
    }

    private void SyncSliders(Row row)
    {
        _updatingSliders = true;
        try
        {
            RSlider.Value = row.R;
            GSlider.Value = row.G;
            BSlider.Value = row.B;
            ASlider.Value = row.A;
        }
        finally
        {
            _updatingSliders = false;
        }
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSliders || ColorList.SelectedItem is not Row row)
            return;
        row.R = (byte)RSlider.Value;
        row.G = (byte)GSlider.Value;
        row.B = (byte)BSlider.Value;
        row.A = (byte)ASlider.Value;
        ColorList.Items.Refresh();
    }

    private void Hex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: Row row } box)
            return;
        if (!TryParseColor(box.Text, out var a, out var r, out var g, out var b))
            return;
        row.A = a;
        row.R = r;
        row.G = g;
        row.B = b;
        if (ReferenceEquals(ColorList.SelectedItem, row))
            SyncSliders(row);
        ColorList.Items.Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row row)
            return;
        var usedByRules = _scheme.Rules.Count(r => r.ColorId == row.Id);
        var usedByAssignments = _scheme.Assignments.Count(a => a.ColorId == row.Id);
        if (usedByRules + usedByAssignments > 0)
        {
            var answer = MessageBox.Show(this,
                $"颜色 {row.Id} 正被 {usedByRules} 条规则和 {usedByAssignments} 条指派引用。删除后这些引用将被一并移除，确定继续？",
                "删除被引用的颜色", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }
        _rows.RemoveAll(r => r.Id == row.Id);
        ColorList.Items.Refresh();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var invalid = _rows.FirstOrDefault(r => !CsdDocument.ColorIdPattern.IsMatch(r.Id));
        if (invalid is not null)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"颜色标签 id 不合法：{invalid.Id}（要求字母开头，仅字母数字，≤16 字符）",
                "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var duplicated = _rows.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"颜色 id 重复：{duplicated.Key}", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var keptIds = _rows.Select(r => r.Id).ToHashSet();
        _scheme.Rules.RemoveAll(r => !keptIds.Contains(r.ColorId));
        _scheme.Assignments.RemoveAll(a => !keptIds.Contains(a.ColorId));
        _scheme.Colors = [.. _rows.Select(r => new AffixColorDef(r.Id, r.R, r.G, r.B, r.A))];
        _saved = true;
        DialogResult = true;
    }

    /// <summary>解析 #RRGGBB / #AARRGGBB / R,G,B / A,R,G,B（大小写不敏感，允许省略 #）。</summary>
    private static bool TryParseColor(string? text, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255;
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim().TrimStart('#');
        if (text.Length == 8 && text.All(Uri.IsHexDigit))
        {
            a = ParseHex(text[..2]);
            r = ParseHex(text.Substring(2, 2));
            g = ParseHex(text.Substring(4, 2));
            b = ParseHex(text[6..]);
            return true;
        }
        if (text.Length == 6 && text.All(Uri.IsHexDigit))
        {
            r = ParseHex(text[..2]);
            g = ParseHex(text.Substring(2, 2));
            b = ParseHex(text[4..]);
            return true;
        }
        var parts = text.Split(',');
        if (parts.Length == 4
            && byte.TryParse(parts[0].Trim(), out a)
            && byte.TryParse(parts[1].Trim(), out r)
            && byte.TryParse(parts[2].Trim(), out g)
            && byte.TryParse(parts[3].Trim(), out b))
            return true;
        if (parts.Length == 3
            && byte.TryParse(parts[0].Trim(), out r)
            && byte.TryParse(parts[1].Trim(), out g)
            && byte.TryParse(parts[2].Trim(), out b))
            return true;
        return false;
    }

    private static byte ParseHex(string s)
        => byte.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
