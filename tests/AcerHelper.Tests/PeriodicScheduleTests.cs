using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AcerHelper.Infrastructure;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="PeriodicSchedule"/> — the one periodic-timer implementation the app's polls, update check and
/// every UI-thread debounce/animation are now built on. This file pins the contract independent of any one
/// caller: it repeats, a failing tick does not stop it, one tick runs at a time, Stop really stops (including a
/// tick already queued for the UI thread), and the UI mode delivers the tick THROUGH the injected dispatch
/// rather than touching the UI from the pool thread.
///
/// WHY THE UI-MODE TESTS MATTER AND WHAT THEY DO NOT PROVE. The starvation bug these tests exist to prevent is
/// the toolkit's GLib scheduling (a default <c>DispatcherTimer</c> runs at the idle priority); no in-process
/// xUnit test can raise the platform's render/input sources, exactly as <c>PollScheduleTests</c> records. What
/// IS decidable here is the SHAPE the fix rests on: the tick is never run on the timer's own thread in UI mode
/// — it goes through the injected dispatcher — and a Stop invalidates a tick the dispatcher has already been
/// handed. The real poster (<c>UiSchedule</c>) is a one-liner over <c>Dispatcher.UIThread.Post(action,
/// priority)</c> and is not exercised here, because a bare xUnit process cannot pump the dispatcher (see
/// <c>Eventually</c>'s note).
///
/// HOW IT IS DRIVEN. Pool-mode tests use a millisecond period and wait with <see cref="Eventually"/>, because
/// the only handle on a real timer is its side effects. The UI-mode tests pass a dispatch delegate that QUEUES
/// the action instead of running it, so "the tick was marshalled, not run here" and "Stop dropped it" are
/// observable without a dispatcher at all; the manual seam <see cref="PeriodicSchedule.TriggerNow"/> is used
/// where a test wants exactly one, race-free pass.
/// </summary>
public class PeriodicScheduleTests
{
    /// <summary>A schedule that is never started never fires — whether a feature is on is the app's decision,
    /// not a timer that arms itself in a constructor.
    /// MUTATION THAT REDDENS IT: arm the timer in the constructor instead of in <see cref="PeriodicSchedule.Start"/>.</summary>
    [Fact]
    public void BeforeStart_NothingTicks()
    {
        var calls = 0;
        using var s = new PeriodicSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));

        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    /// <summary>The timer REPEATS — the requirement a one-shot call cannot cover, for a process that stays up
    /// for days.
    /// MUTATION THAT REDDENS IT: <c>_timer.Change(_period, _period)</c> -> <c>_timer.Change(_period,
    /// Timeout.InfiniteTimeSpan)</c> (one tick, then silence).</summary>
    [Fact]
    public void TheTimerKeepsTicking()
    {
        var calls = 0;
        using var s = new PeriodicSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));

        s.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 3, budgetMs: 3000),
                    $"the period must repeat — three ticks within 3 s of a 20 ms period, saw {Volatile.Read(ref calls)}");
    }

    /// <summary>A tick that throws is swallowed and the schedule keeps its cadence: an unhandled exception on a
    /// pool-timer callback would otherwise be an unobserved pool exception (process death), and a tick the timer
    /// never re-armed is a feature that dies silently.
    /// MUTATION THAT REDDENS IT: delete the <c>try/catch</c> around <c>_tick()</c> (the exception escapes the
    /// timer callback and the count below never reaches four).</summary>
    [Fact]
    public void AFailingTickDoesNotStopTheSchedule()
    {
        var calls = 0;
        using var s = new PeriodicSchedule(() =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new InvalidOperationException("transient failure");
        }, TimeSpan.FromMilliseconds(20));

        s.Start();

        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 4, budgetMs: 3000),
                    $"two failing ticks must not stop the schedule — a successful tick must still follow, saw {Volatile.Read(ref calls)}");
    }

    /// <summary>Stop stops, and Start resumes: the resettable debounces depend on the first half (a Stop drops
    /// the pending tick), and the lighting burst on the bounded half (it stops itself after its last tick).
    /// MUTATION THAT REDDENS IT: make <c>Stop</c> a no-op (the count keeps growing after the stop).</summary>
    [Fact]
    public void StopStops_AndStartResumes()
    {
        var calls = 0;
        using var s = new PeriodicSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));
        s.Start();
        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 2, budgetMs: 3000), "the timer must be running first");

        s.Stop();
        Thread.Sleep(40);                       // let a callback already in flight finish
        var atStop = Volatile.Read(ref calls);
        Thread.Sleep(150);                      // several periods: a live timer would have added more
        Assert.Equal(atStop, Volatile.Read(ref calls));

        s.Start();
        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) > atStop, budgetMs: 3000),
                    "a stopped schedule must be startable again");
    }

    /// <summary><see cref="PeriodicSchedule.IsRunning"/> tracks Start/Stop — the read a resettable debounce uses
    /// to tell "the user's edit has not been applied yet" (<c>LightViewModel.AdoptBrightness</c>), replacing
    /// <c>DispatcherTimer.IsEnabled</c>. It is false before Start, true while armed, and false again once the
    /// debounce's tick has stopped itself.
    /// MUTATION THAT REDDENS IT: leave <c>_running</c> out of <c>Start</c> (the middle assertion reads false).</summary>
    [Fact]
    public void IsRunning_TracksStartAndStop()
    {
        using var s = new PeriodicSchedule(() => { }, TimeSpan.FromMilliseconds(20));

        Assert.False(s.IsRunning);
        s.Start();
        Assert.True(s.IsRunning);
        s.Stop();
        Assert.False(s.IsRunning);
    }

    /// <summary>The manual seam runs one tick on the CALLING thread, with no Start and no timer — the shape the
    /// value tests need, because asserting on a real timer would be a race.
    /// MUTATION THAT REDDENS IT: make <c>TriggerNow</c> a no-op until started (the count stays 0).</summary>
    [Fact]
    public void TriggerNow_RunsOneTickOnTheCallingThread()
    {
        var calls = 0;
        var thread = 0;
        using var s = new PeriodicSchedule(() =>
        {
            Interlocked.Increment(ref calls);
            thread = Environment.CurrentManagedThreadId;
        }, TimeSpan.FromMilliseconds(20));

        s.TriggerNow();

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
    }

    /// <summary>ONE TICK AT A TIME: a tick that arrives while the previous one is still running is skipped, not
    /// overlapped. A sample is a sample; two samples racing on the same hardware is worse than one missed
    /// interval.
    /// MUTATION THAT REDDENS IT: drop the <c>_inFlight</c> guard from <c>Dispatch</c> (the second
    /// <c>TriggerNow</c> enters the blocked tick too, and <c>calls</c> reads 2).</summary>
    [Fact]
    public async Task AnOverlappingTickIsSkipped()
    {
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var calls = 0;
        using var s = new PeriodicSchedule(() =>
        {
            Interlocked.Increment(ref calls);
            started.Set();
            release.Wait(2000);
        }, TimeSpan.FromMilliseconds(20));

        var first = Task.Run(s.TriggerNow);          // holds the single-flight guard
        Assert.True(started.Wait(2000), "the first tick must start");

        s.TriggerNow();                              // arrives while the first is in flight -> skipped
        Assert.Equal(1, Volatile.Read(ref calls));

        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>UI mode delivers the tick THROUGH the injected dispatch, so the pool thread never touches UI
    /// state; the tick does not run until the dispatcher runs the action it was handed.
    /// MUTATION THAT REDDENS IT: run <c>_tick()</c> directly in <c>Run</c> for UI mode (the tick count is
    /// non-zero before the queued action is drained).</summary>
    [Fact]
    public void UiMode_RunsTheTickOnlyWhenTheQueuedActionRuns()
    {
        var queued = new ConcurrentQueue<Action>();
        var ticks = 0;
        using var s = new PeriodicSchedule(() => Interlocked.Increment(ref ticks),
                                           TimeSpan.FromMilliseconds(20), queued.Enqueue);

        s.Start();
        Assert.True(Eventually.Until(() => !queued.IsEmpty, budgetMs: 3000), "the timer must hand a tick to the dispatcher");
        Assert.Equal(0, Volatile.Read(ref ticks));   // queued, not run on the pool thread

        Assert.True(queued.TryDequeue(out var tick));
        tick();

        Assert.True(Volatile.Read(ref ticks) >= 1);
    }

    /// <summary>UI mode queues at most ONE tick at a time: a busy UI thread coalesces rather than piling up a
    /// burst of stale work. With the dispatcher never draining, several periods still leave one action queued.
    /// MUTATION THAT REDDENS IT: drop the <c>_uiPending</c> guard (seven periods then queue seven actions).</summary>
    [Fact]
    public void UiMode_CoalescesToAtMostOneQueuedTick()
    {
        var queued = new ConcurrentQueue<Action>();
        using var s = new PeriodicSchedule(() => { }, TimeSpan.FromMilliseconds(20), queued.Enqueue);

        s.Start();
        Assert.True(Eventually.Until(() => !queued.IsEmpty, budgetMs: 3000), "the timer must queue a tick");
        Thread.Sleep(150);   // several periods; nothing drains the queue

        Assert.Single(queued);
    }

    /// <summary>Stop really stops in UI mode too: a tick the dispatcher has already been handed is dropped when
    /// it finally runs, which is what the resettable debounces rely on (a mode switch / Load must cancel the
    /// pending edit, not let it fire against the new state).
    /// MUTATION THAT REDDENS IT: delete the generation check from <c>Dispatch</c> (the queued tick runs after
    /// Stop and the count below reads 1).</summary>
    [Fact]
    public void UiMode_StopDropsATickAlreadyQueued()
    {
        var queued = new ConcurrentQueue<Action>();
        var ticks = 0;
        using var s = new PeriodicSchedule(() => Interlocked.Increment(ref ticks),
                                           TimeSpan.FromMilliseconds(20), queued.Enqueue);

        s.Start();
        Assert.True(Eventually.Until(() => !queued.IsEmpty, budgetMs: 3000), "the timer must queue a tick");
        s.Stop();

        Assert.True(queued.TryDequeue(out var tick));
        tick();   // the dispatcher runs the action Stop invalidated

        Assert.Equal(0, Volatile.Read(ref ticks));
    }

    /// <summary>Teardown: after <see cref="PeriodicSchedule.Dispose"/> nothing ticks, on the timer path or the
    /// manual step. A tick that lands during shutdown has nowhere to go.
    /// MUTATION THAT REDDENS IT: delete <c>_disposed = true;</c> (the explicit <c>TriggerNow</c> at the end
    /// increments the count).</summary>
    [Fact]
    public void AfterDispose_NothingTicks()
    {
        var calls = 0;
        var s = new PeriodicSchedule(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));
        s.Start();
        Assert.True(Eventually.Until(() => Volatile.Read(ref calls) >= 2, budgetMs: 3000), "the timer must be running first");

        s.Dispose();
        var atDispose = Volatile.Read(ref calls);
        Thread.Sleep(150);   // several periods: a live timer would have added more
        Assert.Equal(atDispose, Volatile.Read(ref calls));

        s.TriggerNow();
        Assert.Equal(atDispose, Volatile.Read(ref calls));
    }

    /// <summary>The ONE-IMPLEMENTATION rule, asserted rather than described: no production source may construct
    /// a raw <c>DispatcherTimer</c> again. This is what keeps a future periodic timer from silently landing back
    /// at the toolkit's default <c>Background</c> priority — the starvation bug this whole file exists to
    /// prevent. (The named schedules <see cref="PollSchedule"/> and <see cref="UpdateSchedule"/> delegate to
    /// <see cref="PeriodicSchedule"/>, and every UI site uses it through <c>UiSchedule</c>.)
    /// MUTATION THAT REDDENS IT: add <c>new DispatcherTimer { Interval = … }</c> to any file under a layer
    /// folder.</summary>
    [Fact]
    public void NoProductionSourceConstructsARawDispatcherTimer()
    {
        var root = Root();
        var offenders = new[] { "Domain", "Application", "Infrastructure", "UI", "Bootstrap", "Localization" }
            .SelectMany(layer => Directory.EnumerateFiles(Path.Combine(root, layer), "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => File.ReadAllText(p).Contains("new DispatcherTimer", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "every periodic timer must go through Infrastructure/PeriodicSchedule (see UiSchedule for the UI "
            + "poster); a raw DispatcherTimer defaults to the starvable Background priority:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>The repository root, taken from the COMPILER's path rather than the current directory: the test
    /// host runs with its working directory set to the output folder, where a relative path would find either
    /// nothing or a stale copy of the tree (the same helper <c>ArchitectureMapTests</c> uses).</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
