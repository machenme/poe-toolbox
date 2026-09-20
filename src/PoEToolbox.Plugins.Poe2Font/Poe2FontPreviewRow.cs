using System.ComponentModel;
using System.Globalization;
using PoEToolbox.Plugins.Poe2Font;

namespace PoEToolbox.Plugins.Poe2Font;

/// <summary>预览列表里的一行 = 模板里的一个字体条目。行内的目标字号就是写盘结果。</summary>
public sealed class Poe2FontPreviewRow : INotifyPropertyChanged
{
    private readonly Action<Poe2FontPreviewRow>? _onChanged;
    private bool _applying;
    private string _edit;
    private double _previewSize;

    public Poe2FontPreviewRow(Poe2FontTemplateEntry entry, Action<Poe2FontPreviewRow>? onChanged)
    {
        Entry = entry;
        _onChanged = onChanged;
        TargetSize = entry.Size;
        _edit = entry.Size.ToString(CultureInfo.InvariantCulture);
        ScopeDisplay = Poe2FontLabels.ScopeDisplayName(entry.Scope);
        Description = Poe2FontLabels.Describe(entry.Scope, entry.Id);
        InheritsText = string.IsNullOrEmpty(entry.Inherits) ? "—" : entry.Inherits;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Poe2FontTemplateEntry Entry { get; }

    public string Key => Entry.Key;

    public string Id => Entry.Id;

    public string ScopeDisplay { get; }

    public string Description { get; }

    public string Typeface => Entry.Typeface;

    public string InheritsText { get; }

    public string SampleText => "流放者 Exile 0123456789 · 抗性 +75%";

    public int OfficialSize => Entry.Size;

    public int TargetSize { get; private set; }

    /// <summary>整行随父条目变化（自身没有 size 属性，且没被改过）。</summary>
    public bool FollowsInheritance => !Entry.HasDeclaredSize && TargetSize == OfficialSize;

    public bool IsChanged => TargetSize != OfficialSize;

    /// <summary>能否被「整体 ±N / 等比」波及：自带 size 的条目，或已被手工改过的条目。</summary>
    public bool IsBatchAffected => Entry.HasDeclaredSize || IsChanged;

    public string StatusText => IsChanged
        ? (Entry.HasDeclaredSize ? "已调整" : "已写死（原为跟随继承）")
        : FollowsInheritance ? $"跟随 {InheritsText}" : "官方值";

    public string Edit
    {
        get => _edit;
        set
        {
            if (_edit == value)
                return;

            _edit = value;
            OnPropertyChanged(nameof(Edit));
            if (_applying || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                return;

            SetTarget(Math.Clamp(size, Poe2FontService.MinFontSize, Poe2FontService.MaxFontSize));
        }
    }

    public double PreviewSize
    {
        get => _previewSize;
        private set
        {
            if (Math.Abs(_previewSize - value) < 0.01)
                return;

            _previewSize = value;
            OnPropertyChanged(nameof(PreviewSize));
        }
    }

    /// <summary>批量操作写入：数字框同步显示，但不回调变更，由批量入口统一刷新一次。</summary>
    public void ApplyBatch(int value)
    {
        _applying = true;
        Edit = value.ToString(CultureInfo.InvariantCulture);
        _applying = false;
        SetTarget(Math.Clamp(value, Poe2FontService.MinFontSize, Poe2FontService.MaxFontSize), notify: false);
    }

    public void UpdatePreviewSize(double targetResolutionWidth, double devicePixelScale, int baseResolution)
    {
        var scale = targetResolutionWidth / Math.Max(1, baseResolution) / Math.Max(0.25, devicePixelScale);
        PreviewSize = Math.Clamp(TargetSize * scale, 6, 400);
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || ScopeDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Typeface.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SetTarget(int value, bool notify = true)
    {
        if (TargetSize == value)
            return;

        var previouslyBatchAffected = IsBatchAffected;
        TargetSize = value;
        OnPropertyChanged(nameof(TargetSize));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(FollowsInheritance));
        OnPropertyChanged(nameof(IsBatchAffected));
        OnPropertyChanged(nameof(StatusText));
        if (IsBatchAffected != previouslyBatchAffected)
            OnPropertyChanged(nameof(Entry));
        if (notify)
            _onChanged?.Invoke(this);
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 批量字号操作。唯一的状态是每行的目标字号，「整体 ±N」「等比 %」「还原官方」都只是改写它的入口，
/// 界面上看到的永远是绝对数字——所以点三次 +1 稳定累加成 +3，而不会像倍率那样滚雪球。
/// </summary>
public static class Poe2FontBatch
{
    /// <summary>
    /// 整体 ±delta。只作用于自带 size 的条目和已被手工改过的条目：纯继承条目不写死字号，
    /// 它会随父条目一起变，因此整体偏移对全部条目依然生效，且不会出现二次缩放。
    /// </summary>
    public static int Shift(IEnumerable<Poe2FontPreviewRow> rows, int delta)
    {
        var affected = 0;
        foreach (var row in rows.Where(row => row.IsBatchAffected).ToList())
        {
            row.ApplyBatch(row.TargetSize + delta);
            affected++;
        }
        return affected;
    }

    /// <summary>相对官方值等比折算。结果是绝对值，所以同一倍率按两次和按一次一样。</summary>
    public static int ScalePercent(IEnumerable<Poe2FontPreviewRow> rows, double percent)
    {
        var affected = 0;
        foreach (var row in rows.Where(row => row.IsBatchAffected).ToList())
        {
            var scaled = (int)Math.Round(row.OfficialSize * percent / 100.0, MidpointRounding.AwayFromZero);
            row.ApplyBatch(scaled);
            affected++;
        }
        return affected;
    }

    public static int ResetToOfficial(IEnumerable<Poe2FontPreviewRow> rows)
    {
        var reset = rows.Count(row => row.IsChanged);
        foreach (var row in rows.Where(row => row.IsChanged).ToList())
            row.ApplyBatch(row.OfficialSize);
        return reset;
    }

    /// <summary>把行状态压成写盘用的稀疏表：只登记与官方值不同的条目。</summary>
    public static IReadOnlyDictionary<string, int> ToSizeMap(IEnumerable<Poe2FontPreviewRow> rows)
        => rows.Where(row => row.IsChanged)
            .GroupBy(row => row.Key)
            .ToDictionary(group => group.Key, group => group.First().TargetSize, StringComparer.Ordinal);
}
