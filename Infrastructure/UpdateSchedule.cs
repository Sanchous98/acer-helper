namespace AcerHelper.Infrastructure;

/// <summary>WHEN the update check runs — never what it does. The check is injected (<c>Func&lt;Task&gt; check</c>)
/// and so is the announcement (<c>Action&lt;UpdateInfo&gt; announce</c>), so this type owns exactly two things:
/// the schedule (startup, every <see cref="Period"/>, on every wake from sleep, and whenever the window is
/// brought up onto the screen — see <see cref="OnWindowShown"/>) and the memory of what has already been
/// announced (see <see cref="Announce"/>).
///
/// WHY IT EXISTS. The check used to run once, from AppController's constructor, and the owner suspends the laptop
/// rather than shutting it down: a start-only check can therefore go weeks without firing, and a session that is
/// never restarted never learns that a release exists (owner's request, 2026-09-22 — "обновление проверяется
/// только при старте приложения… надо сделать проверку обновлений периодически"). The triggers answer the ways a
/// process lives long: the timer covers one that merely stays up, the resume hook covers the suspended machine,
/// and <see cref="OnWindowShown"/> covers the owner bringing the app up — which is the case the owner actually hits.
///
/// THE RESUME HOOK IS <see cref="ResumeWatcher"/>, the same per-OS signal the lighting re-apply already uses:
/// <c>SystemEvents.PowerModeChanged</c> with <c>PowerModes.Resume</c> on Windows (see ResumeWatcher.Windows.cs),
/// and systemd-logind's <c>PrepareForSleep(false)</c> on Linux. NO SIGNAL IS INVENTED WHERE ONE IS MISSING: on a
/// Linux box with no gdbus/logind the watcher degrades to a no-op — its own docstring says so — and the periodic
/// timer is then the only trigger, so a machine that wakes and stays awake is checked on the timer's next tick.
/// Nothing here pretends a wake was seen when none was reported.
///
/// OFF THE UI THREAD, by construction: <see cref="CheckNow"/> runs on whichever thread called it, which is the
/// thread-pool thread <see cref="System.Threading.Timer"/> hands the tick to, the power-mode thread, or the UI
/// thread for the startup tick. The check's own I/O is asynchronous and the announcement marshals itself (that is
/// the injected delegate's job); this type touches nothing UI-bound.
///
/// ONE CHECK AT A TIME. A tick that arrives while the previous check is still in flight is SKIPPED, not queued:
/// a hung HTTP call would otherwise accumulate one check per tick, and "check for a release" is not work that has
/// to happen N times. The guard is released when the in-flight check finishes, so a skipped tick costs one
/// interval and not the feature — and <see cref="UpdateChecker"/> bounds its own request with a 10 s timeout, so
/// even a dead network releases the guard within seconds instead of parking it for the process's life.
///
/// THE PERIOD IS HOURS ON PURPOSE. The resume and show triggers cover the case the owner hits, so the timer's only
/// job is "a process that stays up for weeks must not go unchecked" — not "notice a release within minutes".
/// <see cref="Period"/> is six hours: a release is still seen the day it lands, four requests a day sit far under
/// GitHub's 60-per-hour unauthenticated limit, and the app does not poll a service for an event that has not
/// happened. Never more often than hourly, by construction.</summary>
internal sealed class UpdateSchedule : IDisposable
{
    /// <summary>How long between checks while the process lives: six hours (the reason is in the type's remark).
    /// The sleep case belongs to the resume hook, so this only has to be short enough that a long-running session
    /// hears about a release the same day, and long enough that the app is not a poller.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromHours(6);

    private readonly Func<Task> _check;
    private readonly Action<UpdateInfo> _announce;
    private readonly TimeSpan _period;
    // The cadence is the shared pool-thread PeriodicSchedule (created disarmed; armed by Start), the same one
    // implementation the telemetry poll uses — see PeriodicSchedule for why the trigger must not be a UI-thread
    // DispatcherTimer. This type keeps only what is genuinely its own: the resume hook and the anti-duplicate
    // announcement ledger.
    private readonly PeriodicSchedule _schedule;
    private readonly ResumeWatcher _resume;
    private readonly object _announcedGate = new();

    // The version last announced to the user — the whole of the anti-duplicate rule (see <see cref="Announce"/>).
    // Session-scoped deliberately: it is what keeps a periodic check from re-announcing the same release into the
    // notification list the UI is building.
    private string? _announced;

    // 0/1 single-flight guard for CheckNow (Interlocked: a full fence, so no separate volatile is needed).
    private int _inFlight;
    private volatile bool _disposed;

