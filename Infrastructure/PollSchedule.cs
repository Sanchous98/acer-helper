namespace AcerHelper.Infrastructure;

/// <summary>The FULL refresh poll's cadence and name — the one thing the heavy pass needs beyond the shared
/// <see cref="PeriodicSchedule"/> it is built on: a POOL-THREAD trigger, not a UI-thread <c>DispatcherTimer</c>,
/// so a visible window's rendering/input cannot starve it. The mechanism and the GLib priority-inversion reason
/// live in <see cref="PeriodicSchedule"/>; this is a NAMED INSTANCE of that one implementation, not a second
/// one — it states the three-second period and delegates Start/PollNow/Dispose straight through. The period
/// lives here rather than in <c>AppController</c> because the poll's cadence is a property of the poll, and the
/// tests pin it.
///
/// THE TELEMETRY CADENCE IS SPLIT, and this is only the slow half. The full pass reads profiles and sensors over
/// the EC/WMI gate, drives the fans and can restore a power-source mode, so it stays at seconds. The battery's
/// live power/charge row is read by the much cheaper <see cref="BatteryPollSchedule"/> at one second; the two
/// are independent schedules, so neither can hold up the other.</summary>
internal sealed class PollSchedule : IDisposable
{
    /// <summary>The production cadence of the FULL pass: three seconds. The pass reads profiles and sensors over
    /// the EC/WMI gate and drives the fans, so it must not be re-issued while the previous burst is still
    /// settling. The battery row is the separate, cheaper <see cref="BatteryPollSchedule"/> at one second; that
    /// bounds the latency to a gauge change at one second rather than this pass's three (the gauge itself is
    /// coarse - see <see cref="BatteryPollSchedule"/>).</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(3);

    /// <summary>THE GAP THAT MEANS THE MACHINE WAS ASLEEP. A pool timer does not fire while the machine is
    /// suspended, so a tick that arrives far later than <see cref="Period"/> is the app's own evidence of a wake
    /// - and it is the ONLY reliable one: a Modern-Standby session does not always deliver the OS's resume
    /// event, and when it does not, the lighting stays as the firmware left it (the EC defaults its
    /// operating-mode slot to amber) until the next profile switch. The gap is measured between the END of one
    /// tick and the START of the next, so a tick that runs long (a stalled EC read) is not mistaken for a sleep.
    ///
    /// Ten seconds is three poll periods plus slack: long enough that a busy machine is not read as asleep, short
    /// enough that the lighting is back before the owner has looked at the keyboard.</summary>
    public static readonly TimeSpan WakeGap = TimeSpan.FromSeconds(10);

    private readonly Action _tick;
    private readonly Action? _onGap;
    private readonly PeriodicSchedule _schedule;
    private DateTime _lastTickEnd = DateTime.UtcNow;

    /// <param name="tick">What one interval does. Must be safe to call from a pool thread - the app's tick only
    /// kicks <c>AppController.Refresh</c> and the self-throttling gate-stats write, both of which are.</param>
    /// <param name="period">Only the tests pass this (a cadence has to be exercised in milliseconds; seconds is
    /// the production value). Defaults to <see cref="Period"/>, which is what the app gets.</param>
    /// <param name="onGap">What a WAKE looks like from here: called when the gap since the previous tick ended is
    /// at least <see cref="WakeGap"/>, i.e. the machine was asleep and the app is back. Null when the caller has
    /// no use for it.</param>
    internal PollSchedule(Action tick, TimeSpan? period = null, Action? onGap = null)
    {
        _tick = tick;
        _onGap = onGap;
        _schedule = new PeriodicSchedule(Tick, period ?? Period);
    }

    // One tick, with the wake gap measured around it. The gap is read BEFORE the tick runs and the stamp is
    // taken AFTER it, so what is compared is "the machine was idle between two ticks" - not "the tick was late".
    private void Tick()
    {
        var gap = DateTime.UtcNow - _lastTickEnd;
        if (_onGap is { } onGap && IsWakeGap(gap)) onGap();
        try { _tick(); }
        finally { _lastTickEnd = DateTime.UtcNow; }
    }

    /// <summary>Whether a gap between two ticks is a WAKE. Pure and static, so the rule can be asserted without
    /// a timer: the threshold is <see cref="WakeGap"/>, and a gap under it is an ordinary late tick.</summary>
    internal static bool IsWakeGap(TimeSpan gap) => gap >= WakeGap;

    /// <summary>Begin ticking every <see cref="Period"/>. The first tick is one interval away; the app's initial
    /// poll is its own explicit call, so this adds no surprise pass at startup.</summary>
    public void Start() => _schedule.Start();

    /// <summary>Run one tick now, on the calling thread. The seam that lets a test step the schedule by hand and
    /// observe a read without racing the timer (the same reason <see cref="UpdateSchedule.CheckNow"/> exists).
    /// Exceptions are handled exactly as on the timer path: swallowed, never propagated.</summary>
    internal void PollNow() => _schedule.TriggerNow();

    /// <summary>Stop the schedule. Called from <c>AppController.ExitApp</c> beside the other
    /// timers/coordinators.</summary>
    public void Dispose() => _schedule.Dispose();
}
