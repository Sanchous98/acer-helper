using System.Diagnostics;

namespace AcerHelper.Infrastructure.Diagnostics;

/// <summary>Always-on counters for the process-wide WMI/EC transaction gate (<c>WmiSession.Gate</c>), and the
/// two <see cref="Stopwatch"/> reads that feed them.
///
/// Deliberately NOT under <c>#if DEBUG</c>: the question these numbers answer — "do the UI-thread hardware
/// reads need deferring, and is that enough, or does the poll starve the user badly enough to want a priority
/// queue?" — has to be answered by the build the owner actually runs. A debug-only counter would measure the
/// wrong process. The cost is two <c>GetTimestamp()</c> calls and a few <c>Interlocked</c> adds against a
/// ~1-5 ms COM round-trip: a fraction of a percent.
///
/// The split into two buckets is by <see cref="Thread.IsThreadPoolThread"/> — the 3-second poll and every
/// off-thread write run on the pool, <c>BuildUi</c>/<c>ApplyStartupState</c> and user actions do not. That is
/// exactly the distinction that matters ("the poll waiting for itself" vs "the user waiting"), and it needs no
/// reference to a UI toolkit, which is what keeps this file portable. On Linux the counters simply stay zero:
/// there is no <c>WmiSession</c> there.
///
/// See docs/refactoring-plan.md, waves 5-6.</summary>
internal static class GateStats
{
    /// <summary>A hold longer than this is a "stall" — the EC is wedged, not merely busy. A UI-facing wait of
    /// 10-50 ms is tolerable (D16); past 50 ms it is worth counting separately from ordinary queueing.</summary>
    private const int StallMs = 50;

    private static readonly long StallTicks = Stopwatch.Frequency * StallMs / 1000;

    // Slot 0 is never written to: default(WaitScope)/default(HoldScope) must be inert, and default(int) is 0.
    private const int PoolSlot = 1;
    private const int NonPoolSlot = 2;

    private sealed class Bucket
    {
        public long Tx, Wait, Hold, MaxWait, MaxHold, Stalls;
    }

    private static readonly Bucket[] Buckets = [new(), new(), new()];

    /// <summary>True while this thread is inside a gate transaction. Makes the nested acquisition
    /// (<c>InvokeMethod</c> → <c>QueryFirst</c>, re-entrant on the same thread) count once: one EC
    /// transaction, one sample.</summary>
    [ThreadStatic] private static bool _inTransaction;

    /// <summary>One gate acquisition: wait, then hold. Call immediately BEFORE the <c>lock</c>; call
    /// <see cref="WaitScope.Acquired"/> as the first statement INSIDE it, and let the returned scope end with
    /// the lock block. Dispose the wait scope at the end of the method that took it.
    ///
    /// Returns an inert scope — one that records nothing — when a transaction is already in flight on this
    /// thread, so the re-entrant inner acquisition is not double-counted.</summary>
    public static WaitScope Waiting()
    {
        if (_inTransaction) return default;
        _inTransaction = true;
        var slot = Thread.CurrentThread.IsThreadPoolThread ? PoolSlot : NonPoolSlot;
        return new WaitScope(slot, Stopwatch.GetTimestamp());
    }

    /// <summary>Time spent BLOCKED acquiring the gate, and then time spent HOLDING it. Wait is the cost a
    /// UI-thread read pays for the poll; hold is the cost everything else pays for that read.</summary>
    public readonly struct WaitScope : IDisposable
    {
        private readonly int _slot;   // 0 = inert
        private readonly long _start;

        internal WaitScope(int slot, long start)
        {
            _slot = slot;
            _start = start;
        }

        /// <summary>Called inside the lock: the wait ends and the hold begins.</summary>
        public HoldScope Acquired()
            => _slot == 0 ? default : new HoldScope(_slot, Stopwatch.GetTimestamp());

        public void Dispose()
        {
            if (_slot == 0) return;   // a nested scope: the outermost one owns the sample and the flag
            var b = Buckets[_slot];
            var waited = Stopwatch.GetTimestamp() - _start;
            Interlocked.Increment(ref b.Tx);
            Interlocked.Add(ref b.Wait, waited);
            Max(ref b.MaxWait, waited);
            _inTransaction = false;
        }
    }

    public readonly struct HoldScope : IDisposable
    {
        private readonly int _slot;   // 0 = inert
        private readonly long _start;

        internal HoldScope(int slot, long start)
        {
            _slot = slot;
            _start = start;
        }

        public void Dispose()
        {
            if (_slot == 0) return;
            var b = Buckets[_slot];
            var held = Stopwatch.GetTimestamp() - _start;
            Interlocked.Add(ref b.Hold, held);
            Max(ref b.MaxHold, held);
            if (held >= StallTicks) Interlocked.Increment(ref b.Stalls);
        }
    }

    /// <summary>Counters for one bucket, read as of the call. Durations are milliseconds.</summary>
    public readonly record struct Counters(long Tx, double WaitMs, double MaxWaitMs,
                                           double HoldMs, double MaxHoldMs, long Stalls);

    /// <summary>Both buckets, read as of the call. <paramref name="NonPool"/> is the UI side.</summary>
    public readonly record struct Report(Counters Pool, Counters NonPool);

    public static Report Snapshot() => new(Read(Buckets[PoolSlot]), Read(Buckets[NonPoolSlot]));

    /// <summary>Total ticks the non-pool threads have spent inside the gate, waiting or holding. Sampled as a
    /// delta around a piece of work (see <c>AppController</c>'s startup): the difference is the hardware time
    /// that work spent on the UI thread — the number wave 6 has to move.</summary>
    public static long NonPoolTicks()
    {
        var b = Buckets[NonPoolSlot];
        return Interlocked.Read(ref b.Wait) + Interlocked.Read(ref b.Hold);
    }

    /// <summary>Zero every counter. Exists because the counters are process-global and a test that ran one
    /// transaction would otherwise leave the next one reading its numbers.
    ///
    /// <see cref="_inTransaction"/> is <c>[ThreadStatic]</c>, so this clears it for the CALLING thread only —
    /// which is all a test can be responsible for, and all it needs, provided every transaction it opens is
    /// disposed (a <c>using</c> scope does that even on throw).</summary>
    public static void Reset()
    {
        foreach (var b in Buckets)
        {
            Interlocked.Exchange(ref b.Tx, 0);
            Interlocked.Exchange(ref b.Wait, 0);
            Interlocked.Exchange(ref b.Hold, 0);
            Interlocked.Exchange(ref b.MaxWait, 0);
            Interlocked.Exchange(ref b.MaxHold, 0);
            Interlocked.Exchange(ref b.Stalls, 0);
        }
        _inTransaction = false;
    }

    private static Counters Read(Bucket b)
    {
        var maxWait = Interlocked.Read(ref b.MaxWait);
        var maxHold = Interlocked.Read(ref b.MaxHold);
        return new Counters(
            Interlocked.Read(ref b.Tx),
            Ms(Interlocked.Read(ref b.Wait)),
            Ms(maxWait),
            Ms(Interlocked.Read(ref b.Hold)),
            Ms(maxHold),
            Interlocked.Read(ref b.Stalls));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Raise <paramref name="target"/> to <paramref name="value"/> if it is larger, without a lock:
    /// contention here is rare (two buckets, a handful of threads) and a lost race only costs a retry.</summary>
    private static void Max(ref long target, long value)
    {
        for (var cur = Interlocked.Read(ref target); value > cur; cur = Interlocked.Read(ref target))
            if (Interlocked.CompareExchange(ref target, value, cur) == cur) return;
    }
}
