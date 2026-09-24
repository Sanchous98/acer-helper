using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="PollSchedule"/> — WHEN the telemetry poll runs, and the two properties the freeze fix rests on:
/// the tick is NOT on the UI thread (so it cannot be starved by the visible window's rendering/input), and a
/// tick that throws does not stop the schedule.
///
/// WHY THE FILE EXISTS. The 3 s poll used to be a UI-thread <c>DispatcherTimer</c>, which the toolkit creates at
/// <c>DispatcherPriority.Background</c> — on the GLib backend a <c>g_idle_add</c> source at the lowest priority,
/// below render/input. On a visible window the tick was deferred and only ran once the UI went idle (hidden or
/// minimized), so the live readings froze on screen and caught up on hide/show. The power row made it visible
/// (it changes every second); the fix moves the trigger to a pool-thread timer.
///
/// HOW IT IS DRIVEN. The schedule is stepped BY HAND (<see cref="PollSchedule.PollNow"/>) for the value and
/// error tests, because asserting on a real timer would be a race; the two tests that must use the timer give it
/// a millisecond period. The injected tick is the seam the app is wired through (<c>AppController</c> passes its
/// own <c>Refresh</c>), which is what makes "a changing read reaches the power row" an assertion about THIS type
/// rather than about a fake standing in for the UI.
///
/// WHAT IT DOES NOT PROVE: that the real GLib dispatcher starves a Background <c>DispatcherTimer</c> while the
/// window is visible. That is the toolkit's scheduling (and the reason this type is off the dispatcher); no
/// in-process test can raise the platform's render/input sources.
/// </summary>
public class PollScheduleTests
{
    /// <summary>The cadence is seconds, not milliseconds: a shorter one re-issues the EC/WMI burst behind one pass
    /// while it is still settling, and a longer one stops the readings reading as live.
    /// MUTATION THAT REDDENS IT: <c>TimeSpan.FromSeconds(3)</c> -> <c>TimeSpan.FromSeconds(30)</c>.</summary>
    [Fact]
    public void ThePeriodIsThreeSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(3), PollSchedule.Period);

    /// <summary>A schedule that is never started never fires — whether the feature is on is the app's decision,
    /// not a timer that arms itself in a constructor.
    /// MUTATION THAT REDDENS IT: arm the timer in the constructor instead of in <see cref="PollSchedule.Start"/>.</summary>
    [Fact]
    public void BeforeStart_NothingTicks()
    {
        var calls = 0;
        using var poll = new PollSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));

        Thread.Sleep(100);   // several periods; an armed timer would have ticked
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    /// <summary>The timer REPEATS — the requirement a one-shot startup poll cannot cover, for a process that
    /// stays up for days. Driven at a millisecond period because seconds is not a test's business.
    /// MUTATION THAT REDDENS IT: <c>_timer.Change(_period, _period)</c> -> <c>_timer.Change(_period,
    /// Timeout.InfiniteTimeSpan)</c> (one tick, then silence).</summary>
    [Fact]
    public void TheTimerKeepsTicking()
    {
        var calls = 0;
        using var poll = new PollSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));

        poll.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 3, budgetMs: 3000),
                    $"the period must repeat — three ticks within 3 s of a 20 ms period, saw {Volatile.Read(ref calls)}");
    }

    /// <summary>A tick that throws is swallowed and the schedule keeps its cadence: a transient ACPI/EC/WMI read
    /// failure is routine, and an unhandled exception on a pool timer callback would otherwise take the process
    /// down. The next interval is the retry.
    /// MUTATION THAT REDDENS IT: delete the <c>try/catch</c> around <c>_tick()</c> (the exception escapes the
    /// timer callback and the count below never reaches four).</summary>
    [Fact]
    public void AFailingTickDoesNotStopTheSchedule()
    {
        var calls = 0;
        using var poll = new PollSchedule(() =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new InvalidOperationException("transient read failure");
        }, TimeSpan.FromMilliseconds(20));

        poll.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 4, budgetMs: 3000),
                    $"two failing reads must not stop the schedule — a successful tick must still follow, saw {Volatile.Read(ref calls)}");
    }

    /// <summary>A failed read delivers nothing, and the NEXT refresh is unaffected — the exact shape the app
    /// needs: one bad battery read must not leave the power row frozen on the previous value.
    /// MUTATION THAT REDDENS IT: drop the <c>try/catch</c> so the failed <see cref="PollSchedule.PollNow"/>
    /// escapes and the second call never runs.</summary>
    [Fact]
    public void AFailedReadThenASuccessfulOne_OnTheNextRefresh()
    {
        var vm = new BatteryViewModel(hasInfo: true, limit: null, calibration: null, chargeMode: null);
        var telemetry = new FakeBatteryTelemetry
        {
            Snapshot = new BatteryInfoSnapshot { State = BatteryState.Discharging, PowerWatts = -10.0 },
        };
        var first = true;
        using var poll = new PollSchedule(() =>
        {
            if (first) { first = false; throw new InvalidOperationException("transient read failure"); }
            vm.Update(telemetry.Read());
        });

        poll.PollNow();                 // the read fails; nothing may be applied
        Assert.False(vm.ShowPower);

        poll.PollNow();                 // the next refresh reads through and lands
        Assert.True(vm.ShowPower);
        Assert.Equal(Loc.T("Power draw"), vm.PowerLabel);
        Assert.Equal(Loc.T("{0:0.0} W", 10.0), vm.Power);
        Assert.Equal(1, telemetry.ReadCount);
    }

    /// <summary>THE LIVE-ROW CLAIM: repeated refreshes with changing hardware values propagate to the ViewModel's
    /// <c>Power</c>/<c>ShowPower</c>, in both directions, and the row hides again when the rate is gone. This is
    /// the readout the freeze froze; the schedule must deliver a fresh snapshot on every tick, not the first one.
    /// MUTATION THAT REDDENS IT: cache the first <see cref="BatteryInfoSnapshot"/> in the tick (the later
    /// assertions then read 12.3 W / "Power draw").</summary>
    [Fact]
    public void RepeatedRefreshesWithChangingValues_TrackThePowerRow()
    {
        var vm = new BatteryViewModel(hasInfo: true, limit: null, calibration: null, chargeMode: null);
        var telemetry = new FakeBatteryTelemetry();
        using var poll = new PollSchedule(() => vm.Update(telemetry.Read()));

        telemetry.Snapshot = new BatteryInfoSnapshot { State = BatteryState.Discharging, PowerWatts = -12.3 };
        poll.PollNow();
        Assert.True(vm.ShowPower);
        Assert.Equal(Loc.T("Power draw"), vm.PowerLabel);
        Assert.Equal(Loc.T("{0:0.0} W", 12.3), vm.Power);

        telemetry.Snapshot = new BatteryInfoSnapshot { State = BatteryState.Charging, PowerWatts = 45.0 };
        poll.PollNow();
        Assert.Equal(Loc.T("Charging power"), vm.PowerLabel);
        Assert.Equal(Loc.T("{0:0.0} W", 45.0), vm.Power);

        telemetry.Snapshot = new BatteryInfoSnapshot { State = BatteryState.Unknown };   // no rate
        poll.PollNow();
        Assert.False(vm.ShowPower);
        Assert.Equal("", vm.Power);

        telemetry.Snapshot = new BatteryInfoSnapshot { State = BatteryState.Charging, PowerWatts = 30.0 };
        poll.PollNow();
        Assert.True(vm.ShowPower);
        Assert.Equal(Loc.T("{0:0.0} W", 30.0), vm.Power);
    }

    /// <summary>Teardown: after <see cref="PollSchedule.Dispose"/> nothing ticks, on the timer path or a manual
    /// step. A tick that lands during shutdown has nowhere to go.</summary>
    [Fact]
    public void AfterDispose_NothingTicks()
    {
        var calls = 0;
        var poll = new PollSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));
        poll.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 2, budgetMs: 3000),
                    $"the timer must be running before stopping means anything (saw {Volatile.Read(ref calls)})");

        poll.Dispose();
        var atDispose = Volatile.Read(ref calls);
        Thread.Sleep(150);   // several periods: a live timer would have added more
        Assert.Equal(atDispose, Volatile.Read(ref calls));

        poll.PollNow();
        Assert.Equal(atDispose, Volatile.Read(ref calls));
    }
}
