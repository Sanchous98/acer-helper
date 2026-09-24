using System.Threading;

namespace AcerHelper.Infrastructure;

/// <summary>THE one periodic-timer implementation for the whole app — the cadence of anything that has to happen
/// again and again — with the two delivery modes the app needs and no third copy of the pattern anywhere. The
/// telemetry poll, the update check and every UI-thread debounce/animation are all this type; the named
/// schedules (<see cref="PollSchedule"/>, <see cref="UpdateSchedule"/>) are policies/instances built on it, not
/// parallel mechanisms.
///
/// WHY IT EXISTS, and why it is not a bare <c>DispatcherTimer</c>. A <c>DispatcherTimer</c> created with its
/// default constructor runs at <c>DispatcherPriority.Background</c>. On the GLib/X11 backend that priority is a
/// <c>g_idle_add</c> source at <c>G_PRIORITY_DEFAULT_IDLE</c> — the LOWEST priority, below the render/input
/// sources — so while a visible window has rendering or input to do the tick is deferred and only runs once the
/// UI has nothing higher-priority left, which is exactly the state a hidden or minimized window is in. The
/// telemetry poll froze at its last value on screen (the live power row made it visible) and a fan animation
/// stutters for the same reason: the trigger was on the dispatcher's idle queue. The trigger therefore comes
/// from a pool-thread <see cref="System.Threading.Timer"/>, which is immune to the priority inversion, and UI
/// work is <em>marshalled</em> to the UI thread instead of being scheduled on the idle queue.
///
/// TWO MODES, ONE TYPE:
/// <list type="bullet">
/// <item><b>Pool</b> (the two-argument constructor): the tick runs on the timer's own pool thread. This is the
/// mode for work that touches no UI — the three-second telemetry poll and the six-hourly update check. It is
/// the shape <see cref="PollSchedule"/> and <see cref="UpdateSchedule"/> used to own separately.</item>
/// <item><b>UI</b> (the three-argument constructor): the tick is marshalled through the supplied
/// <c>uiDispatch</c> delegate, so it runs on the UI thread with an EXPLICIT priority chosen at the call site.
/// The app's UI sites pass a <c>Dispatcher.UIThread.Post(action, priority)</c> poster (Render for an animation,
/// Normal for ordinary work). The delegate is injected rather than a direct <c>Dispatcher.UIThread.Post</c> call
/// for the same reason <c>OptionsAssembler</c> takes a <c>post</c>: this type lives in Infrastructure, which may
/// not name the UI toolkit at all (<c>ArchitectureMapTests.NoLayerBelowTheUiImportsAvalonia</c>), and a test can
/// pass a synchronous poster to drive the schedule with no dispatcher loop.</item>
/// </list>
///
/// A FAILING TICK IS NOT A STOPPED SCHEDULE. The tick is wrapped, so an exception from one pass (a transient
/// ACPI/EC/WMI stall, a UI callback that throws) is swallowed and the next interval is the retry; an unhandled
/// exception on a pool-timer callback would otherwise be an unobserved pool exception (process death), and a
/// tick the timer never re-armed is a feature that dies silently.
///
/// ONE TICK AT A TIME. In pool mode a tick that arrives while the previous one is still running is skipped, not
/// overlapped (a sample is a sample; two samples racing on the same hardware is worse than one missed interval).
/// In UI mode at most one tick is queued on the dispatcher at a time, so a busy UI thread coalesces rather than
/// piling up a burst. A <see cref="Stop"/> invalidates a tick already queued (the generation check), so stopping
/// really stops — which the resettable debounces and the bounded lighting burst rely on.
///
/// Created DISARMED (no due time): a schedule that is never started never fires, so whether a feature is on is
/// decided by the app rather than by a timer that starts itself in a constructor. The first tick is one interval
/// away; a caller that needs one now (startup) calls <see cref="TriggerNow"/> itself.</summary>
internal sealed class PeriodicSchedule : IDisposable
{
    private readonly Action _tick;
    private readonly TimeSpan _period;
    private readonly Action<Action>? _uiDispatch;   // non-null => UI mode: marshal each tick through this
    private readonly Timer _timer;

