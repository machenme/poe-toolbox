namespace PoEToolbox.Plugins.BagCleaner.Core;

public enum CleanState
{
    /// <summary>待命，等待热键。</summary>
    Idle,

    /// <summary>正在遍历背包。</summary>
    Executing,

    /// <summary>正在中止 (Ctrl 释放中)。</summary>
    Stopping
}
