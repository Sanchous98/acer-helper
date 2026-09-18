using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What a FAN edit needs from whoever owns the fan graph and the EC — the contract for one of the things
/// the owner's model applies («применение кривой вентилятора»), declared here and implemented by the layer that
/// owns both the stored preset and the port (Infrastructure/Composition/LaptopService.Fans.cs). The axes differ in
/// what they WRITE and not in how they are driven, which is why the re-apply operation needs one contract for five
/// axes (<see cref="IReapplyTarget"/>); here the thing applied is one axis, and it is one contract.
///
/// WHY THERE IS A READ AND TWO WRITES. The domain rule a fan edit carries is READ-MODIFY-WRITE, and stating it is
/// the reason this contract exists at all: a curve edit names ONE fan, so the other fan's half and the mode must
/// survive it, and a mode edit names the mode and the two fixed speeds, so each fan's curve settings must survive
/// it. Both survival rules used to be the first two lines of a method body, and both are now stated once, in
/// Application, where a stub pins them without Infrastructure (<see cref="ApplyFanCurve"/>,
/// <see cref="ApplyFanSelection"/>).
///
/// <see cref="ReplaceCurve"/> AND <see cref="ReplaceSelection"/> ARE TWO MEMBERS AND NOT ONE, because the EC is
/// asked something DIFFERENT after each kind of edit, and that difference is measured rather than stylistic. A
/// curve edit drives the fans from the curves and pushes NOTHING when the mode is not Custom — <c>ApplyCustom</c>'s
/// own guard returns before it touches the port, so a curve edited in Auto produces no EC traffic at all. A mode
/// edit puts the EC into the mode instead, unless the new selection IS Custom, which goes through the same curve
/// pass rather than being pushed as a speed. One member would have to re-derive which edit happened from the
/// stored state, and that is not derivable: the same state is the result of both (a curve edit in Custom mode and
/// a Custom selection with the same speeds write the same preset).
///
/// EACH WRITE MEMBER IS ATOMIC BY ITSELF. Both hold the graph's lock from the stored write through the EC call —
/// which is what both edit paths have always done, and is one of the three deliberate holds
/// (docs/open-decisions.md §3): the deadband is cleared and the write driven inside one hold, so a background
/// <c>ApplyCustom</c> cannot be interleaved between its <c>Step</c> and its <c>Commit</c>. What IS newly separate
/// is <see cref="Stored"/> from the write, and that window is bounded by measurement rather than by hope: the only
/// writer of a fan preset is the UI thread — the refresh pass reads one and writes the deadband engine, never the
/// graph — and both edit paths read and write on the caller's own thread, so no competing edit can land between
/// the two calls.
///
/// THE STATE CROSSES IN DOMAIN VOCABULARY (<see cref="FanAxisState"/>) and not as the stored preset, because the
/// use cases are in Application, which may not name the container
/// (<c>ArchitectureMapTests.ApplicationPointsAtDomainNotInfrastructure</c>).</summary>
public interface IFanAxisTarget
{
    /// <summary>The current mode's stored fan state, in the domain's vocabulary. CREATED ON FIRST WRITE, which is
    /// this axis's read and has been since the per-mode graph was introduced: both edits mean "the user is
    /// configuring this mode", so the first edit of a mode must start from the defaults rather than be refused.
    ///
    /// The arrays it carries are copies of the stored ones, because <see cref="FanSettings"/> copies the curve it
    /// is given — and that is the rule rather than a cost: a state that leaves an edit is a value, and a value
    /// that shares its array with the settings graph is one a caller could rewrite the user's file through. What
    /// the copying changes on the write side is only WHICH array the preset ends up holding: the edit hands back a
    /// state built from this one, and the preset's field is re-pointed at the copy. Its CONTENTS are unchanged,
    /// and the array it replaces was read by this same call, so nothing that could still be looking at it exists.
    /// The snapshot rule (<c>LaptopService.CurrentFan</c>) is still a separate, stronger rule for readers that
    /// KEEP a value — it duplicates the whole preset, arrays included.</summary>
    FanAxisState Stored();

