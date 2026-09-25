using AcerHelper.Infrastructure;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="UpdateSchedule"/> — WHEN the update check runs, and the one thing about its output that had to
/// change with it: the same release must not be announced twice.
///
/// WHY THIS FILE EXISTS. The owner suspends the laptop instead of shutting it down, so a check that ran only from
/// the constructor could go weeks without firing (owner's request, 2026-09-22); the fix is a schedule — startup,
/// six-hourly, on every wake, and whenever the flyout is shown — and the risk the fix introduces is the opposite of
/// the bug it fixes: a check that finds the same release three times a day putting it in the notification list
/// three times (the show path would make that many times an afternoon, which is why it reuses the same ledger).
///
/// HOW IT IS DRIVEN. The schedule is stepped BY HAND (<see cref="UpdateSchedule.CheckNow"/>) with a fake check,
/// because asserting on a real timer would be a race; the one test that must use the timer gives it a
/// millisecond period. The two injected delegates are the seam the app is wired through (AppController passes its
/// own check and its own announce), which is what makes "the release was announced once" an assertion about THIS
/// type rather than about a fake standing in for the UI.
///
/// WHAT IT DOES NOT PROVE, stated rather than implied: that a real Windows resume event reaches
/// <see cref="UpdateSchedule.CheckNow"/>. That hop is <see cref="ResumeWatcher"/>'s (an existing, separately
/// wired type: <c>SystemEvents.PowerModeChanged</c> / <c>PowerModes.Resume</c>), and this suite cannot raise a
/// power-management event.
/// </summary>
public class UpdateScheduleTests
{
    /// <summary>Six hours, and never less than one. The value is a stated decision — a release is days apart, and
    /// the sleep case belongs to the resume hook rather than to the timer — so the failure this guards is the app
    /// turning into a poller because some later author found a "fresher" number appealing.
    /// MUTATION THAT REDDENS IT: <c>TimeSpan.FromHours(6)</c> -> <c>TimeSpan.FromMinutes(30)</c>.</summary>
    [Fact]
    public void ThePeriodIsHoursNotMinutes()
        => Assert.True(UpdateSchedule.Period >= TimeSpan.FromHours(1),
                       $"the update check may not run more often than hourly, and the period is {UpdateSchedule.Period}");

    /// <summary>The startup check is the schedule's FIRST tick, so replacing the one-shot constructor call loses
    /// nothing: without it a user who restarts and never sleeps would hear about a release only six hours in.
    /// MUTATION THAT REDDENS IT: delete <c>CheckNow();</c> from <c>Start</c>.</summary>
    [Fact]
    public void Starting_RunsTheStartupCheckNow()
    {
        var check = new CountingCheck();
        using var s = new UpdateSchedule(check.Run, _ => { });

        Assert.Equal(0, check.Calls);   // control: constructing a schedule checks nothing
        s.Start();
        Assert.Equal(1, check.Calls);
    }

    /// <summary>A tick runs the injected check, and the guard is released when it completes — so the second tick
    /// below runs too (a check is not a one-shot per process).
    /// MUTATION THAT REDDENS IT: drop <c>_ = RunAsync();</c> from <c>CheckNow</c>.</summary>
    [Fact]
    public void ATick_RunsTheInjectedCheck()
    {
        var check = new CountingCheck();
        using var s = new UpdateSchedule(check.Run, _ => { });

        s.CheckNow();
        Assert.Equal(1, check.Calls);

        s.CheckNow();   // the check completed synchronously, so the guard is free again
        Assert.Equal(2, check.Calls);
    }

    /// <summary>A tick that arrives while a check is still in flight is SKIPPED — not queued and not run in
    /// parallel, which is what keeps a hung HTTP call from accumulating one check per tick. The guard is released
    /// when the in-flight check finishes, so the tick after it does run: a skipped tick costs one interval, not
    /// the feature.
    ///
    /// The pending task is the seam that makes the in-flight state observable at all — with a completed check the
    /// method would return with the guard already free, and the suppression below would be untestable.
    /// MUTATION THAT REDDENS IT: drop the <c>CompareExchange</c> guard from <c>CheckNow</c> (the two ticks while
    /// pending then start their own checks -> <c>Calls</c> is 3, and the second assertion below reads 3).</summary>
    [Fact]
    public void ATickDuringAnInFlightCheck_IsSkipped_AndTheNextOneAfterItRuns()
    {
        var gate = new TaskCompletionSource();
        var check = new CountingCheck { Pending = gate.Task };
        using var s = new UpdateSchedule(check.Run, _ => { });

        s.CheckNow();
        Assert.Equal(1, check.Calls);   // in flight

        s.CheckNow();
        s.CheckNow();
        Assert.Equal(1, check.Calls);   // neither tick started a second check

        gate.SetResult();
        Assert.True(Eventually.Until(() => { s.CheckNow(); return check.Calls == 2; }),
                    "the guard must be released when the in-flight check finishes — a skipped tick costs one "
                    + $"interval, not the feature (saw {check.Calls})");
    }

