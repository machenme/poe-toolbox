using System.Diagnostics;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 临时接管 <see cref="Debug.Fail"/>：既不让它把用例炸掉，又留下「它到底响没响」的证据。
///
/// 为什么接管得到：.NET 里 <c>Debug.Fail</c> 是走 <see cref="Trace.Listeners"/> 派发的，
/// 测试主机在里面挂了一个 <c>TestHostTraceListener</c>，由它把 Fail 翻成
/// <c>DebugAssertException</c>、用例才失败。换掉监听器就换掉了这层翻译。
///
/// 为什么这样做是安全的：<see cref="Trace.Listeners"/> 是进程级静态，「接管—还原」这段窗口
/// 里别的测试若也在记 trace 就会被吞掉。本程序集已整体关并行（见 <c>AssemblyInfo.cs</c>），
/// 所以窗口内只有当前用例在跑。**若将来重新开启并行，本类必须挂进相应的串行 collection。**
/// </summary>
internal sealed class CapturedDebugFail : IDisposable
{
    private readonly TraceListener[] _saved;
    private readonly RecordingListener _recorder = new();

    public CapturedDebugFail()
    {
        _saved = [.. Trace.Listeners.Cast<TraceListener>()];
        Trace.Listeners.Clear();
        Trace.Listeners.Add(_recorder);
    }

    /// <summary>断言守卫确实响了。Release 下 <c>Debug.Fail</c> 整个被编译掉，故只在 Debug 下成立。</summary>
    public void AssertFired(string substring)
    {
#if DEBUG
        Assert.Contains(_recorder.Messages, m => m.Contains(substring, StringComparison.Ordinal));
#endif
    }

    public void Dispose()
    {
        Trace.Listeners.Clear();
        foreach (var listener in _saved)
            Trace.Listeners.Add(listener);
    }

    private sealed class RecordingListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? value) { }
        public override void WriteLine(string? value) { }

        public override void Fail(string? message) => Messages.Add(message ?? "");
        public override void Fail(string? message, string? detailMessage) => Messages.Add(message ?? "");
    }
}
