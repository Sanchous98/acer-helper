using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What re-applying this machine's settings needs from whoever owns the hardware and the stored
/// presets. ONE contract with the axes as ARGUMENTS, and the size is the point rather than an accident: a
/// contract per axis would be five interfaces of one method each — the schedule re-stated as type declarations
/// — while what the use case asks for is the same of every axis, because the axes differ in what they WRITE
/// and not in how they are driven.
///
/// The two methods are the schedule's two questions, and they are genuinely different questions. <see
/// cref="Apply"/> writes the hardware and reports what it left; <see cref="Reflect"/> answers WITHOUT writing,
/// for an axis whose apply was handed to another thread and whose row must therefore be filled from what is
/// STORED.
///
/// WHO IMPLEMENTS IT: the layer that owns the hardware and the settings graph
/// (Infrastructure/Composition/HardwareReconciler.cs), and nothing in this file knows that a stored preset or
/// a vendor port exists — which is what lets the use case live here at all. See
/// <see cref="ReapplyPlan"/> for why the plan and the executor are split this way.</summary>
public interface IReapplyTarget
{
    /// <summary>Apply one axis to the hardware for the CURRENT mode, and record in <paramref name="outcome"/>
    /// the value the calling site's UI pass must show for it — leaving that member alone where the axis has
    /// nothing to report, which is the same null the sites have always received (no preset for this mode, or
    /// no port at all).</summary>
    void Apply(ModeAxis axis, ReapplyOutcome outcome);

    /// <summary>The value an axis must show when its apply did NOT run on this thread, read on the caller's
    /// thread BEFORE the deferred apply is handed off — which is where every site has always read it.
    ///
    /// The reason it is a separate question rather than part of <see cref="Apply"/> is not tidiness: this read
    /// takes the current mode key under the hardware's lock on one of the axes, so performing it for a trigger
    /// that discards the outcome would be a new hardware transaction on the boot path.
    ///
    /// An axis with no STORED value to show does nothing here, and that is the answer for every axis but the
    /// Curve Optimizer today: the others' UI values come from their apply, and the one whose apply is deferred
    /// is the one whose row has to come from somewhere else.</summary>
    void Reflect(ModeAxis axis, ReapplyOutcome outcome);
}

/// <summary>What a re-apply left for the calling site's UI pass to show: one member per axis that can report a
/// value, left null for an axis this trigger did not drive — and for the fan axis also when the mode has no
/// preset, which the UI reads as "leave the section alone" (the same null it has always received). Discarded by
/// the startup and resume sites, which have no UI pass of their own.
///
/// The members are DOMAIN values (<see cref="FanAxisState"/>, <see cref="GpuAxisState"/>, the overlay id and
/// the offsets the UI renders its rows from) and not the stored presets, because this is the use case's result
/// and the use case is in Application, which may not name the container
/// (<c>ArchitectureMapTests.ApplicationPointsAtDomainNotInfrastructure</c>). The conversion is the implementing
/// layer's, and it happens where the preset is read — one place per axis.</summary>
public sealed class ReapplyOutcome
{
    public FanAxisState? Fan { get; set; }
    public GpuAxisState? GpuOc { get; set; }
    public string? CpuPower { get; set; }
    /// <summary>Index-aligned with the port's voltage domains, exactly as the row renders them — the same
    /// array the accessor builds, so nothing is copied or reshaped on the way to the UI.</summary>
    public int[]? Co { get; set; }
}

/// <summary>The use case the owner's model calls for at startup: put this machine's settings back in the order
/// <see cref="ReapplyPlan"/> states, over the contract above. It is the one member of the operation that could
/// move here WHOLE, and what made that possible is worth stating: it needs no stored container, no vendor port
/// and no <c>CoAxis</c> by name — only a schedule, the axes that schedule names, and a target that knows how to
/// write them. Everything the other use cases of this layer would need is on the far side of a wall — the walls
/// that hold it there are listed in docs/open-decisions.md's note on the retired analysis — and this one is on
/// the near side of it.
///
/// WHAT IT DECIDES, and each of the three was made by hand at four separate sites before they were gathered
/// here (the sites now name only the MOMENT they are, a <see cref="ReapplyTrigger"/>):
/// <list type="number">
/// <item><b>Which axes run at all.</b> Only the ones <see cref="ModeAxisTable.Owner"/> gives to the hardware —
/// the lighting axis is the UI's repaint burst, with its own interval and retry count, and the loop skips it.
/// The schedule still NAMES it, so "which layer puts this axis back" reads off one list.</item>
/// <item><b>Which of them run on another thread</b>, from <see cref="ReapplyPlan.RunsOffTheCallersThread"/>,
/// and that they go as ONE task so their relative order survives and they share one catch — the guarantee the
/// resume site already had.</item>
/// <item><b>What escapes.</b> Nothing here catches: an axis driven on the caller's thread throws straight
/// through to the site, exactly as it did when the site called the axis itself. A single catch around both
/// would unify three sites whose guarantees are genuinely different — <c>ApplyStartupState</c> has no catch at
/// all, the refresh pass's covers its whole pass, and the resume handler's covers its three axes together —
/// which the plan names as a change rather than an improvement.</item>
/// </list>
///
/// NO LOCK IS TAKEN HERE. One axis (<c>ApplyModeFan</c>) takes the machine's state lock while calling its port,
/// and it does that inside the layer that owns the lock; nothing in this file holds one across a hardware
/// call.</summary>
public static class ReapplySettings
{
    /// <summary>Drive every axis <paramref name="trigger"/> schedules, in the order
    /// <see cref="ReapplyPlan.Schedule"/> states them, and return the values the calling site's UI pass
    /// reflects. Callers and what they discard: the mode-change site consumes the outcome (it feeds the
    /// view-models on its UI pass), the startup and resume sites discard it.</summary>
    public static ReapplyOutcome Run(ReapplyTrigger trigger, IReapplyTarget target)
    {
        var outcome = new ReapplyOutcome();
        List<ModeAxis>? deferred = null;

        foreach (var axis in ReapplyPlan.Schedule(trigger))
        {
            if (ModeAxisTable.Owner(axis) != ReassertOwner.Hardware) continue;

            if (ReapplyPlan.RunsOffTheCallersThread(trigger, axis))
            {
                // The reflected value is read BEFORE the task starts, so the two happen in the order they
                // always have at the mode-change site.
                if (ReapplyPlan.Reflects(trigger)) target.Reflect(axis, outcome);
                (deferred ??= []).Add(axis);
                continue;
            }
            target.Apply(axis, outcome);
        }

        if (deferred is not null)
            _ = Task.Run(() =>
            {
                // The sink is thrown away: a deferred axis has no UI value to report — the one that has a row
                // was read on the caller's thread above. ONE catch for the whole deferred set, which is the
                // guarantee each site already had (resume had exactly these calls under one catch). A failure
                // stops the rest and is never retried by this operation; the next trigger re-asserts.
                // Swallowed rather than surfaced because an escaping throw from here would be an unobserved
                // task exception, i.e. nothing at all.
                try { var sink = new ReapplyOutcome(); foreach (var axis in deferred) target.Apply(axis, sink); }
                catch { /* the next boot, mode switch or wake re-asserts */ }
            });

        return outcome;
    }
}
