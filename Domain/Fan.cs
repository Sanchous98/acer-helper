namespace AcerHelper.Domain;

/// <summary>
/// One fan's own data, as its caller read it: the curve this fan follows when its curve is on, and the fixed
/// duty% it holds when the curve is off.
///
/// WHICH FAN THIS DESCRIBES IS DELIBERATELY NOT REPRESENTED. A machine has two fans and only the caller knows
/// which one is the CPU and which the GPU — that is a fact about a particular machine's layout, not about what
/// a fan does — so nothing here names a position, a label or an index. The persisted container holds BOTH fans
/// in one object (and that shape is a compatibility surface), so the mapping from its two halves to two of
/// these lives with the layer that reads presets: Infrastructure/Composition/LaptopService.Fans.cs. That is also what keeps
/// Domain from naming the stored schema at all.
/// </summary>
/// <param name="UseCurve">Drive this fan from <paramref name="Curve"/> rather than from
/// <paramref name="FixedDuty"/> — the per-fan half of the preset's <c>*UseCurve</c> flag.</param>
/// <param name="Curve">One duty% per <see cref="Fan.Anchors"/> entry. A null or short array is tolerated and
/// falls back to <see cref="Fan.DefaultCurve"/> (see the curve evaluation in <see cref="Fan"/>), because a
/// deserialised or hand-edited settings file can hand one over, and nothing between there and here validates
/// it.</param>
/// <param name="FixedDuty">The stored fixed speed. Read only when <paramref name="UseCurve"/> is off, and
/// clamped into the duty range when it is.</param>
public readonly record struct FanSettings(bool UseCurve, int[] Curve, int FixedDuty);

/// <summary>
/// One fan as the app drives it in Custom mode: the data it was given — the curve it follows, whether it uses
/// that curve or its fixed duty, its fixed duty, and the last duty the app actually applied to it. The curve
/// and its memory belong to the fan they are about, so they live here rather than in the controller that walks
/// them.
///
/// WHAT IT DOES NOT KNOW. Which fan it is: there is no CPU/GPU flag, no half-of-a-preset selector and no
/// constructor argument that picks one, because a fan is a fan — it answers for its own speed and nothing
/// above it changes that. The caller decides the identity (Infrastructure/Composition/LaptopService.Fans.cs maps the preset's
/// two halves onto two of these), and the fan it hands over is told only its own data.
///
/// WHAT IT DOES NOT DECIDE. Whether a duty is written at all. The controller's deadband is decided for the PAIR
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

    private FanSettings _settings;  // the data the caller read for THIS fan, replaced whenever it re-reads
    private int _last = -1;         // last duty% applied to THIS fan (-1 = none yet)

    /// <summary>A fan holding the data its caller read for it. Nothing else is passed, and nothing about which
    /// fan this is can be: <see cref="FanSettings"/> is the whole of what a fan needs to answer for its
    /// speed.</summary>
    public Fan(FanSettings settings) => _settings = settings;

    /// <summary>Replace this fan's data with what the caller has just read for it. Called on every step, because
    /// the caller reads the preset live: a mode switch or an edit takes effect on the next step exactly as it
    /// did when the data arrived as a parameter.
    ///
    /// The duty MEMORY is deliberately not touched. It records what the hardware was last told, which the data
    /// says nothing about; clearing it here would make every step look like a first step, and the deadband —
    /// whose entire job is to suppress a write when nothing moved — would never suppress one. <see cref="Reset"/>
    /// is the one operation that clears it, and the caller calls it exactly when the fans' out-of-band state
    /// changes.</summary>
    internal void Configure(FanSettings settings) => _settings = settings;

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
    public int Duty(int tempC)
        => _settings.UseCurve ? EvalCurve(_settings.Curve, tempC, _last)
                              : Math.Clamp(_settings.FixedDuty, 0, 100);

    /// <summary>Interpolate a duty% for <paramref name="temp"/> from the per-anchor curve (linear between
    /// anchors, flat beyond the ends). Unknown temperature (-1) holds the last value (or the idle duty).</summary>
    private static int EvalCurve(int[] duties, int temp, int fallback)
    {
        var a = Anchors;
        if (duties == null || duties.Length < a.Length) duties = DefaultCurve;
        if (temp < 0)      return fallback >= 0 ? fallback : Math.Clamp(duties[0], 0, 100);
        if (temp <= a[0])  return Math.Clamp(duties[0], 0, 100);
        if (temp >= a[^1]) return Math.Clamp(duties[^1], 0, 100);
        for (var i = 1; i < a.Length; i++)
            if (temp <= a[i])
            {
                int d0 = duties[i - 1], d1 = duties[i];
                return Math.Clamp(d0 + (d1 - d0) * (temp - a[i - 1]) / (a[i] - a[i - 1]), 0, 100);
            }
        return Math.Clamp(duties[^1], 0, 100);
    }
}
