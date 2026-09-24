using System.Reflection;
using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="BatteryPollSchedule"/> — the FAST half of the telemetry cadence (one second), split from the full
/// <see cref="PollSchedule"/> refresh (three seconds) so the live power/charge row updates more often without
/// re-issuing the EC/WMI sweep, the fan drive and the power-source sync the full pass owns. This file pins the
/// two cadences and their relationship, and that the fast wrapper really drives the battery row.
///
/// The mechanism (pool trigger, single-flight, error swallow) lives in <see cref="PeriodicSchedule"/> and is
/// pinned by <c>PeriodicScheduleTests</c>; what is asserted here is only what is NEW: the one-second constant,
/// that it is SHORTER than the full pass, and that a tick through THIS wrapper reaches <see cref="BatteryViewModel"/>.
/// The AppController wiring itself is not reachable by this suite (it needs a live desktop lifetime and a refresh
/// loop), the same gap <c>BatteryPrimeTests</c> records for its own call site.
///
/// ONE WRITER. The split only buys anything if the fast path OWNS the battery card: the heavy pass reads the
/// battery at the start of a sweep that can reach the UI seconds later, so letting it push that snapshot to the
/// same <see cref="BatteryViewModel"/> would overwrite a fresh one-second value with an older three-second one
/// every sweep. The two reflection facts below pin that the heavy path carries no battery snapshot — at the
/// view-model's <c>Refresh</c> and at the pass's own <c>Tick</c> — so re-adding either reddens here rather than
/// silently pinning the user's power row back to the slow cadence. (AppController is internal; the test
/// assembly is granted access — see AcerHelper.csproj's InternalsVisibleTo.)
/// </summary>
public class BatteryPollScheduleTests
{
    /// <summary>The cadence is one second: fast enough that the live power row reads as live, slow enough that the
    /// two cheap OS calls behind a pass are not hammered.
    /// MUTATION THAT REDDENS IT: <c>TimeSpan.FromSeconds(1)</c> -> <c>TimeSpan.FromSeconds(10)</c>.</summary>
    [Fact]
    public void ThePeriodIsOneSecond()
        => Assert.Equal(TimeSpan.FromSeconds(1), BatteryPollSchedule.Period);

    /// <summary>The split, stated as a relationship rather than two independent constants: the battery row must
    /// refresh FASTER than the full pass it was separated from, or the separation buys nothing.
    /// MUTATION THAT REDDENS IT: raise the fast period to the full one (3 s), or lower the full period below it.</summary>
    [Fact]
    public void TheFastBatteryPeriodIsShorterThanTheFullRefreshPeriod()
        => Assert.True(BatteryPollSchedule.Period < PollSchedule.Period,
            "the live power/charge row must refresh faster than the full EC/WMI sweep it was split from");

    /// <summary>THE OWNERSHIP RULE, half one: the heavy refresh no longer carries a battery snapshot, so it
    /// cannot overwrite the card the fast path owns. The pass still READS the battery (SyncPowerSource needs
    /// the AC state); it just never hands that value to the view-model.
    /// MUTATION THAT REDDENS IT: add the <c>BatteryInfoSnapshot battery</c> parameter back to
    /// <see cref="MainViewModel.Refresh"/> — the heavy pass would then overwrite the one-second value every
    /// three seconds, which is exactly the regression this split exists to prevent.</summary>
    [Fact]
    public void TheHeavyRefreshCarriesNoBatterySnapshot()
    {
        var refresh = typeof(MainViewModel).GetMethod(nameof(MainViewModel.Refresh));

        Assert.NotNull(refresh);
        Assert.DoesNotContain(refresh!.GetParameters(),
            p => p.ParameterType == typeof(BatteryInfoSnapshot));
    }