    /// <summary>Make <paramref name="state"/> the current mode's stored fan state, clear the deadband and drive
    /// the curves from it, as ONE hold of the graph lock. A mode that is not Custom writes nothing to the EC, as
    /// the curve pass's own guard decides.</summary>
    void ReplaceCurve(FanAxisState state);

    /// <summary>The same store and deadband reset, for a mode selection: the EC is then put into
    /// <paramref name="state"/>'s behaviour, unless that mode is Custom — which is driven from the curves by the
    /// same pass instead, because the EC honours a manual speed only once the fans are in Custom behaviour, so
    /// setting a speed without switching behaviour first is silently ignored.</summary>
    void ReplaceSelection(FanAxisState state);
}

/// <summary>The fan curve as a use case: turn ONE fan's curve on or off and store its points, for the CURRENT
/// mode, and make it take effect now.
///
/// WHAT IT DECIDES, and it is the whole of what a curve edit is: which half of the stored state the edit belongs
/// to, and that the REST of that state — the other fan's half, its curve and both fixed speeds, and the mode —
/// is carried over UNTOUCHED. A fan's identity is a fact about the machine (Domain/Fan.cs has no CPU/GPU flag to
/// set), so "this is the GPU fan's curve" is decided here and nowhere else, and a swap of the two halves is what a
/// stub test for this use case is for.
///
/// THE CURVE ARRAY CROSSES THE DOMAIN'S DOOR, which is where its shape is settled: the state is rebuilt through
/// <see cref="FanSettings"/>'s constructor rather than by assigning the array onto a copy of the stored data, so
/// a curve that is not one duty% per anchor — or one carrying a duty outside 0..100 — is REFUSED here, and the
/// array the model ends up holding is a copy the caller cannot write through. That is deliberately not
/// validation in this use case: the rule is the domain's, the UI's own editor cannot produce a violating curve
/// (FansViewModel builds exactly one clamped point per anchor), and this is only the call that happens to reach
/// the constructor first.</summary>
public static class ApplyFanCurve
{
    public static void Run(bool gpu, bool use, int[] points, IFanAxisTarget target)
    {
        var stored = target.Stored();
        target.ReplaceCurve(gpu
            ? stored with { Gpu = new FanSettings(use, points, stored.Gpu.FixedDuty) }
            : stored with { Cpu = new FanSettings(use, points, stored.Cpu.FixedDuty) });
    }
}

/// <summary>The fan selection as a use case: the mode and the two fixed speeds, for the CURRENT mode, effective
/// now. The sibling of <see cref="ApplyFanCurve"/> over the same contract and for the same axis — a fan is
/// configured one of these two ways and never both at once.
///
/// WHAT IT DECIDES: that a selection names the mode and the two speeds and nothing else — each fan's curve and
/// its curve switch are carried over from the stored state, which is why the two fans below are rebuilt with the
/// stored <c>UseCurve</c> and the stored <c>Curve</c> and only their speed replaced. The stored speeds are read
/// from the graph rather than defaulted, so a selection on a mode that was never configured starts from the
/// preset's own defaults instead of from zero.
///
/// WHAT IT DOES NOT DECIDE: how the selection reaches the EC. That a Custom selection is driven from the curves
/// rather than pushed as a speed is <see cref="IFanAxisTarget.ReplaceSelection"/>'s doing, and it is the EC's own
/// rule rather than this use case's.</summary>
public static class ApplyFanSelection
{
    public static void Run(FanMode mode, byte cpu, byte gpu, IFanAxisTarget target)
    {
        var stored = target.Stored();
        target.ReplaceSelection(stored with
        {
            Mode = mode,
            // Rebuilt through the constructor rather than written onto a copy: FanSettings' members are get-only
            // precisely so the two rules it enforces — a real curve and a speed that is a duty% — cannot be
            // stepped around by the `with` expression that used to sit here.
            Cpu = new FanSettings(stored.Cpu.UseCurve, stored.Cpu.Curve, cpu),
            Gpu = new FanSettings(stored.Gpu.UseCurve, stored.Gpu.Curve, gpu),
        });
    }
}
