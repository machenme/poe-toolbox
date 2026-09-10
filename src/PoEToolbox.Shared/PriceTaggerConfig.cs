namespace PoEToolbox.Shared;

/// <summary>
/// PriceTagger plugin configuration stored in the per-user config.json.
/// </summary>
public class PriceTaggerConfig
{
    public string? GgpkPath { get; set; }
    public bool LangSwapped { get; set; }
    public string? LeagueCache { get; set; }
    public string? LeagueCacheGame { get; set; }
    public string? LeagueCacheTime { get; set; }
}
