namespace PoEToolbox.Shared;

/// <summary>Identifies the Path of Exile client associated with a session value.</summary>
public enum PoeGameKind
{
    Unknown,
    Poe1,
    Poe2,
}

/// <summary>Published after a plugin identifies or selects game data.</summary>
public sealed record GameContextChanged(
    PoeGameKind Game,
    string? GameDataPath,
    bool IsPoeRunning);

/// <summary>Published after Price Tagger selects a league.</summary>
public sealed record LeagueChanged(PoeGameKind Game, string League);

/// <summary>Requests all plugins to release open GGPK/Bundles2 file handles.</summary>
public sealed record ReleaseGameDataLocksRequested(bool KeepReopenPath = true);
