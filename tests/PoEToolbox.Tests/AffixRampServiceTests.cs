using System;
using System.Collections.Generic;
using System.Linq;
using PoEToolbox.Plugins.AffixWorkbench.Services;
using PoEToolbox.Shared;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// 词缀色阶的纯计算（UI 逻辑下沉 W1 从 <c>AffixWorkbenchView</c> 抽出）：
/// 档位 id 判定、按档数线性插值重建、旧版默认绿的就地升级、语义别名映射。
/// 全是纯函数，因此不需要任何路径注入缝或串行组。
/// </summary>
public sealed class AffixRampServiceTests
{
    // ═══ IsTierId ═══

    [Theory]
    [InlineData("Tier1", true)]
    [InlineData("Tier12", true)]
    [InlineData("Tier", false)]
    [InlineData("TierX", false)]
    [InlineData("NotTier1", false)]
    [InlineData("", false)]
    public void IsTierId_只认Tier加纯数字(string id, bool expected)
        => Assert.Equal(expected, AffixRampService.IsTierId(id));

    // ═══ BuildTierGradient ═══

    [Fact]
    public void BuildTierGradient_单档时就是锚点本身()
    {
        var anchor = new AffixColorDef("Tier1", 200, 255, 70, 255);

        var gradient = AffixRampService.BuildTierGradient(anchor, 1);

        var single = Assert.Single(gradient);
        Assert.Equal("Tier1", single.Id);
        Assert.Equal((byte)200, single.R);
        Assert.Equal((byte)255, single.G);
        Assert.Equal((byte)70, single.B);
        Assert.Equal((byte)255, single.A);
    }

    [Fact]
    public void BuildTierGradient_首档等于锚点_末档等于最深档()
    {
        var anchor = new AffixColorDef("Tier1", 200, 255, 70, 255);

        var gradient = AffixRampService.BuildTierGradient(anchor, 4);

        Assert.Equal(4, gradient.Count);
        Assert.Equal(anchor, gradient[0]);
        // 末档（t = 1）完全落在渐变终点上
        Assert.Equal(AffixRampService.DeepestTier.R, gradient[^1].R);
        Assert.Equal(AffixRampService.DeepestTier.G, gradient[^1].G);
        Assert.Equal(AffixRampService.DeepestTier.B, gradient[^1].B);
        Assert.Equal(AffixRampService.DeepestTier.A, gradient[^1].A);
    }

    [Fact]
    public void BuildTierGradient_逐档变暗_单调不回头()
    {
        var anchor = new AffixColorDef("Tier1", 200, 255, 70, 255);

        var gradient = AffixRampService.BuildTierGradient(anchor, 6);

        for (var i = 1; i < gradient.Count; i++)
        {
            Assert.True(gradient[i].G <= gradient[i - 1].G, $"第 {i + 1} 档的 G 不应比上一档更亮");
            Assert.True(gradient[i].A <= gradient[i - 1].A, $"第 {i + 1} 档的 A 不应比上一档更大");
        }
        Assert.Equal("Tier6", gradient[^1].Id);
    }

    [Fact]
    public void BuildTierGradient_没有锚点时用默认第一档()
    {
        var gradient = AffixRampService.BuildTierGradient(null, 3);

        Assert.Equal(AffixRampService.DefaultTierRamp[0].R, gradient[0].R);
        Assert.Equal(AffixRampService.DefaultTierRamp[0].G, gradient[0].G);
    }

    [Fact]
    public void BuildTierGradient_档数小于1时抛异常()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AffixRampService.BuildTierGradient(null, 0));

    // ═══ TryUpgradeLegacy ═══

    [Fact]
    public void TryUpgradeLegacy_历史默认绿被升级_且Tier5整档移除()
    {
        var colors = new List<AffixColorDef>
        {
            new("Tier1", 170, 255, 100, 255),   // 历史默认绿
            new("Tier2", 120, 230, 80, 255),    // 历史默认绿
            new("Tier5", 90, 200, 70, 255),     // 历史默认绿 → 整档取消
        };

        var changed = AffixRampService.TryUpgradeLegacy(colors);

        Assert.True(changed);
        Assert.DoesNotContain(colors, c => c.Id == "Tier5");
        Assert.Equal(AffixRampService.DefaultTierRamp[0], colors.Single(c => c.Id == "Tier1"));
        Assert.Equal(AffixRampService.DefaultTierRamp[1], colors.Single(c => c.Id == "Tier2"));
    }

    [Fact]
    public void TryUpgradeLegacy_用户改过的颜色不动()
    {
        var custom = new AffixColorDef("Tier2", 12, 34, 56, 255);
        var colors = new List<AffixColorDef> { new("Tier1", 170, 255, 100, 255), custom };

        var changed = AffixRampService.TryUpgradeLegacy(colors);

        Assert.True(changed); // Tier1 被升级了
        Assert.Equal(custom, colors.Single(c => c.Id == "Tier2")); // 但用户改过的 Tier2 原样保留
    }

    [Fact]
    public void TryUpgradeLegacy_没有历史色时返回false且不改列表()
    {
        var colors = new List<AffixColorDef> { new("Dangerous", 255, 140, 0) };
        var before = colors.ToList();

        var changed = AffixRampService.TryUpgradeLegacy(colors);

        Assert.False(changed);
        Assert.Equal(before, colors);
    }

    // ═══ EnsureDefaultTierRamp ═══

    [Fact]
    public void EnsureDefaultTierRamp_补齐四档且不重复添加()
    {
        var colors = new List<AffixColorDef> { new("Tier2", 1, 2, 3) };

        Assert.True(AffixRampService.EnsureDefaultTierRamp(colors));
        Assert.Equal(4, colors.Count);
        Assert.Equal((byte)1, colors.Single(c => c.Id == "Tier2").R); // 已有的不动

        Assert.False(AffixRampService.EnsureDefaultTierRamp(colors)); // 第二次无新增
        Assert.Equal(4, colors.Count);
    }

    // ═══ TierMapped ═══

    [Fact]
    public void TierMapped_把别名映射到默认色阶的那一档()
    {
        var mapped = AffixRampService.TierMapped("Tier4", "Lucky");

        Assert.Equal("Lucky", mapped.Id);
        Assert.Equal(AffixRampService.DefaultTierRamp[3].R, mapped.R);
        Assert.Equal(AffixRampService.DefaultTierRamp[3].G, mapped.G);
        Assert.Equal(AffixRampService.DefaultTierRamp[3].B, mapped.B);
    }

    [Fact]
    public void TierMapped_未知档位抛异常()
        => Assert.Throws<ArgumentException>(() => AffixRampService.TierMapped("Tier9", "Nope"));
}
