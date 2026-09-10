using CommunityToolkit.Mvvm.ComponentModel;

namespace PoEToolbox.Plugins.BagCleaner.ViewModels;

/// <summary>
/// 仓库筛选矩阵中的单个格子 ViewModel。
/// IsSkipped = true 时该格在清包时跳过 (UI 显示绿色)。
/// </summary>
public partial class SkipCell : ObservableObject
{
    [ObservableProperty] private bool _isSkipped;

    /// <summary>在矩阵中的索引 (0-based, 行优先)。</summary>
    public int Index { get; }

    /// <summary>显示标签 (1-based 序号)。</summary>
    public string Label => (Index + 1).ToString();

    public SkipCell(int index, bool isSkipped = false)
    {
        Index = index;
        IsSkipped = isSkipped;
    }
}
