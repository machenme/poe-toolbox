using PoEToolbox.Shared;

namespace PoEToolbox.Plugins.AffixWorkbench.Services;

/// <summary>
/// 词缀色阶（Tier 分档）的纯计算：默认梯度、按档数线性插值重建、旧版默认绿的就地升级。
///
/// 全部是不依赖界面、不碰 IO 的纯函数，因此可直接单测（见 <c>AffixRampServiceTests</c>）；
/// 调用方（<c>AffixWorkbenchView</c>）只负责拿结果刷界面与记日志。
///
/// 放在插件工程自己的 <c>Services/</c> 而不下沉 <c>Shared</c>/<c>Core</c>：色阶只服务于词缀工作台，
/// 没有跨插件复用的需求，下沉只会让共享层多一份无人消费的抽象。
/// 这条理由写在注释里，避免下一个人以为「漏了下沉」。
/// </summary>
public static class AffixRampService
{
    /// <summary>默认等级色阶：只建 T1~T4 四档，绿系明度/色调/透明度三重梯度、相邻档差距拉大
    /// （Tier1 荧光黄绿极醒目，逐档大幅变暗变深）。方案里还没有色阶时自动建这一套；
    /// 想换色去「颜色修改」改即可。</summary>
    public static IReadOnlyList<AffixColorDef> DefaultTierRamp { get; } =
    [
        new AffixColorDef("Tier1", 200, 255, 70, 255),
        new AffixColorDef("Tier2", 110, 235, 60, 240),
        new AffixColorDef("Tier3", 65, 180, 55, 215),
        new AffixColorDef("Tier4", 40, 120, 45, 185),
    ];

    /// <summary>按档数重建时的渐变终点（最深档：暗绿）。</summary>
    public static AffixColorDef DeepestTier { get; } = new("Tier", 35, 105, 45, 185);

    /// <summary>历史版默认绿的 RGB 集合：用于把早期自动创建的暗绿色阶升级为当前梯度（用户改过色的不动）。</summary>
    private static readonly HashSet<(byte R, byte G, byte B)> LegacyDefaultTierGreens =
    [
        (90, 200, 70),
        (120, 230, 80),
        (170, 255, 100),
        (110, 235, 60),
        (65, 155, 60),
    ];

    /// <summary>是否是 TierN 形式的档位 id（N 为纯数字，且至少一位）。</summary>
    public static bool IsTierId(string id)
        => id.Length > 4
           && id.StartsWith("Tier", StringComparison.Ordinal)
           && id[4..].All(char.IsDigit);

    /// <summary>该 RGB 是否是历史版默认绿（是才允许被自动升级，用户改过的色不动）。</summary>
    public static bool IsLegacyDefaultGreen(byte r, byte g, byte b)
        => LegacyDefaultTierGreens.Contains((r, g, b));

    /// <summary>以 <paramref name="anchor"/> 为起点（没有就用默认 T1），线性渐变到最深档，
    /// 生成 Tier1~TierN。生成后仍可在「颜色修改」逐档自定义。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 小于 1。</exception>
    public static IReadOnlyList<AffixColorDef> BuildTierGradient(AffixColorDef? anchor, int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "档位数至少为 1。");

        var start = anchor ?? DefaultTierRamp[0];
        var end = DeepestTier;
        var result = new List<AffixColorDef>(count);
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0d : (double)i / (count - 1);
            result.Add(new AffixColorDef(
                $"Tier{i + 1}",
                Lerp(start.R, end.R, t),
                Lerp(start.G, end.G, t),
                Lerp(start.B, end.B, t),
                Lerp(start.A, end.A, t)));
        }
        return result;
    }

    /// <summary>把按历史版默认绿自动创建的 Tier1~Tier5 就地升级为当前四档梯度（含透明度）并移除 Tier5；
    /// 用户自己改过颜色的（RGB 不是任何历史默认值）保持不动。</summary>
    /// <returns>是否有任何颜色被改动（调用方据此决定是否刷新调色板与提示）。</returns>
    public static bool TryUpgradeLegacy(IList<AffixColorDef> colors)
    {
        var changed = false;
        for (var i = colors.Count - 1; i >= 0; i--)
        {
            var def = colors[i];
            // T5 整档取消：默认梯度不再包含它（等阶 5+ 本来就不染色）
            if (def.Id == "Tier5")
            {
                if (IsLegacyDefaultGreen(def.R, def.G, def.B))
                {
                    colors.RemoveAt(i);
                    changed = true;
                }
                continue;
            }

            var tierIndex = def.Id switch
            {
                "Tier1" => 0,
                "Tier2" => 1,
                "Tier3" => 2,
                "Tier4" => 3,
                _ => -1,
            };
            if (tierIndex < 0 || !IsLegacyDefaultGreen(def.R, def.G, def.B))
                continue;

            var target = DefaultTierRamp[tierIndex];
            colors[i] = def with { R = target.R, G = target.G, B = target.B, A = target.A };
            changed = true;
        }
        return changed;
    }

    /// <summary>方案里还缺默认档位时按 <see cref="DefaultTierRamp"/> 补齐（已存在的不动）。</summary>
    /// <returns>是否新增了任何颜色。</returns>
    public static bool EnsureDefaultTierRamp(IList<AffixColorDef> colors)
    {
        var added = false;
        foreach (var def in DefaultTierRamp)
        {
            if (colors.Any(c => c.Id == def.Id))
                continue;
            colors.Add(def);
            added = true;
        }
        return added;
    }

    /// <summary>把语义别名色映射到默认色阶的某档：改默认色阶时这里自动跟随。</summary>
    /// <exception cref="ArgumentException"><paramref name="tierId"/> 不在默认色阶里。</exception>
    public static AffixColorDef TierMapped(string tierId, string aliasId)
    {
        var tier = DefaultTierRamp.FirstOrDefault(t => t.Id == tierId)
                   ?? throw new ArgumentException($"默认色阶里没有档位 {tierId}。", nameof(tierId));
        return new AffixColorDef(aliasId, tier.R, tier.G, tier.B);
    }

    private static byte Lerp(byte from, byte to, double t)
        => (byte)Math.Round(from + (to - from) * t);
}
