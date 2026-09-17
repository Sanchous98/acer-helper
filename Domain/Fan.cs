namespace AcerHelper.Domain;

/// <summary>
/// One fan of this machine as the app drives it in Custom mode: the duty curve it follows, the interpolation
/// that turns a temperature into a duty%, and the last duty the app actually applied to it. The curve and its
/// memory belong to the fan they are about, so they live here rather than in the controller that walks them.
///
/// IT IS A VIEW OVER <see cref="FanPreset"/>, AND THAT IS THE FROZEN SCHEMA TALKING. The preset holds BOTH
/// fans' settings in one object keyed per performance mode, and that shape is a compatibility surface: a
/// committed fixture (tests/AcerHelper.Tests/Fixtures/settings-0.33.0.json, guarded by JsonSettingsStoreTests)
/// makes renaming any part of it a decision rather than an edit. So a fan does not own its stored curve — it
/// reads its own half of the preset, chosen once at construction, because the CPU and the GPU are the only two
/// halves the schema has. Everything else about the curve IS the model's: the anchors, the default ramp, the
/// interpolation, and the duty memory.
///
/// WHAT IT DOES NOT DECIDE: whether a duty is written at all. The controller's deadband is decided for the PAIR
/// of fans at once and returns both duties when it is left (see <see cref="FanCurveEngine"/>), because the port
/// writes a pair. A fan answers what its duty is and remembers what was applied to it; the decision to write
/// belongs to the pair.
/// </summary>
public sealed class Fan
{
    /// <summary>Fixed temperature anchors (°C) a curve is defined at; a curve is one duty% per anchor, per
    /// fan. The single source of truth — the UI reads these so the graph and the controller can't drift.</summary>
    public static readonly int[] Anchors      = [50, 60, 70, 80, 90];
    public static readonly int[] DefaultCurve = [30, 45, 60, 80, 100];

    private readonly bool _gpu;     // which half of the preset this fan reads
    private int _last = -1;         // last duty% applied to THIS fan (-1 = none yet)

    /// <param name="gpu"><c>true</c> for the GPU fan, <c>false</c> for the CPU one — the preset's two halves.</param>
    public Fan(bool gpu) => _gpu = gpu;

    /// <summary>The last duty% the caller committed to this fan, or -1 when nothing has been committed yet.
    /// Read by <see cref="FanCurveEngine"/>, whose deadband is decided on both fans at once and therefore needs
    /// both memories; nothing else has any use for a single fan's half of that pair.</summary>
    internal int LastApplied => _last;

    /// <summary>Clear this fan's memory so its next <see cref="Duty"/> applies unconditionally. Called on any
    /// out-of-band change (mode/preset switch) so a deadband referring to the previous preset's duties cannot
    /// swallow the first legitimate write.</summary>
    public void Reset() => _last = -1;

    /// <summary>Record the duty the caller actually applied to this fan — call it only on a successful write,
    /// so the deadband references what the fan is really at.</summary>
    public void Commit(int duty) => _last = duty;

    /// <summary>The duty% this fan should be at for the live temperature: its own curve's value when its curve
    /// is on, else its own fixed speed clamped into the duty range. An unreadable temperature (-1, and anything
    /// below) holds the last committed duty when there is one, so one bad sample cannot move a fan.</summary>
    public int Duty(FanPreset preset, int tempC)
    {
        var (useCurve, curve, fixedDuty) = Stored(preset);
        return useCurve ? EvalCurve(curve, tempC, _last) : Math.Clamp(fixedDuty, 0, 100);
    }

    /// <summary>This fan's half of the persisted preset — the ONE place the frozen field layout is read, so a
    /// reader of the other half cannot be mistaken for this one.</summary>
    private (bool UseCurve, int[] Curve, int FixedDuty) Stored(FanPreset p)
        => _gpu ? (p.GpuUseCurve, p.GpuCurve, p.Gpu) : (p.CpuUseCurve, p.CpuCurve, p.Cpu);

    /// <summary>Interpolate a duty% for <paramref name="temp"/> from the per-anchor curve (linear between
    /// anchors, flat beyond the ends). Unknown temperature (-1) holds the last value (or the idle duty).</summary>
    internal static int EvalCurve(int[] duties, int temp, int fallback)
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
