namespace PoEToolbox.Shared;

/// <summary>Persists the game data path shared by modules that operate on the same client.</summary>
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

    public static string? GetOrDetect(PoeGameKind preferredGame = PoeGameKind.Unknown)
    {
        var configuredPath = Get();
        if (configuredPath is not null
            && (preferredGame == PoeGameKind.Unknown || IsExpectedGame(configuredPath, preferredGame)))
        {
            return configuredPath;
        }

        var detectedPath = PoeDetector.Default.DetectGameDataPath(preferredGame);
        return detectedPath is null ? null : GameDataAccess.ResolvePath(detectedPath);
    }

    public static void Set(string path)
        => ConfigService.SetValue(ConfigKey, GameDataAccess.ResolvePath(path));

    private static bool IsExpectedGame(string gameDataPath, PoeGameKind expectedGame)
    {
        try
        {
            using var gameData = GameDataAccess.OpenReadOnlyMapped(gameDataPath);
            return expectedGame == PoeGameKind.Poe2
                ? gameData.IsPoe2Client
                : !gameData.IsPoe2Client;
        }
        catch
        {
            return false;
        }
        finally
        {
            MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());
        }
    }
}
