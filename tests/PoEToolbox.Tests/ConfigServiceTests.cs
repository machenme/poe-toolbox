using System.IO;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 不变式 10（docs/ARCHITECTURE.md §4）：断言日志内容前先加锁取快照，不直接枚举订阅列表。
/// config.json 的容错分支。P2-5 给这两条静默回落补了日志，但当时写不出测试——
/// 路径是 static readonly，要复现就得往真实的 %LocalAppData% 里写坏文件。有了注入缝才谈得上验证。
/// </summary>
[Collection(ConfigPathTestCollection.Name)]
public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly List<(LogLevel Level, string Message)> _logged = [];
    private readonly object _loggedSync = new();

    public ConfigServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "poetoolbox-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ConfigService.DataDirectoryOverride = () => _dir;
        FileLogger.EntryLogged += OnEntry;
    }

    // EntryLogged 是进程级静态事件，任何线程记一条都会回调到这里；
    // 直接枚举原列表会在「后台补记一条」时撞上「Collection was modified」。加锁 + 取快照断言。
    private void OnEntry(LogLevel level, string message, Exception? _)
    {
        lock (_loggedSync) _logged.Add((level, message));
    }

    private (LogLevel Level, string Message)[] Logged()
    {
        lock (_loggedSync) return [.. _logged];
    }

    public void Dispose()
    {
        FileLogger.EntryLogged -= OnEntry;
        ConfigService.DataDirectoryOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class SampleConfig
    {
        public string Title { get; set; } = "";
        public int Count { get; set; }
    }

    [Fact]
    public void CorruptConfig_ReturnsEmpty_RenamesAsideAndWarns()
    {
        File.WriteAllText(ConfigService.ConfigPath, "{ 这不是 json ");

        Assert.Empty(ConfigService.ReadFullConfig());
        Assert.NotNull(ConfigService.LastReadError);

        // 坏文件必须留在原地可查，而不是被覆盖掉
        Assert.False(File.Exists(ConfigService.ConfigPath));
        var corrupt = Assert.Single(Directory.GetFiles(_dir, "config.json.corrupt.*"));
        Assert.Contains("这不是 json", File.ReadAllText(corrupt));
        Assert.Contains(Logged(), e => e.Level == LogLevel.Warn && e.Message.Contains("config.json 解析失败"));
    }

    [Fact]
    public void PluginSectionOfWrongShape_FallsBackToDefaultsAndWarns()
    {
        // 段落存在但形状不对：整段是个字符串，反序列化成 SampleConfig 会抛
        File.WriteAllText(ConfigService.ConfigPath, """{"SomePlugin": "oops"}""");

        var cfg = ConfigService.GetPluginConfig<SampleConfig>("SomePlugin");

        Assert.NotNull(cfg);
        Assert.Equal("", cfg.Title);
        Assert.Equal(0, cfg.Count);
        Assert.Contains(Logged(), e =>
            e.Level == LogLevel.Warn && e.Message.Contains("SomePlugin/SampleConfig"));
    }

    [Fact]
    public void Save_WritesUnderTheOverriddenDirectoryOnly()
    {
        ConfigService.SavePluginConfig("SomePlugin", new SampleConfig { Title = "标题", Count = 7 });

        Assert.True(File.Exists(ConfigService.ConfigPath));
        var json = File.ReadAllText(ConfigService.ConfigPath);
        Assert.Contains("\"Count\": 7", json);
        Assert.Contains("标题", json);
    }
}
