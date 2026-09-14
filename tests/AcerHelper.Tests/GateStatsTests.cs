using System.Globalization;
using AcerHelper.Infrastructure.Diagnostics;

namespace AcerHelper.Tests;

/// <summary>Covers the wave-5 instrumentation itself: a counter nobody has tested measures an unknown
/// quantity, and these numbers are meant to decide whether the hardware actor gets built at all.
///
/// Bucket assertions run on threads the test owns (a <see cref="Task.Run(Action)"/> for the pool, a raw
/// <see cref="Thread"/> for the UI side) rather than on xUnit's own thread, so "which bucket" is not a guess
/// about the runner. Claims that do not care about the bucket assert on the SUM of both, so unrelated gate
/// activity on another thread cannot make them flaky.
///
/// NOTE ON THE RELEASE CLAIM: that these counters are compiled unconditionally — not under <c>#if DEBUG</c>,
/// which would measure a build the owner never runs — cannot be asserted in Debug. It is enforced by
/// construction instead: if the type were conditionally compiled, the Release test build would not compile
/// because of this file. So "the counters are on in Release" is verified by running
/// <c>dotnet test -c Release</c>, not by a fact here.</summary>
public class GateStatsTests
{
    /// <summary>The exact shape <c>WmiSession</c> uses: time the wait outside the lock, the hold inside it.</summary>
    private static void Transaction(Action? insideLock = null)
    {
        using var wait = GateStats.Waiting();
        using var hold = wait.Acquired();
        insideLock?.Invoke();
    }

    private static long TotalTx(in GateStats.Report r) => r.Pool.Tx + r.NonPool.Tx;
    private static long TotalStalls(in GateStats.Report r) => r.Pool.Stalls + r.NonPool.Stalls;

    private static void OnPoolThread(Action body) => Task.Run(body).GetAwaiter().GetResult();

    private static void OnDedicatedThread(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { failure = e; } });
        t.Start();
        t.Join();
        if (failure != null) throw failure;
    }

    [Fact]
    public void OneAcquisition_RecordsExactlyOneTransaction()
    {
        var before = GateStats.Snapshot();
        Transaction();
        var after = GateStats.Snapshot();

        Assert.Equal(1, TotalTx(after) - TotalTx(before));
    }

    [Fact]
    public void Buckets_SeparateThePollFromTheUiThread()
    {
        var before = GateStats.Snapshot();

        OnPoolThread(() => Transaction());          // the 3-second poll and every off-thread write
        OnDedicatedThread(() => Transaction());     // BuildUi / a user action

        var after = GateStats.Snapshot();
        Assert.Equal(1, after.Pool.Tx - before.Pool.Tx);
        Assert.Equal(1, after.NonPool.Tx - before.NonPool.Tx);
    }

    [Fact]
    public void NonPoolTicks_IgnoresPoolWorkAndGrowsOnTheUiSide()
    {
        var before = GateStats.NonPoolTicks();

        OnPoolThread(() => Transaction());
        Assert.Equal(before, GateStats.NonPoolTicks());

        OnDedicatedThread(() => Transaction());
        Assert.True(GateStats.NonPoolTicks() > before);
    }

    [Fact]
    public void NestedAcquisition_IsOneTransactionAndDoesNotLeakTheFlag()
    {
        var before = GateStats.Snapshot();

        // InvokeMethod -> QueryFirst: the same thread re-enters the gate. One EC transaction, one sample.
        Transaction(() => Transaction());

        var after = GateStats.Snapshot();
        Assert.Equal(1, TotalTx(after) - TotalTx(before));

        // And the thread is not left marked "inside": a later acquisition must still be counted, or every
        // transaction after the first nested one would silently vanish from the numbers.
        var before2 = GateStats.Snapshot();
        Transaction();
        Assert.Equal(1, TotalTx(GateStats.Snapshot()) - TotalTx(before2));
    }

    [Fact]
    public void HoldLongerThanTheThreshold_CountsAsAStall()
    {
        var before = GateStats.Snapshot();
        OnDedicatedThread(() => Transaction(() => Thread.Sleep(70)));   // threshold is 50 ms
        var after = GateStats.Snapshot();

        Assert.Equal(1, after.NonPool.Stalls - before.NonPool.Stalls);
        Assert.True(after.NonPool.MaxHoldMs >= 50, $"max hold was {after.NonPool.MaxHoldMs:F1} ms");
    }

    [Fact]
    public void AShortHold_IsNotAStall()
    {
        var before = GateStats.Snapshot();
        OnDedicatedThread(() => Transaction());
        var after = GateStats.Snapshot();

        Assert.Equal(0, TotalStalls(after) - TotalStalls(before));
    }

    [Fact]
    public void Reset_ZeroesEveryCounterAndTheHoldMaximum()
    {
        Transaction(() => Thread.Sleep(70));
        GateStats.Reset();

        var r = GateStats.Snapshot();
        Assert.Equal(0, TotalTx(r));
        Assert.Equal(0, TotalStalls(r));
        Assert.Equal(0, GateStats.NonPoolTicks());
        Assert.Equal(0, r.Pool.MaxHoldMs);
        Assert.Equal(0, r.NonPool.MaxHoldMs);
    }

    [Fact]
    public void Format_WritesEveryFieldInInvariantCulture()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            // Russian-locale machines are the target (the app is localized into it); the default formatter
            // would write 0,0 there and a comma-locale log is not machine-parseable.
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");

            var line = GateStatsLog.Format(GateStats.Snapshot(), 0, 0,
                                           new DateTime(2026, 9, 14, 10, 22, 33, DateTimeKind.Utc));

            Assert.StartsWith("2026-09-14T10:22:33Z ", line);
            foreach (var key in new[] { "tx_pool=", "tx_ui=", "wait_pool_ms=", "wait_pool_max_ms=",
                                        "wait_ui_ms=", "wait_ui_max_ms=", "hold_pool_ms=", "hold_pool_max_ms=",
                                        "hold_pool_stalls=", "hold_ui_ms=", "hold_ui_max_ms=", "hold_ui_stalls=",
                                        "startup_hw_ms=", "build_hw_ms=" })
                Assert.Contains(key, line);

            Assert.Contains("build_hw_ms=0.0", line);   // invariant decimal point, not "0,0"
            Assert.DoesNotContain(",", line);
            Assert.DoesNotContain(Environment.NewLine, line);   // exactly one line per sample
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }
}
