using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PoEToolbox.Shared;

/// <summary>
/// Reclaims the memory a released game data index leaves behind.
/// </summary>
/// <remarks>
/// Disposing a <see cref="GameDataAccess"/> only closes the underlying stream. The index records,
/// path strings and directory bundle data stay on the managed heap until a gen2 collection runs —
/// for a large client that is around 900 MB, so letting go of the data has to force one, with the
/// large object heap compacted.
/// <para>
/// A single collection is not enough. Right after it the heap is down to a few MB, but the GC keeps
/// hundreds of MB committed and resident: it hands free pages back lazily, a bit more on every later
/// collection. Measured on a 3.1M-file index: 1.07 GB -> 749 MB -> 165 MB committed. So collect a few
/// times over several seconds and then trim the working set, otherwise Task Manager keeps reporting
/// the memory as in use even though nothing is holding it any more.
/// </para>
/// <para>
/// A whole chain costs several seconds and every pass is a blocking gen2 collection, so requests are
/// coalesced: at most one chain runs at a time, and a request arriving while one is running merges
/// into it (and earns one extra pass) instead of starting a second overlapping chain. That is what
/// makes it safe for <see cref="GameDataLoader"/> to ask for a reclaim after every short-lived use.
/// </para>
/// </remarks>
public static class MemoryReclaimer
{
    /// <summary>Lets the UI drop its last references (selection change, layout pass) before the first pass.</summary>
    /// <remarks>Read at every use, so tests can shorten it.</remarks>
    internal static TimeSpan FirstPassDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Pause between passes — the runtime needs a later collection to decommit the free pages.</summary>
    /// <remarks>Read at every use, so tests can shorten it.</remarks>
    internal static TimeSpan PassInterval = TimeSpan.FromMilliseconds(2500);

    /// <summary>Extra passes after the first one.</summary>
    private const int ExtraPasses = 2;

    /// <summary>Cap on passes added by requests that merged in while the chain was running.</summary>
    private const int MaxMergedPasses = 3;

    private static readonly object Gate = new();
    private static readonly List<Func<bool>> AbortChecks = [];

    private static bool _chainRunning;
    private static bool _requestedAgain;

    /// <summary>Number of chains started so far. Diagnostics and tests only.</summary>
    internal static int ChainStarts;

    /// <summary>Number of collection passes run so far. Diagnostics and tests only.</summary>
    internal static int Passes;

    /// <summary>True while a chain owns the reclaim slot.</summary>
    internal static bool IsScheduled
    {
        get { lock (Gate) return _chainRunning; }
    }

    /// <summary>
    /// Schedules a reclaim on a background thread. Safe to call from the UI thread, and safe to call
    /// repeatedly: concurrent requests merge into the chain that is already running.
    /// </summary>
    /// <param name="shouldAbort">
    /// Checked before every pass; return true to stop and leave the memory alone. Pass
    /// <see cref="GameDataAccess.CreateAbortCheck"/> to skip reclaiming while another module still
    /// holds game data open. When requests merge, every registered check is honoured: if any of them
    /// says stop, the chain stops.
    /// </param>
    public static void Reclaim(Func<bool>? shouldAbort = null)
    {
        lock (Gate)
        {
            if (shouldAbort is not null)
                AbortChecks.Add(shouldAbort);

            // A chain is already covering this: remember that one more request came in so it runs an
            // extra pass, rather than starting a second chain that overlaps it.
            if (_chainRunning)
            {
                _requestedAgain = true;
                return;
            }

            _chainRunning = true;
            _requestedAgain = false;
        }

        Interlocked.Increment(ref ChainStarts);
        _ = Task.Run(RunChainAsync);
    }

    private static async Task RunChainAsync()
    {
        try
        {
            await Task.Delay(FirstPassDelay);
            if (ShouldAbort()) return;

            CollectOnce();

            for (var pass = 0; pass < ExtraPasses; pass++)
            {
                await Task.Delay(PassInterval);
                if (ShouldAbort()) return;

                CollectOnce();
            }

            // Requests that arrived while we were collecting are served here — one extra pass each,
            // up to a bound, so a caller hammering Reclaim cannot keep the chain alive forever.
            for (var merged = 0; merged < MaxMergedPasses && TakeMergedRequest(); merged++)
            {
                await Task.Delay(PassInterval);
                if (ShouldAbort()) return;

                CollectOnce();
            }

            // Trimming with data open again would only make the new index fault its pages back in.
            if (!ShouldAbort())
                TrimWorkingSet();
        }
        finally
        {
            bool requestedAgain;
            lock (Gate)
            {
                requestedAgain = _requestedAgain;
                _requestedAgain = false;
                _chainRunning = false;

                // Keep the checks when a request is carried over; drop them when the slot is free.
                if (!requestedAgain)
                    AbortChecks.Clear();
            }

            // A request that landed while this chain was finishing would otherwise be swallowed.
            if (requestedAgain)
                Reclaim();
        }
    }

    /// <summary>True as soon as any registered check asks us to keep the memory alone.</summary>
    private static bool ShouldAbort()
    {
        Func<bool>[] checks;
        lock (Gate)
        {
            if (AbortChecks.Count == 0)
                return false;
            checks = AbortChecks.ToArray();
        }

        foreach (var check in checks)
        {
            try
            {
                if (check())
                    return true;
            }
            catch
            {
                // A misbehaving check must not stop the reclaim.
            }
        }

        return false;
    }

    /// <summary>Consumes the "another request came in" flag, if it was set.</summary>
    private static bool TakeMergedRequest()
    {
        lock (Gate)
        {
            if (!_requestedAgain)
                return false;

            _requestedAgain = false;
            return true;
        }
    }

    /// <summary>One blocking gen2 collection with the large object heap compacted.</summary>
    public static void CollectOnce()
    {
        Interlocked.Increment(ref Passes);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    /// <summary>
    /// Asks Windows to drop the process' resident pages. They are already free at this point, they
    /// are simply still mapped into the working set, which is what Task Manager shows.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(p.Handle, -1, -1);
        }
        catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);
}
