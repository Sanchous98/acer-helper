namespace AcerHelper.Infrastructure;

/// <summary>WHEN the power-source (barrel vs USB-C PD) read runs: on its OWN schedule, never on the fast 1 Hz
/// battery poll. The value comes from the device's declared <c>Battery.PowerSource</c> op, which on a vendor
/// backend is a heavier transport transaction than the cheap OS gauge — an Acer EC HID SEND-then-GET on the bus
/// the RGB controller and the performance-envelope writes share, and the property a plugin exposes through
/// <c>Capability.Battery</c>/<c>ReadPowerSource</c> (docs/vendor-plugins.md §3.5). Putting it on
/// <see cref="BatteryPollSchedule"/> would make that transaction every second, which is exactly what that file
/// says the fast path must not do (it reads only cheap OS syscalls). So the two are separate named schedules;
/// the battery card's fast row keeps its 1 Hz OS gauge and this row gets a slower, heavier cadence.
///
/// WHY FIVE SECONDS, and not the full pass's three. A power source changes on a PHYSICAL event (a plug or an
/// unplug), not as a value that churns between polls, so the cadence only has to bound how soon the user sees
/// the change; five seconds is comfortably "shortly after". It is deliberately SLOWER than the three-second full
/// <see cref="PollSchedule"/> and OFF the one-second battery grid, so the app does not carry two heavy reads at
/// the same rate — the profile writes already own the vendor bus at their own times, and this read must not add
/// contention to them.
///
/// SINGLE-FLIGHT, like every <see cref="PeriodicSchedule"/>: a slow read skips the next tick rather than
/// overlapping it, and a throwing read is swallowed with the next interval as the retry. A failed or absent read
/// leaves the property's op returning <c>PowerSource.Unknown</c>, which the view-model treats as "hide the row"
/// (see BatteryViewModel.SetPowerSource) — never a guessed value.
///
/// CREATED BY THE APP ONLY WHEN THE DEVICE EXPOSES THE PROPERTY. On a machine without it
/// (<c>Battery.PowerSource == null</c>) no schedule is built at all, so nothing ticks and nothing reads.</summary>
internal sealed class PowerSourceSchedule : IDisposable
{
    /// <summary>The production cadence: five seconds. Slower than the full pass (three seconds) and slower than
    /// the battery row (one second) on purpose — see the type doc. Deliberately a distinct, named constant rather
    /// than a literal at the call site.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(5);

    private readonly PeriodicSchedule _schedule;

    /// <param name="tick">What one interval does. Must be safe to call from a pool thread — the app's tick reads
    /// the power source off the UI thread and posts the resulting enum to the UI thread.</param>
    /// <param name="period">Only the tests pass this (a cadence has to be exercised in milliseconds; five seconds
    /// is the production value). Defaults to <see cref="Period"/>, which is what the app gets.</param>
    internal PowerSourceSchedule(Action tick, TimeSpan? period = null)
        => _schedule = new PeriodicSchedule(tick, period ?? Period);

    /// <summary>Begin ticking every <see cref="Period"/>. The first tick is one interval away; the app fills the
    /// row immediately with its own explicit read rather than leaving it on the placeholder until then.</summary>
    public void Start() => _schedule.Start();

    /// <summary>Run one tick now, on the calling thread. The seam that lets a test step the schedule by hand and
    /// observe one read without racing the timer (the same reason <see cref="BatteryPollSchedule.PollNow"/>
    /// exists). Exceptions are handled exactly as on the timer path: swallowed, never propagated.</summary>
    internal void PollNow() => _schedule.TriggerNow();

    /// <summary>Stop the schedule. Called from <c>AppController.ExitApp</c> beside the other timers.</summary>
    public void Dispose() => _schedule.Dispose();
}
