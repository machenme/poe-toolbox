using System.Net;
using System.Text;
using PoEToolbox.Core.Pipeline;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 网络失败必须变成「这一类没抓到」+ 一句用户看得懂的话，而不是英文框架异常冒泡。
/// 尤其是超时：HttpClient 抛的 <see cref="TaskCanceledException"/> 与用户主动取消同类型，
/// 一旦被当成取消往上扔，第一个分类超时就会让后面十几个分类全部不再尝试。
/// </summary>
public sealed class NetworkTimeoutMessageTests : IDisposable
{
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), "poeninja-" + Guid.NewGuid().ToString("N"));

    public NetworkTimeoutMessageTests() => Directory.CreateDirectory(_outDir);

    public void Dispose()
    {
        try { Directory.Delete(_outDir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task TimeoutOnOneCategory_DegradesAndKeepsFetchingTheRest()
    {
        var log = new List<string>();
        var handler = new StubHandler(timeoutTypes: ["Currency"]);

        var result = await PoeNinjaFetcher.FetchSelectedAsync(
            _outDir, "Essence", ["Currency", "Fragment"], new CollectingProgress(log), handler: handler);

        Assert.Equal(2, handler.RequestedTypes.Count);
        Assert.Equal(PoeNinjaFetcher.FetchStatus.Partial, result.Status);

        var failed = result.Items.Single(i => i.Category == "Currency");
        Assert.Equal(PoeNinjaFetcher.FetchStatus.Failed, failed.Status);
        Assert.Null(failed.OutputPath);
        Assert.Contains("超时", failed.Error);

        var ok = result.Items.Single(i => i.Category == "Fragment");
        Assert.Equal(PoeNinjaFetcher.FetchStatus.Succeeded, ok.Status);
        Assert.NotNull(ok.OutputPath);
        Assert.True(File.Exists(ok.OutputPath));

        Assert.Contains(log, line => line.Contains("超时"));
    }

    [Fact]
    public async Task UserCancellation_StopsImmediatelyAndBubbles()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(cancelTypes: ["Currency"], cts: cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PoeNinjaFetcher.FetchSelectedAsync(
            _outDir, "Essence", ["Currency", "Fragment"], ct: cts.Token, handler: handler));

        Assert.Equal(["Currency"], handler.RequestedTypes);
    }

    [Fact]
    public void DescribeFailure_ReportsTimeoutCancelAndHttpStatusSeparately()
    {
        Assert.Contains("超时", NetworkDefaults.DescribeFailure(new TaskCanceledException("framework text")));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal("已取消。",
            NetworkDefaults.DescribeFailure(new OperationCanceledException(cts.Token), cts.Token));

        Assert.Contains("HTTP 429", NetworkDefaults.DescribeFailure(
            new HttpRequestException("processing failure", null, HttpStatusCode.TooManyRequests)));

        Assert.Contains("无法连接", NetworkDefaults.DescribeFailure(
            new HttpRequestException("no message", new Exception("目标计算机积极拒绝，无法连接"))));

        Assert.Equal("磁盘已满。", NetworkDefaults.DescribeFailure(new IOException("磁盘已满。")));
    }

    private sealed class CollectingProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    /// <summary>按 URL 里的 type= 分派结果，让超时/取消不需要真的等 30 秒。</summary>
    private sealed class StubHandler(string[]? timeoutTypes = null, string[]? cancelTypes = null,
        CancellationTokenSource? cts = null) : HttpMessageHandler
    {
        public List<string> RequestedTypes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var type = TypeOf(request.RequestUri!.ToString());
            RequestedTypes.Add(type);

            if (timeoutTypes?.Contains(type) == true)
                return Task.FromException<HttpResponseMessage>(
                    new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."));

            if (cancelTypes?.Contains(type) == true)
            {
                cts!.Cancel();
                return Task.FromException<HttpResponseMessage>(new OperationCanceledException(cts.Token));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"lines":[{"name":"Chaos Orb"}]}""", Encoding.UTF8, "application/json"),
            });
        }

        private static string TypeOf(string url)
        {
            var start = url.IndexOf("type=", StringComparison.Ordinal) + 5;
            var end = url.IndexOf('&', start);
            return end < 0 ? url[start..] : url[start..end];
        }
    }
}
