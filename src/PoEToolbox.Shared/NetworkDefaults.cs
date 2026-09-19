namespace PoEToolbox.Shared;

/// <summary>
/// 所有对外 HTTP 调用的超时默认值。
/// </summary>
/// <remarks>
/// 库里若干 <c>HttpClient</c> 曾经用 <see cref="Timeout.InfiniteTimeSpan"/>：网络挂住时操作永不返回，
/// 界面只能靠用户杀进程。这里给出统一上限，让「网络不对」变成一次可以提示的失败。
/// 需要别的时长的调用方可以显式传入自己的值（例如补丁下载按文件覆盖）。
/// </remarks>
public static class NetworkDefaults
{
    /// <summary>单次请求的整体超时。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>建立连接的超时：比整体超时短，便于在网络不通时快速失败。</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
}
