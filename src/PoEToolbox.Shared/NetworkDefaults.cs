using System.Net.Http;

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

    /// <summary>
    /// 把网络异常翻成给用户看的一句话。
    /// 直接用 <see cref="Exception.Message"/> 有两个问题：框架给的是英文长句，
    /// 而且超时抛的 <see cref="TaskCanceledException"/> 与用户主动取消是同一个类型，
    /// 不做区分会把「网络没通」显示成「已取消」，或反过来。
    /// </summary>
    /// <param name="ct">调用方自己的取消令牌：未触发而异常是 <c>OperationCanceledException</c> 时即为超时。</param>
    public static string DescribeFailure(Exception ex, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return "已取消。";
        if (ex is TimeoutException || ex is OperationCanceledException)
            return $"请求超时（{(int)RequestTimeout.TotalSeconds} 秒内未响应），请检查网络后重试。";
        if (ex is HttpRequestException { StatusCode: { } statusCode })
            return $"服务器返回 HTTP {(int)statusCode}（{statusCode}）。";
        // 连不上时 HttpRequestException.Message 是笼统的，真正的原因在内层的 SocketException。
        if (ex is HttpRequestException && ex.InnerException is { } inner)
            return $"无法连接到服务器：{inner.Message}";
        return ex.Message;
    }
}