    /// <summary>The SHOW/RESTORE trigger runs the same check the timer does: bringing the flyout up onto the
    /// screen (the tray icon / its "Show" item, the Nitro hotkey) is the owner's real cue that a release might
    /// exist, so the app must ask then rather than wait up to six hours for the tick.
    /// MUTATION THAT REDDENS IT: make <c>OnWindowShown</c> a no-op (the count stays 0).</summary>
    [Fact]
    public void WindowShown_RunsTheCheckNow()
    {
        var check = new CountingCheck();
        using var s = new UpdateSchedule(check.Run, _ => { });

        Assert.Equal(0, check.Calls);   // control: constructing a schedule checks nothing
        s.OnWindowShown();
        Assert.Equal(1, check.Calls);
    }

    /// <summary>A show that arrives while a check is still in flight costs nothing: it is SKIPPED, not queued, so
    /// a hung request cannot accumulate one check per window-open. This is the single-flight guard doing the work
    /// for the show path, not a second throttle.
    /// MUTATION THAT REDDENS IT: drop the <c>CompareExchange</c> guard from <c>CheckNow</c> (the second show
    /// starts its own check and <c>Calls</c> reads 2).</summary>
    [Fact]
    public void AWindowShowDuringAnInFlightCheck_IsSkipped()
    {
        var gate = new TaskCompletionSource();
        var check = new CountingCheck { Pending = gate.Task };
        using var s = new UpdateSchedule(check.Run, _ => { });

        s.OnWindowShown();
        Assert.Equal(1, check.Calls);   // in flight

        s.OnWindowShown();
        Assert.Equal(1, check.Calls);   // the second show started nothing

        gate.SetResult();
    }

    /// <summary>Showing the window repeatedly does NOT spam the user: each show runs a check (the show is the
    /// trigger, not a reason to skip), but the release already announced is not announced again — the existing
    /// anti-duplicate ledger suppresses it, so the show path needed no second mechanism. A genuinely newer
    /// release that lands between two shows is still announced in turn.
    /// MUTATION THAT REDDENS IT: delete the <c>_announced == info.Version</c> guard (the three same-release shows
    /// all land -> three announcements).</summary>
    [Fact]
    public void RepeatedWindowShows_AnnounceTheSameReleaseOnce()
    {
        var releases = new Queue<UpdateInfo?>([
            new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0", []),   // first show
            new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0", []),   // ...and again
            new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0", []),   // ...and again
            new UpdateInfo("0.22.0", "https://example.invalid/v0.22.0", [])]); // a release has landed
        var announced = new List<string>();
        UpdateSchedule? schedule = null;   // the check must reach the ledger of the schedule it is wired into
        schedule = new UpdateSchedule(
            () => { var info = releases.Dequeue(); if (info != null) schedule!.Announce(info); return Task.CompletedTask; },
            i => announced.Add(i.Version));
        using var _ = schedule;

        schedule.OnWindowShown();
        schedule.OnWindowShown();
        schedule.OnWindowShown();
        Assert.Equal(["0.21.0"], announced);   // three shows of the same release -> one announcement

        schedule.OnWindowShown();
        Assert.Equal(["0.21.0", "0.22.0"], announced);   // a newer release still gets through
    }

