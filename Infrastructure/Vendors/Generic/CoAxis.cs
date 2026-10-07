using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;

namespace AcerHelper.Infrastructure.Vendors.Generic;

/// <summary>
/// The Curve-Optimizer axis's write rules, holding no state of its own: they take the rails a CPU exposes and
/// one mode's stored <see cref="CoPreset"/>, and answer what the SMU is sent and what the settings rows show.
/// No port, no lock, no I/O — <see cref="ICurveOptimizer"/> stays a transport, and the caller keeps both its
/// <c>_state</c> discipline and the decision of whether a write is attempted at all.
///
/// WHY IT EXISTS. Four rules were re-stated at every site in <c>LaptopService.Tuning.cs</c> — six copies of two
/// facts, a rail may carry bounds of its own and a rail's identity is its KEY rather than its position in a list.
/// They are stated once here and the sites ask.
///
/// WHAT IT DELIBERATELY DOES NOT DO, each of which would be a silent failure:
/// <list type="number">
/// <item><b>It holds no lock.</b> The SMU mailbox transaction waits up to five seconds on the machine-wide PCI
/// access lock, and every call site keeps that wait OUTSIDE <c>_state</c> — six of them, all verified. A type that
/// took its own lock across <c>Set</c>/<c>SetDomains</c> would recreate the stall the invariant exists to prevent,
/// inside an object where no test could see it.</item>
/// <item><b>It decides nothing about ports.</b> A null port, a count mismatch and a port that throws are the
/// caller's to refuse, and its answers differ per call site in ways pinned as shipped — for one, <c>SetCo</c>
/// persists before it looks at the port while <c>SetCoDomains</c> returns before persisting.</item>
/// <item><b>It never clamps a value it reads back.</b> A stored offset was clamped when it was written; clamping
/// it again on the way out would quietly repair a preset edited by hand instead of reporting it.</item>
/// </list>
///
/// THE ONE BOUND NOT RESTATED HERE is the ceiling's provenance: both clamps below are
/// <see cref="OffsetCounts.Clamp"/>. What this type adds is WHICH range applies to WHICH number.
/// </summary>
public sealed class CoAxis
{
    private readonly IReadOnlyList<VoltageDomain> _rails;
    private readonly (int Min, int Max) _portRange;

    /// <param name="rails">The CPU's voltage domains exactly as the port lists them, in the port's own order —
    /// every array this type returns is index-aligned with it.</param>
    /// <param name="portRange">The port-wide range, which a rail carrying no <see cref="VoltageDomain.Range"/>
    /// of its own falls back to.</param>
    public CoAxis(IReadOnlyList<VoltageDomain> rails, (int Min, int Max) portRange)
    {
        _rails = rails;
        _portRange = portRange;
    }

    /// <summary>The routing rule: which of the two writes this CPU takes. A CPU exposing rails takes one offset
    /// per rail — a hybrid part's clusters are separate rails sitting at different voltages, so one number for
    /// both is pinned by whichever gives out first — and a CPU exposing none takes a single all-core value.</summary>
    public bool UsesRails => _rails.Count > 0;

    /// <summary>Clamp one rail's request against THAT RAIL's own range where it has one, and against the port's
    /// where it does not. Per rail rather than per port because the rails are different silicon: on an APU the
    /// graphics rail moves about 5 mV per count against the cores' 2.5.</summary>
    public int ClampRail(int requested, int index)
        => OffsetCounts.Clamp(requested, _rails[index].Range ?? _portRange).Counts;

    /// <summary>The all-core half of the same rule: a CPU with no rails is bounded by the port's range and by
    /// nothing narrower — the same bound the port itself applies on the way to the SMU.</summary>
    public int ClampAllCore(int requested) => OffsetCounts.Clamp(requested, _portRange).Counts;

    /// <summary>Every rail's own clamp, index-aligned with the rails: what a per-rail write sends and files.
    ///
    /// PRECONDITION — one request per rail. The caller refuses a count mismatch before it gets here, at its own
    /// site, where that refusal is pinned against the port's shape.</summary>
    public int[] ClampEach(IReadOnlyList<int> requested)
    {
        var clamped = new int[requested.Count];
        for (var i = 0; i < clamped.Length; i++) clamped[i] = ClampRail(requested[i], i);
        return clamped;
    }

    /// <summary>The rows the settings section shows: one per rail in the port's order, or a single all-core value
    /// on a CPU without rails, so the UI renders either without knowing which path is in play.
    ///
    /// A rail's slot is named by <see cref="VoltageDomain.Key"/> and read back by that key — the hardware rail, not
    /// a position. Position must not be used: the port's list moves whenever a backend enumerates its rails in
    /// another order or relabels one (the label is display-only), and reading by position would then apply the
    /// cores' undervolt to the iGPU. A key the preset has no entry for is stock for that rail ALONE — not a
    /// neighbour's value, and not the all-core one, which means something different.</summary>
    public int[] Rows(CoPreset preset)
    {
        if (!UsesRails) return [preset.AllCore];
        var rows = new int[_rails.Count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = preset.Domains.TryGetValue(_rails[i].Key, out var counts) ? counts : 0;
        return rows;
    }

    /// <summary>The same rule on the way in: file each count under its own rail's key, so the value follows the
    /// rail wherever it moves to instead of staying at the index it was written at. PRECONDITION — the counts are
    /// already index-aligned and clamped.</summary>
    public void File(CoPreset preset, IReadOnlyList<int> counts)
    {
        for (var i = 0; i < counts.Count; i++) preset.Domains[_rails[i].Key] = counts[i];
    }

    /// <summary>What a mode change, a startup or a resume puts on the SMU: the mode's stored offsets
    /// index-aligned with the rails, or <c>null</c> for "do not talk to the SMU at all".
    ///
    /// THE NULL IS THE NEVER-CONFIGURED GUARD. An empty store says the user has never opted into undervolting, so
    /// the mailbox message would be traffic nobody asked for on an opcode this CPU does not confirm; a mode that
    /// merely LACKS a preset gets stock written actively, because the offset is SMU-resident and a stale undervolt
    /// carried into a profile the user never configured is how an unexplained instability happens.
    ///
    /// The policy is <see cref="ModeAxisTable.PolicyWhenNoModeWasEverConfigured"/> (<c>LeaveUntouched</c> for an
    /// empty store, <c>ForceStock</c> for a missing preset); this is where those words become counts, and where
    /// "leave untouched" means no message at all rather than a zero.</summary>
    public int[]? Reapply(CoPreset preset, bool storeIsEmpty) => storeIsEmpty ? null : Rows(preset);
}
