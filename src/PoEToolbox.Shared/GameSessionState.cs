using PoEToolbox.Sdk;

namespace PoEToolbox.Shared;

/// <summary>Read-only session state shared by the application shell.</summary>
public interface IAppState
{
    PoeGameKind Game { get; }
    string? GameDataPath { get; }
    bool IsPoeRunning { get; }
    PoeGameKind LeagueGame { get; }
    string? CurrentLeague { get; }
}

/// <summary>
/// Keeps the latest game context and league published by built-in plugins.
/// It intentionally does not poll processes or persist state.
/// </summary>
public sealed class GameSessionState : IAppState, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly object _lock = new();
    private PoeGameKind _game;
    private string? _gameDataPath;
    private bool _isPoeRunning;
    private PoeGameKind _leagueGame;
    private string? _currentLeague;

    public GameSessionState(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _eventBus.Subscribe<GameContextChanged>(OnGameContextChanged);
        _eventBus.Subscribe<LeagueChanged>(OnLeagueChanged);
    }

    public PoeGameKind Game { get { lock (_lock) return _game; } }
    public string? GameDataPath { get { lock (_lock) return _gameDataPath; } }
    public bool IsPoeRunning { get { lock (_lock) return _isPoeRunning; } }
    public PoeGameKind LeagueGame { get { lock (_lock) return _leagueGame; } }
    public string? CurrentLeague { get { lock (_lock) return _currentLeague; } }

    private void OnGameContextChanged(GameContextChanged context)
    {
        lock (_lock)
        {
            _game = context.Game;
            _gameDataPath = context.GameDataPath;
            _isPoeRunning = context.IsPoeRunning;
            if (_leagueGame != PoeGameKind.Unknown && context.Game != PoeGameKind.Unknown
                && _leagueGame != context.Game)
            {
                _leagueGame = PoeGameKind.Unknown;
                _currentLeague = null;
            }
        }
    }

    private void OnLeagueChanged(LeagueChanged league)
    {
        lock (_lock)
        {
            _leagueGame = league.Game;
            _currentLeague = league.League;
        }
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GameContextChanged>(OnGameContextChanged);
        _eventBus.Unsubscribe<LeagueChanged>(OnLeagueChanged);
    }
}