    /// <summary>THE ANTI-DUPLICATE RULE, keyed on the VERSION. The same release ticked twice is announced once —
    /// that is what keeps a six-hourly check from filling the notification list — while a genuinely newer release
    /// still gets through. The middle assertion is the URL half, and it is not decoration: a re-cut release keeps
    /// its version and changes its page/asset URLs, and the announcement the user must not see twice is about the
    /// release rather than about the file.
    ///
    /// MUTATIONS THAT REDDEN IT, one per half: delete the <c>_announced == info.Version</c> guard (the repeat and
    /// the re-cut both land -> three announcements); or compare <c>info.Url</c> instead of <c>info.Version</c>
    /// (the re-cut URL is a new "release" -> the second assertion reads two).</summary>
    [Fact]
    public void TheSameReleaseIsAnnouncedOnce_AndANewerOneStillGetsThrough()
    {
        var announced = new List<string>();
        using var s = new UpdateSchedule(() => Task.CompletedTask, i => announced.Add(i.Version));

        s.Announce(new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0", []));
        s.Announce(new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0", []));
        Assert.Equal(["0.21.0"], announced);

        s.Announce(new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0-rec", []));
        Assert.Equal(["0.21.0"], announced);

        s.Announce(new UpdateInfo("0.22.0", "https://example.invalid/v0.22.0", []));
        Assert.Equal(["0.21.0", "0.22.0"], announced);
    }

    /// <summary>The same rule reached through the wiring the app uses: the check hands what it found to the ledger
    /// and the ledger decides whether the user hears about it (AppController's check method, after the change).
    /// A startup tick, a periodic tick and a post-resume tick that all find the same release announce it once; the
    /// tick after a newer release is published announces that one.
    ///
    /// The release feed is what makes each tick distinct — without it the test would be the one above with a
    /// timer attached.
    /// MUTATION THAT REDDENS IT: the deleted <c>_announced == info.Version</c> guard, as above (the same rule by a
    /// second route).</summary>
    [Fact]
    public void TicksFindingTheSameRelease_AnnounceItOnce()
    {
        var releases = new Queue<UpdateInfo?>([
            new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0", []),   // startup
            new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0", []),   // six hours later
            new UpdateInfo("0.21.0", "https://example.invalid/v0.21.0", []),   // ...and after a wake
            new UpdateInfo("0.22.0", "https://example.invalid/v0.22.0", [])]); // a release has landed
        var announced = new List<string>();
        UpdateSchedule? schedule = null;   // the check must reach the ledger of the schedule it is wired into
        schedule = new UpdateSchedule(
            () => { var info = releases.Dequeue(); if (info != null) schedule!.Announce(info); return Task.CompletedTask; },
            i => announced.Add(i.Version));
        using var _ = schedule;

        schedule.Start();
        schedule.CheckNow();
        schedule.CheckNow();
        Assert.Equal(["0.21.0"], announced);

        schedule.CheckNow();
        Assert.Equal(["0.21.0", "0.22.0"], announced);
    }

    /// <summary>The timer REPEATS — the requirement the resume hook cannot cover, for a machine that simply stays
    /// awake for weeks. Driven at a millisecond period because six hours is not a test's business; the production
    /// period is pinned by its own test above.
    /// MUTATION THAT REDDENS IT: <c>_timer.Change(_period, _period)</c> ->
    /// <c>_timer.Change(_period, Timeout.InfiniteTimeSpan)</c> (one check, then silence -> the assertion below
    /// never holds and reports <c>Calls</c> as 1).</summary>
    [Fact]
    public void TheTimerKeepsChecking()
    {
        var check = new CountingCheck();
        using var s = new UpdateSchedule(check.Run, _ => { }, period: TimeSpan.FromMilliseconds(40));

        s.Start();   // tick 1 is the startup check

        Assert.True(Eventually.Until(() => check.Calls >= 3, budgetMs: 3000),
                    $"the period must repeat — three checks within 3 s of a 40 ms period, saw {check.Calls}");
    }

    /// <summary>Teardown: after <see cref="UpdateSchedule.Dispose"/> nothing checks again. A check that lands
    /// during shutdown has nowhere to report to, and the tick at the end of this test stands for the one that
    /// would otherwise raise a notification over a window that is going away.
    ///
    /// NOT ASSERTED, and this is measured rather than assumed: that the timer itself is released. Deleting
    /// <c>_timer.Dispose()</c> from <c>Dispose</c> leaves this test GREEN — the <c>_disposed</c> flag silences the
    /// still-firing ticks before they reach the check, so a leaked <see cref="System.Threading.Timer"/> is a
    /// resource fact rather than a behaviour one, and no assertion here could see it. The flag is what this test
    /// is about; the timer disposal beside it is hygiene, not a claim.
    ///
    /// The wait before the count is a NEGATIVE one — five periods of silence — so it is only meaningful with the
    /// "the timer was running" assertion above it; without that, a schedule that never started would satisfy it
    /// vacuously.
    /// MUTATION THAT REDDENS IT: delete <c>_disposed = true;</c> from <c>Dispose</c> (the explicit tick at the end
    /// then runs the check and <c>Calls</c> grows).</summary>
    [Fact]
    public void AfterDispose_NothingChecks()
    {
        var check = new CountingCheck();
        var s = new UpdateSchedule(check.Run, _ => { }, period: TimeSpan.FromMilliseconds(40));
        s.Start();

        Assert.True(Eventually.Until(() => check.Calls >= 2, budgetMs: 3000),
                    $"the timer must be running before its stopping means anything (saw {check.Calls})");

        s.Dispose();
        var atDispose = check.Calls;
        Thread.Sleep(200);   // five periods: a live timer would have added about five checks
        Assert.Equal(atDispose, check.Calls);

        s.CheckNow();
        Assert.Equal(atDispose, check.Calls);
    }
}

/// <summary>The injected check, as a fake: it counts its calls and hands back the task the test is holding — an
/// already completed one by default, or a pending one the test releases when it wants to observe the in-flight
/// state. Interlocked because the timer path calls it from a pool thread.</summary>
internal sealed class CountingCheck
{
    private int _calls;

    /// <summary>What <see cref="Run"/> returns. Set before the schedule is started.</summary>
    public Task Pending { get; set; } = Task.CompletedTask;

    public int Calls => Volatile.Read(ref _calls);

    public Task Run()
    {
        Interlocked.Increment(ref _calls);
        return Pending;
    }
}
