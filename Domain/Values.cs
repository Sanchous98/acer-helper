namespace AcerHelper.Domain;

/// <summary>A Curve-Optimizer offset in AVFS "counts" — negative = less voltage at every frequency, 0 = stock.
///
/// THE ONE THING THIS TYPE DOES IS MAKE AN OVERVOLT UNREPRESENTABLE, and that is exactly what the interface
/// already promises: <see cref="ICurveOptimizer"/> declares its range as "Min &lt; 0, Max = 0 — undervolt only",
/// because a positive offset raises voltage, which buys nothing on this hardware and is a thermal and stability
/// risk. Until now that promise lived in a comment and in clamps at the call sites — five of them, in
/// <c>LaptopService.Tuning.cs</c> and in the vendor codec — with nothing that made the invariant hold if one
/// was missed.
///
/// WHY THE CEILING IS <c>min(range.Max, 0)</c> RATHER THAN EITHER EXTREME. Taking the port's Max alone would let
/// a port that advertised positive headroom raise the voltage — the case the invariant exists to refuse. Taking a
/// hard 0 alone would ignore a rail whose range is ENTIRELY negative (a CPU whose floor and ceiling are both below
/// stock), and an existing test forbids exactly that: <c>SetCo_FollowsThePortsRange_NotAHardCodedOne</c> requires a
/// port shaped <c>(-50, -5)</c> to clamp a request of 0 down to its own ceiling of -5, because "this port cannot
/// deliver stock". The floor is <c>min(range.Min, 0)</c> for symmetry — a nonsensical port cannot widen the range
/// into positive territory from below either.
///
/// So the type enforces one thing and defers on the rest: an overvolt is unrepresentable, and everything else
/// follows the port, whose lower bound is a measured property of a particular rail (a domain's range overrides
/// the port's, and narrowing one of them to a constant would widen a rail whose silicon gives out sooner).</summary>
public readonly record struct OffsetCounts
{
    public int Counts { get; }

    private OffsetCounts(int counts) => Counts = counts;

    /// <summary>No offset at all — what a mode with no preset for this axis is put back to.</summary>
    public static readonly OffsetCounts Stock = new(0);

    /// <summary>Clamp <paramref name="raw"/> into what the hardware will accept: the rail's own bounds, except that
    /// the ceiling is never positive.</summary>
    public static OffsetCounts Clamp(int raw, (int Min, int Max) portRange)
        => new(Math.Clamp(raw, Math.Min(portRange.Min, 0), Math.Min(portRange.Max, 0)));
}
