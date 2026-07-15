namespace AcerHelper.Features;

/// <summary>Emulated fan-curve controller. Acer has no native fan curves, so in Custom mode the app drives
/// each fan's duty from a duty%-per-temperature-anchor curve, evaluated against live temps on the sensor
/// loop. Owns the fixed anchors, the default ramp, and the per-fan hysteresis/deadband so the fans don't
/// hunt. Stateful — it remembers the last duty applied to each fan; call <see cref="Reset"/> whenever the
/// inputs change out of band (mode/preset switch) so the deadband can't suppress the first legitimate write.
/// State advances only when the caller confirms an apply via <see cref="Commit"/>, so a failed hardware
/// write is retried on the next step rather than silently swallowed by the deadband.</summary>
public sealed class FanCurveEngine
{
    /// <summary>Fixed temperature anchors (°C) a curve is defined at; a curve is one duty% per anchor, per
    /// fan. The single source of truth — the UI reads these so the graph and the controller can't drift.</summary>
    public static readonly int[] Anchors      = [50, 60, 70, 80, 90];
    public static readonly int[] DefaultCurve = [30, 45, 60, 80, 100];

    private int _cpu = -1, _gpu = -1;   // last duty% applied per fan (-1 = none yet)

    /// <summary>Clear the hysteresis state so the next <see cref="Step"/> applies unconditionally.</summary>
    public void Reset() => _cpu = _gpu = -1;

    /// <summary>Record the duties the caller actually applied to the hardware (call only on a successful
    /// write, so the deadband references what the fans are really at).</summary>
    public void Commit(int cpu, int gpu) { _cpu = cpu; _gpu = gpu; }

    /// <summary>The (cpu, gpu) duty for the live temps, or null when it's within the deadband of the last
    /// committed pair (no change needed). A fan whose curve is off uses its fixed speed. Does NOT update
    /// state — the caller applies the result and then calls <see cref="Commit"/>.</summary>
    public (int cpu, int gpu)? Step(FanPreset f, SensorSnapshot s)
    {
        int cpu = f.CpuUseCurve ? EvalCurve(f.CpuCurve, s.CpuTempC, _cpu) : Math.Clamp(f.Cpu, 0, 100);
        int gpu = f.GpuUseCurve ? EvalCurve(f.GpuCurve, s.GpuTempC, _gpu) : Math.Clamp(f.Gpu, 0, 100);
        if (_cpu >= 0 && Math.Abs(cpu - _cpu) < 4 && Math.Abs(gpu - _gpu) < 4) return null;   // deadband
        return (cpu, gpu);
    }

    /// <summary>Interpolate a duty% for <paramref name="temp"/> from the per-anchor curve (linear between
    /// anchors, flat beyond the ends). Unknown temperature (-1) holds the last value (or the idle duty).</summary>
    private static int EvalCurve(int[] duties, int temp, int fallback)
    {
        var a = Anchors;
        if (duties == null || duties.Length < a.Length) duties = DefaultCurve;
        if (temp < 0)      return fallback >= 0 ? fallback : Math.Clamp(duties[0], 0, 100);
        if (temp <= a[0])  return Math.Clamp(duties[0], 0, 100);
        if (temp >= a[^1]) return Math.Clamp(duties[^1], 0, 100);
        for (int i = 1; i < a.Length; i++)
            if (temp <= a[i])
            {
                int d0 = duties[i - 1], d1 = duties[i];
                return Math.Clamp(d0 + (d1 - d0) * (temp - a[i - 1]) / (a[i] - a[i - 1]), 0, 100);
            }
        return Math.Clamp(duties[^1], 0, 100);
    }
}
