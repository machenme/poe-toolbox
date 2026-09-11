using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PoEToolbox.Shared;

/// <summary>How the caller intends to use the data — picks the underlying open strategy.</summary>
public enum GameDataMode
{
    /// <summary>
    /// Preferred strategy for read-only work: a Bundles2 index is memory-mapped with a read-only
    /// handle. This is a <em>preference</em>, not a guarantee — a .ggpk still goes through
    /// <see cref="GameDataAccess.OpenReadOnlyMapped"/>, which opens the container with GGPK's own
    /// read/write file access. Do not rely on this mode to keep the file unlocked.
    /// </summary>
    Read,

    /// <summary>
    /// The caller writes through the handle. Note that saving is not implicit:
    /// <see cref="GameDataAccess.Index"/> writes of the <c>Write</c> kind need an explicit
    /// <c>Save()</c>, while <c>CopyFileAs</c> / <c>AddFile</c> already persist on their own.
    /// </summary>
    ReadWrite,
}

/// <summary>
/// Owns the lifecycle of a short-lived <see cref="GameDataAccess"/>: resolve the path, open it off
/// the caller's thread, hand it to a callback, then dispose it.
/// </summary>
/// <remarks>
/// Opening a real client costs seconds and around 1 GB of managed memory, so doing it on a UI
/// thread freezes the window. That is why <see cref="UseAsync"/> runs the <em>whole</em> scope on a
/// background thread — opening, the callback's own parsing/writing, and disposal — rather than only
/// wrapping the open call.
/// <para>
/// Every overload also schedules a <see cref="MemoryReclaimer.Reclaim(Func{bool})"/> once the data is
/// closed, so the ~1 GB an index costs does not sit on the heap until the next chance collection.
/// That is safe to do per call because <c>MemoryReclaimer</c> coalesces: repeated uses merge into one
/// chain, and the chain stops as soon as another module has game data open.
/// </para>
/// </remarks>
public static class GameDataLoader
{
    /// <summary>
    /// Resolves a path the same way <see cref="GameDataAccess.Open"/> does, and falls back to
    /// auto-detection when none is given.
    /// </summary>
    /// <param name="path">
    /// A file (Content.ggpk / _.index.bin), a directory containing one, or null/blank to detect.
    /// </param>
    /// <exception cref="FileNotFoundException">No path given and nothing could be detected.</exception>
    public static string ResolvePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return GameDataAccess.ResolvePath(path);

        var detected = PoeDetector.Default.DetectGameDataPath()
            ?? throw new FileNotFoundException(
                "No game data path was given and none could be detected.");

        return GameDataAccess.ResolvePath(detected);
    }

    /// <summary>
    /// Opens the data on a background thread, runs <paramref name="use"/>, then disposes it.
    /// Nothing in the scope touches the calling thread, so a UI caller stays responsive.
    /// </summary>
    /// <param name="use">
    /// Runs on a background thread — <b>it must not touch the UI</b>. Collect what happened and
    /// return it; the caller is back on its own context after the await and can update controls,
    /// log to the output box, and so on.
    /// </param>
    public static async Task UseAsync(
        string? path,
        GameDataMode mode,
        Func<GameDataAccess, CancellationToken, Task> use,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(use);
        var resolved = ResolvePath(path);

        await Task.Run(async () =>
        {
            var gd = Open(resolved, mode);
            try
            {
                await use(gd, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gd.Dispose();
                RequestReclaim();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="UseAsync(string?, GameDataMode, Func{GameDataAccess, CancellationToken, Task}, CancellationToken)"/>
    public static async Task<T> UseAsync<T>(
        string? path,
        GameDataMode mode,
        Func<GameDataAccess, CancellationToken, Task<T>> use,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(use);
        var resolved = ResolvePath(path);

        return await Task.Run(async () =>
        {
            var gd = Open(resolved, mode);
            try
            {
                return await use(gd, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gd.Dispose();
                RequestReclaim();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous variant for callers that already run off the UI thread (the CLI, services).
    /// </summary>
    public static T Use<T>(string? path, GameDataMode mode, Func<GameDataAccess, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);

        var gd = Open(ResolvePath(path), mode);
        try
        {
            return use(gd);
        }
        finally
        {
            gd.Dispose();
            RequestReclaim();
        }
    }

    /// <summary>
    /// Synchronous variant for callers that already run off the UI thread (the CLI, services).
    /// </summary>
    public static void Use(string? path, GameDataMode mode, Action<GameDataAccess> use)
    {
        ArgumentNullException.ThrowIfNull(use);

        var gd = Open(ResolvePath(path), mode);
        try
        {
            use(gd);
        }
        finally
        {
            gd.Dispose();
            RequestReclaim();
        }
    }

    /// <summary>
    /// Asks for the memory back now that the data is closed. Only fires when the open actually
    /// happened, and the guard keeps another module's open index out of the collection.
    /// </summary>
    private static void RequestReclaim()
        => MemoryReclaimer.Reclaim(GameDataAccess.CreateAbortCheck());

    /// <summary>
    /// Opens data the caller keeps and disposes itself (the data browser, which releases on a
    /// button click). Parsing still happens off the calling thread.
    /// </summary>
    public static Task<GameDataAccess> OpenAsync(
        string? path,
        GameDataMode mode,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        return Task.Run(() => Open(resolved, mode), cancellationToken);
    }

    private static GameDataAccess Open(string path, GameDataMode mode) => mode switch
    {
        GameDataMode.Read => GameDataAccess.OpenReadOnlyMapped(path),
        GameDataMode.ReadWrite => GameDataAccess.Open(path),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
