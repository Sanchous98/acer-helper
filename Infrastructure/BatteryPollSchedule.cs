namespace AcerHelper.Infrastructure;

/// <summary>WHEN the fast battery-telemetry poll runs — the LIGHT half of the telemetry cadence, split from the
/// full <see cref="PollSchedule"/> refresh because the two halves cost very different things. The live power /
/// charge row is the reading the user watches change, and one second lowers the LATENCY to a change to ≤1 s;
/// the full pass reads profiles and sensors through the EC/WMI gate, drives the fans and can restore a
/// power-source mode, which is neither needed nor safe at that rate. So the two are two named instances of the
/// same <see cref="PeriodicSchedule"/> rather than one cadence for everything.
///
/// ONE SECOND IS NOT A PROMISE THAT THE VALUE MOVES EVERY SECOND. The reading's cadence is the OS fuel gauge's,
/// and on the AN18-61 that gauge is coarse: percent and <c>CallNtPowerInformation</c>'s Rate change in ~0.1 Wh
/// steps, so the same value comes back for ten to twenty seconds at a time even though this schedule fires
/// every second (measured: 17 ticks in 18 s, Rate constant at -40.3 W throughout). Polling faster than the
/// gauge cannot make it change faster — it only means a change is picked up within a second of happening,
/// instead of within the full pass's three. The user should therefore expect the power row to move on the
/// gauge's cadence (seconds to tens of seconds), not on this timer's.
///
/// WHY ONE SECOND IS CHEAP ENOUGH. What this poll runs reads ONLY the battery: on Windows
/// <c>GetSystemPowerStatus</c> plus <c>CallNtPowerInformation(SystemBatteryState)</c> — two syscalls and no WMI
/// transaction (the process-wide WMI/EC gate is deliberately avoided; health and cycle count were cached once at
/// construction), and on Linux a handful of <c>/sys/class/power_supply</c> file reads. That is a battery gauge
/// poll, not a hardware sweep. The schedule is pool-triggered and single-flight (see
/// <see cref="PeriodicSchedule"/>), so a slow read skips the next tick instead of overlapping it, and a throwing
/// read is swallowed with the next interval as the retry.
///
/// The full <see cref="PollSchedule"/> stays at its own (slower) period; the two are independent, so a stalled
/// EC read in the heavy pass cannot hold up the live power row, nor the reverse.</summary>
internal sealed class BatteryPollSchedule : IDisposable
{
    /// <summary>The production cadence: one second. Short enough to bound the latency to a gauge change at one
    /// second, long enough that the two cheap OS calls behind one pass are not hammered. Deliberately a distinct,
    /// named constant rather than a literal at the call site.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    private readonly PeriodicSchedule _schedule;

    /// <param name="tick">What one interval does. Must be safe to call from a pool thread — the app's tick only
    /// reads the battery and posts the resulting snapshot to the UI thread.</param>
    /// <param name="period">Only the tests pass this (a cadence has to be exercised in milliseconds; one second
    /// is the production value). Defaults to <see cref="Period"/>, which is what the app gets.</param>
    internal BatteryPollSchedule(Action tick, TimeSpan? period = null)
        => _schedule = new PeriodicSchedule(tick, period ?? Period);

    /// <summary>Begin ticking every <see cref="Period"/>. The first tick is one interval away; the app fills the
    /// row immediately with its own explicit read rather than leaving it on the placeholder until then.</summary>
    public void Start() => _schedule.Start();

    /// <summary>Run one tick now, on the calling thread. The seam that lets a test step the schedule by hand and
    /// observe one read without racing the timer (the same reason <see cref="PollSchedule.PollNow"/> exists).
    /// Exceptions are handled exactly as on the timer path: swallowed, never propagated.</summary>
    internal void PollNow() => _schedule.TriggerNow();

    /// <summary>Stop the schedule. Called from <c>AppController.ExitApp</c> beside the other timers.</summary>
    public void Dispose() => _schedule.Dispose();
}
