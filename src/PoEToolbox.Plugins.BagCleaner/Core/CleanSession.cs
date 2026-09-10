namespace PoEToolbox.Plugins.BagCleaner.Core;

/// <summary>
/// 单次清包的运行时上下文 (不持久化)。
/// </summary>
public sealed class CleanSession
{
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public int ClickedCells { get; set; }
    public CleanState State { get; set; } = CleanState.Idle;
}