    private int _generation;   // bumped by Start/Stop, so a tick queued before a Stop is dropped when it runs
    private int _uiPending;    // UI mode: 0/1, one tick is queued on the dispatcher and has not run yet
    private int _inFlight;     // 0/1, a tick is running (single-flight)
    private volatile bool _running;
    private volatile bool _disposed;

    /// <summary>Pool mode: the tick runs on the timer's own thread.</summary>
    /// <param name="tick">What one interval does. Must be safe to call from a pool thread.</param>
    /// <param name="period">The cadence. The tests pass milliseconds; the app passes seconds/hours.</param>
    internal PeriodicSchedule(Action tick, TimeSpan period)
    {
        _tick = tick;
        _period = period;
        _timer = new Timer(_ => Run());
    }

    /// <summary>UI mode: the tick is marshalled through <paramref name="uiDispatch"/> so it runs on the UI
    /// thread. The delegate encodes the tick's priority — the UI sites pass a
    /// <c>Dispatcher.UIThread.Post(action, priority)</c> poster (Render for an animation, Normal for ordinary
    /// work); a test may pass one that runs the action synchronously.</summary>
    /// <param name="tick">What one interval does. Must be safe to call from the UI thread.</param>
    /// <param name="period">The cadence.</param>
    /// <param name="uiDispatch">Marshals one tick to the UI thread.</param>
    internal PeriodicSchedule(Action tick, TimeSpan period, Action<Action> uiDispatch)
    {
        _tick = tick;
        _period = period;
        _uiDispatch = uiDispatch;
        _timer = new Timer(_ => Run());
    }

    /// <summary>Begin ticking every period. Bumps the generation so a tick queued by a previous run cannot
    /// surface after a restart.</summary>
    public void Start()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _generation);
        _running = true;
        _timer.Change(_period, _period);
    }

    /// <summary>Stop ticking, and invalidate a UI tick that is already queued but has not run yet. A stopped
    /// schedule can be started again.</summary>
    public void Stop()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _generation);
        _running = false;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Re-arm the whole period from now. The resettable debounces call this on every edit, which is
    /// what makes their interval "quiet time since the last change" rather than a fixed grid.</summary>
    public void Restart() { Stop(); Start(); }

    /// <summary>True from <see cref="Start"/> until <see cref="Stop"/> — i.e. a tick is still owed. Read by a
    /// resettable debounce to tell "the user's edit has not been applied yet" (mirrors
    /// <c>DispatcherTimer.IsEnabled</c>, which is what this replaces; <c>LightViewModel.AdoptBrightness</c> uses
    /// it to refuse a hardware read that raced ahead of a pending edit).</summary>
    public bool IsRunning => _running;

    /// <summary>Run one tick now, on the calling thread, bypassing the timer. The seam that lets a test step
    /// the schedule by hand and observe one pass without racing the timer (the same reason
    /// <see cref="PollSchedule.PollNow"/> and <see cref="UpdateSchedule.CheckNow"/> exist). Exceptions are
    /// handled exactly as on the timer path: swallowed, never propagated.</summary>
    internal void TriggerNow() => Dispatch(Volatile.Read(ref _generation));

    // The timer callback. Pool mode runs the tick here; UI mode queues at most one tick and returns, so the
    // pool thread never touches UI state.
    private void Run()
    {
        if (_disposed) return;
        if (_uiDispatch is { } dispatch)
        {
            if (Interlocked.CompareExchange(ref _uiPending, 1, 0) != 0) return;   // one queued tick at a time
            var generation = Volatile.Read(ref _generation);
            dispatch(() =>
            {
                Interlocked.Exchange(ref _uiPending, 0);
                Dispatch(generation);
            });
            return;
        }
        Dispatch(Volatile.Read(ref _generation));
    }

    // The tick itself, on whichever thread is the right one for this mode. Single-flight; a tick queued under an
    // older generation (stopped/restarted since) is dropped here rather than run.
    private void Dispatch(int generation)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        try
        {
            if (generation != Volatile.Read(ref _generation)) return;
            _tick();
        }
        catch { /* a transient failure is routine; the next interval is the retry */ }
        finally { Interlocked.Exchange(ref _inFlight, 0); }
    }

    /// <summary>Stop the schedule for good and release the timer.</summary>
    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
