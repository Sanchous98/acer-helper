using System.Collections.Immutable;

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
///
/// IT HOLDS BOTH FIELDS OF A FAN'S SPEED IN A STATE NO CALLER CAN PUT IT OUT OF: a curve is exactly one duty%
/// per anchor and every duty% is inside 0..100, and a fixed speed is a duty% too. Both are refused or corrected
/// at CONSTRUCTION (the constructor below), so "the fan is at 300" and "the curve is four entries long" are not
/// states this type can carry — they are states a hand-edited file can be in, and the file's own repair is the
/// load-time sanitiser (Infrastructure/Composition/JsonSettingsStore.cs).
///
/// THE MEMBERS ARE GET-ONLY, which is load-bearing rather than tidy: with an <c>init</c> accessor a caller could
/// write <c>settings with { Curve = whatever }</c> and put the type back into a state the constructor refuses —
/// which is precisely what the curve edit in Application/FanAxis.cs used to do. Every construction goes through
/// the constructor, so every value this type holds was checked by it.
/// </summary>
public readonly record struct FanSettings
{
    /// <summary>Drive this fan from <see cref="Curve"/> rather than from <see cref="FixedDuty"/> — the per-fan
    /// half of the preset's <c>*UseCurve</c> flag.</summary>
    public bool UseCurve { get; }

    /// <summary>One duty% per <see cref="Fan.Anchors"/> entry, every one of them in 0..100, and a COPY of
    /// whatever the caller handed over — see <see cref="FanSettings"/>'s own note and the constructor.
    ///
    /// A curve that is not exactly that is REFUSED rather than repaired, and the tolerance this type used to
    /// document (a null or short array silently becoming <see cref="Fan.DefaultCurve"/>) is now the LOAD-TIME
    /// sanitiser's job: it replaces a malformed stored curve with the default and rewrites settings.json, so a
    /// user with a bad curve in their file loses that one curve instead of the whole file. What it can no longer
    /// do is reach the model — <see cref="Fan.EvalCurve"/>'s fallback is kept as a last resort that no longer has
    /// a way in.</summary>
    public int[] Curve { get; }

    /// <summary>The stored fixed speed, CLAMPED INTO THE DUTY RANGE HERE — so no caller can hold, persist or write
    /// a speed that is not a duty%. The clamp lives in this constructor and not at any call site because this is
    /// where the invariant can be stated once: the EC takes a byte, and two members of
    /// Infrastructure/Composition/LaptopService.Fans.cs handed it a raw <c>(byte)</c> of this value read straight
    /// out of the stored preset — a conversion that turns a stored 300 into 44.
    ///
    /// MEASURED, AND NARROWER THAN IT LOOKS: that pair of casts is inert today. <c>ApplyFan</c> discards the two
    /// speeds unless the mode is Custom, and the Custom path goes through the curve evaluation, which clamps — so
    /// no out-of-range speed ever reached the wire, and what this clamp changes is the FILE: a hand-edited 300 is
    /// brought to 100 when the preset is next written, so the graph stops holding a number the hardware can never
    /// be told. Read only when <see cref="UseCurve"/> is off.</summary>
    public int FixedDuty { get; }

    /// <summary>A fan's data — the only way to build one. <paramref name="curve"/> must be a curve
    /// (<see cref="Fan.IsValidCurve"/>) and <paramref name="fixedDuty"/> is clamped; the curve is copied, so the
    /// array this holds is never the caller's and never the stored preset's — the aliasing that used to run both
    /// ways (the preset's array handed in, the same array handed back out) has no way to happen here.</summary>
    public FanSettings(bool useCurve, int[] curve, int fixedDuty)
    {
        if (!Fan.IsValidCurve(curve))
            throw new ArgumentException(
                "a fan curve is one duty% per anchor — " + $"{Fan.Anchors.Length} entries, each inside 0..100; "
                + (curve is null ? "got null" : $"got {curve.Length} entries"), nameof(curve));

        UseCurve = useCurve;
        Curve = [.. curve];
        FixedDuty = Math.Clamp(fixedDuty, 0, 100);
    }
}

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
    /// fan. The single source of truth — the UI reads these so the graph and the controller can't drift.
    ///
    /// IMMUTABLE AND NOT MERELY <c>readonly</c>. Both of these were <c>public static readonly int[]</c>, which
    /// freezes the REFERENCE and not the elements: <c>Fan.Anchors[0] = -999</c> was legal from anywhere in the
    /// assembly, and UI/ViewModels/FansViewModel.cs holds these very instances, so a write through one would have
    /// moved the graph the user drags and the anchors the controller interpolates at once. No writer existed, so
    /// this was a trap rather than a live bug — which is the kind that is cheapest to close before one does.
    /// <see cref="ImmutableArray{T}"/> keeps both readers' shape (<c>[i]</c> and <c>.Length</c>) and removes the
    /// mutation.</summary>
    public static readonly ImmutableArray<int> Anchors      = [50, 60, 70, 80, 90];
    public static readonly ImmutableArray<int> DefaultCurve = [30, 45, 60, 80, 100];

    /// <summary>Whether <paramref name="duties"/> is a curve this domain admits: exactly one duty% per anchor and
    /// every one of them inside 0..100. THE ONE STATEMENT OF THAT INVARIANT — <see cref="FanSettings"/>'s
    /// constructor enforces it, and the load-time sanitiser asks it about a stored curve before deciding whether
    /// to replace one (Infrastructure/Composition/JsonSettingsStore.cs). Asking it in either place alone would
    /// let the two answers drift, which is how a file that one of them thinks is fine becomes a throw in the
    /// sensor loop.</summary>
    public static bool IsValidCurve(int[]? duties)
    {
        if (duties is null || duties.Length != Anchors.Length) return false;
        foreach (var duty in duties)
            if (duty is < 0 or > 100)
                return false;
        return true;
    }

    /// <summary>A FRESH array holding the built-in ramp, for the two callers that need a curve they may store:
    /// a preset that has never been configured (Infrastructure/Composition/Settings.cs) and the load-time
    /// sanitiser's replacement. A copy rather than <see cref="DefaultCurve"/> itself, so a stored preset is never
    /// the immutable ramp and no later write can go through one array into every other holder of it.</summary>
    public static int[] DefaultDuties() => [.. DefaultCurve];

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
                              // The clamp is a last resort the constructor has already made unnecessary
                              // (FanSettings.FixedDuty), and it is kept for the reason the curve's fallback is:
                              // a duty the EC would take as a byte must not depend on that one guard holding.
                              : Math.Clamp(_settings.FixedDuty, 0, 100);

    /// <summary>Interpolate a duty% for <paramref name="temp"/> from the per-anchor curve (linear between
    /// anchors, flat beyond the ends). Unknown temperature (-1) holds the last value (or the idle duty).
    ///
    /// THE FALLBACK LINE IS NOW UNREACHABLE, and it is kept rather than deleted because deleting a working guard
    /// to make a shape tidier is how a later widening of the type's own rules becomes a duty read out of a null
    /// array. It used to be the load-bearing answer for a hand-edited or half-deserialised settings file; that
    /// case is now handled before the model ever sees it — a persisted curve that is null, short, over-long or
    /// out of range is replaced by <see cref="DefaultCurve"/> and the file is rewritten by the load-time
    /// sanitiser — and <see cref="FanSettings"/> refuses to hold a curve that is not exactly one duty% per
    /// anchor. So this line says the same thing the sanitiser does, for the case the sanitiser cannot see: a
    /// caller inside this assembly that builds a curve by hand.
    ///
    /// The last-entry reads no longer have to be the anchor's own index: the array is exactly as long as the
    /// anchors (that is the invariant above), so <c>duties[^1]</c> IS the duty at the last anchor — the
    /// asymmetry a six-entry stored curve used to expose (its tail entry taken as the flat top) is gone with
    /// the malformed curve itself.</summary>
    private static int EvalCurve(int[] duties, int temp, int fallback)
    {
        var a = Anchors;
        if (duties == null || duties.Length < a.Length) duties = [.. DefaultCurve];
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