    /// <summary>THE OWNERSHIP RULE, half two: the heavy pass's own snapshot (<c>Tick</c>) carries no battery,
    /// so there is no field for a future <c>UiPass</c> to forget and start feeding back in. Reflection on the
    /// private nested record, the same shape <c>ArchitectureMapTests</c> uses for its structural pins.
    /// MUTATION THAT REDDENS IT: put <c>BatteryInfoSnapshot Battery</c> back on the <c>Tick</c> record.</summary>
    [Fact]
    public void TheHeavyPassTickCarriesNoBatterySnapshot()
    {
        var tick = typeof(AppController).GetNestedType("Tick", BindingFlags.NonPublic);

        Assert.NotNull(tick);
        Assert.DoesNotContain(
            tick!.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            f => f.FieldType == typeof(BatteryInfoSnapshot));
    }

    /// <summary>THE LIVE-ROW CLAIM AT THE FAST CADENCE: repeated fast ticks deliver a changing snapshot to the
    /// battery view-model's power row, in both directions, and the row hides again when the rate is gone. Stepped
    /// by hand through the wrapper's own seam, because asserting on a real timer would be a race.
    /// MUTATION THAT REDDENS IT: have the schedule cache its first read (the later assertions then read 12.3 W).</summary>
    [Fact]
    public void RepeatedFastPollsWithChangingValues_TrackThePowerRow()
    {
        var vm = new BatteryViewModel(hasInfo: true, limit: null, calibration: null, chargeMode: null);
        var telemetry = new FakeBatteryTelemetry();
        using var poll = new BatteryPollSchedule(() => vm.Update(telemetry.Read()));

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
    }

    /// <summary>The timer REPEATS — the requirement a one-shot startup read cannot cover, for a process that
    /// stays up for days. Driven at a millisecond period because one second is not a test's business.
    /// MUTATION THAT REDDENS IT: <c>_timer.Change(_period, _period)</c> -> <c>_timer.Change(_period,
    /// Timeout.InfiniteTimeSpan)</c> (one tick, then silence).</summary>
    [Fact]
    public void TheTimerKeepsTicking()
    {
        var calls = 0;
        using var poll = new BatteryPollSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));

        poll.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 3, budgetMs: 3000),
                    $"the period must repeat — three ticks within 3 s of a 20 ms period, saw {Volatile.Read(ref calls)}");
    }

    /// <summary>A tick that throws is swallowed and the schedule keeps its cadence: a transient battery read
    /// failure is routine, and an unhandled exception on a pool timer callback would take the process down. The
    /// next interval is the retry.
    /// MUTATION THAT REDDENS IT: delete the <c>try/catch</c> around <c>_tick()</c> in <c>PeriodicSchedule</c>.</summary>
    [Fact]
    public void AFailingTickDoesNotStopTheSchedule()
    {
        var calls = 0;
        using var poll = new BatteryPollSchedule(() =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new InvalidOperationException("transient read failure");
        }, TimeSpan.FromMilliseconds(20));

        poll.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 4, budgetMs: 3000),
                    $"two failing reads must not stop the schedule — a successful tick must still follow, saw {Volatile.Read(ref calls)}");
    }

    /// <summary>ONE FAST READ AT A TIME: a fast tick that arrives while the previous read is still running is
    /// skipped, not overlapped. A sample is a sample, and two reads racing the same battery gauge is worse than
    /// one missed second. This is <see cref="PeriodicSchedule"/>'s guard, asserted through the fast wrapper so the
    /// faster cadence does not quietly lose it.
    /// MUTATION THAT REDDENS IT: drop the <c>_inFlight</c> guard from <c>PeriodicSchedule.Dispatch</c>.</summary>
    [Fact]
    public async Task AnOverlappingFastPollIsSkipped()
    {
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var calls = 0;
        using var poll = new BatteryPollSchedule(() =>
        {
            Interlocked.Increment(ref calls);
            started.Set();
            release.Wait(2000);
        }, TimeSpan.FromMilliseconds(20));

        var first = Task.Run(poll.PollNow);          // holds the single-flight guard
        Assert.True(started.Wait(2000), "the first fast read must start");

        poll.PollNow();                              // arrives while the first is in flight -> skipped
        Assert.Equal(1, Volatile.Read(ref calls));

        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