    /// <param name="check">The check itself, awaited. Must not throw: a throw is swallowed and the next tick is the
    /// retry (see <see cref="RunAsync"/>).</param>
    /// <param name="announce">What a found release means for the user. Reached at most once per version per
    /// process, and from whatever thread the check completed on, so it must marshal to the UI itself.</param>
    /// <param name="period">Only the tests pass this (a period has to be exercised in milliseconds; six hours is
    /// the production value). Defaults to <see cref="Period"/>, which is what the app gets.</param>
    internal UpdateSchedule(Func<Task> check, Action<UpdateInfo> announce, TimeSpan? period = null)
    {
        _check = check;
        _announce = announce;
        _period = period ?? Period;
        _schedule = new PeriodicSchedule(CheckNow, _period);
        _resume = new ResumeWatcher(CheckNow);
    }

    /// <summary>Start the schedule: the first check runs NOW — that is the startup check this type replaces, kept
    /// as the schedule's first tick so replacing the one-shot call loses nothing — then every
    /// <see cref="Period"/>, and on every wake from sleep. No idempotence guard: there is one caller and one
    /// process, and a second Start would only double the resume subscription.</summary>
    public void Start()
    {
        _resume.Start();
        _schedule.Start();
        CheckNow();
    }

    /// <summary>Run one check now, unless one is already in flight or the app is shutting down. Called by the
    /// timer, by the resume hook, by <see cref="Start"/> for the startup check, and by
    /// <see cref="OnWindowShown"/> for the show trigger.</summary>
    public void CheckNow()
    {
        if (_disposed) return;   // a check found during teardown has nowhere to report to (see Dispose)
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;   // in flight -> skip, do not queue
        _ = RunAsync();
    }

    /// <summary>The SHOW/RESTORE trigger: the flyout was brought up onto the screen (a hidden-to-visible
    /// transition — the tray icon / its "Show" item, the Nitro hotkey, the update tray item, a rebuild reopening
    /// what was open). This is the fourth way a long-lived session learns a release exists, and it exists because
    /// the owner opens the app when they sit down at it rather than at any cadence the timer knows. It adds NO
    /// mechanism of its own: it is <see cref="CheckNow"/> under a name that says who called it, so it inherits
    /// BOTH existing guards — the single-flight guard means showing the window while a check is already running
    /// costs nothing and starts nothing, and <see cref="Announce"/> means a check that finds the same release the
    /// previous show already reported does not announce it again. So a user opening and closing the flyout all
    /// afternoon is quiet after the first time a release is news, and only a genuinely newer release gets through.</summary>
    public void OnWindowShown() => CheckNow();

    /// <summary>Announce a found release, once. "Already announced" means the VERSION STRING
    /// (<see cref="UpdateInfo.Version"/> — the human tag with the leading 'v' trimmed, exactly what the banner
    /// shows) and not the asset URL: the asset is what a click downloads, while the announcement that must not be
    /// seen twice is about the release. NOTHING CLEARS IT but a different version, which replaces it and is
    /// announced in turn — so a newer release always gets through, and a check that finds nothing cannot resurrect
    /// an already-announced one, because it never gets here at all (<see cref="UpdateChecker.CheckAsync"/> returns
    /// null for "already current", for offline and for a failed call alike, and the caller's null guard is what
    /// keeps that path away from this method).
    ///
    /// The remembered version is the LAST one, not a set of all of them: with <c>releases/latest</c> the sequence
    /// is monotone, and the one case a set would also cover — a release retracted and then re-published — would
    /// cost state that never stops growing for a notification that is, in that moment, true again.</summary>
    public void Announce(UpdateInfo info)
    {
        lock (_announcedGate)
        {
            if (_announced == info.Version) return;
            _announced = info.Version;
        }
        // Outside the lock: the announcement marshals and does work, and none of that is the memory's business.
        _announce(info);
    }

    // The scheduled check, with the single-flight guard around it. A throw is swallowed rather than allowed to
    // escape: nobody awaits `_ = RunAsync()`, and an unobserved exception on a pool thread takes the process down.
    // UpdateChecker already degrades every failure to "no update"; this is the outer net for a delegate that does
    // not, and the next tick is the retry.
    private async Task RunAsync()
    {
        try { await _check(); }
        catch { }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    /// <summary>Stop the schedule and release the wake hook. The timer goes first and the flag before it, so a tick
    /// that is already queued goes quiet instead of raising a banner over a window that is being torn down; the
    /// watcher's Linux half owns a <c>gdbus</c> child process, which must not outlive the app. Called from
    /// AppController.ExitApp beside the other timers and coordinators, the same teardown habit as theirs.</summary>
    public void Dispose()
    {
        _disposed = true;
        _schedule.Dispose();
        _resume.Dispose();
    }
}
