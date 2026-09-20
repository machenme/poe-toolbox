using System.IO;
using System.Text;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 游戏数据路径的唯一默认规则：<b>用户没选过就没有默认</b>。这条单独设测是因为它的失败方式最贵——
/// 一旦哪天又允许「找不到就读注册表/默认安装目录」，用户会在毫不知情的情况下把补丁打进另一份客户端。
/// 所以这里守的不是解析算法，而是「空配置读出来必须是 null」这件事。
/// </summary>
[Collection(ConfigPathTestCollection.Name)]
public sealed class GameDataPathPreferenceTests : IDisposable
{
    private const string SeedPath = "metadata/effects/spells/grd_zones/grd_burning01.ao";
    private readonly string _root;

    public GameDataPathPreferenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "poetoolbox-path-pref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ConfigService.DataDirectoryOverride = () => _root;
    }

    public void Dispose()
    {
        ConfigService.DataDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Get_BeforeAnySelection_IsNullAndDoesNotDetectAClient()
    {
        Assert.Null(GameDataPathPreference.Get());
    }

    [Fact]
    public void Set_ThenGet_ReturnsThatFile_WithNoConfigWrittenByHand()
    {
        var indexPath = SeedIndex.Build(Path.Combine(_root, "client", "Bundles2"),
            (SeedPath, Encoding.UTF8.GetBytes("{\"name\":\"loop\"}")));

        GameDataPathPreference.Set(indexPath);

        Assert.Equal(indexPath, GameDataPathPreference.Get());
        Assert.True(File.Exists(Path.Combine(_root, "config.json")));
    }

    [Fact]
    public void Get_ARememberedPathThatWentAway_IsNullRatherThanADetectedSubstitute()
    {
        var client = Path.Combine(_root, "client");
        var indexPath = SeedIndex.Build(Path.Combine(client, "Bundles2"),
            (SeedPath, Encoding.UTF8.GetBytes("{\"name\":\"loop\"}")));
        GameDataPathPreference.Set(indexPath);
        Directory.Delete(client, recursive: true);

        Assert.Null(GameDataPathPreference.Get());
    }
}
