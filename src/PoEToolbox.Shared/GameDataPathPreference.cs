namespace PoEToolbox.Shared;

/// <summary>
/// 不变式 14（docs/ARCHITECTURE.md §4）：游戏数据没有「自动检测」这条路径，只沿用用户亲手选过一次的那一份。
/// Persists the game data path shared by modules that operate on the same client.
/// <para>
/// The rule this class exists to enforce: <b>nothing is selected until the user selects it once</b>.
/// A missing choice reads as <see langword="null"/> and every caller must ask the user instead of
/// probing the registry or the default install folders — a silently detected client is worse than no
/// client, because the user then patches a game they never pointed the tool at.
/// </para>
/// </summary>
public static class GameDataPathPreference
{
    private const string ConfigKey = "CurrentGameDataPath";

    public static string? Get()
    {
        var configuredPath = ConfigService.GetValue(ConfigKey);
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        try
        {
            return GameDataAccess.ResolvePath(configuredPath);
        }
        catch
        {
            return null;
        }
    }

    public static void Set(string path)
        => ConfigService.SetValue(ConfigKey, GameDataAccess.ResolvePath(path));
}
