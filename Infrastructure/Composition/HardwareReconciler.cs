using AcerHelper.Application;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>WHO re-applies volatile hardware state after a boot, a mode switch or a wake — the implementation of
/// the contract the use case declares (<see cref="IReapplyTarget"/>, Application), not the use case itself.
///
/// WHY THIS EXISTS. The operation used to be written out by hand in four places — <c>ApplyStartupState</c>, the
/// refresh pass, the resume handler and the Acer device constructor — each with its own axis list (or, for the
/// device constructor, its own boot-state write), its own order, its own thread and its own catch. They
/// disagreed, and nothing could see it: <see cref="ModeAxisTable"/> stated the schedule and no production code
/// read it. This class is now the one place that knows how to WRITE each axis, so a site names only the MOMENT
/// it is (a <see cref="ReapplyTrigger"/>) and takes the axes, the order, the thread and the exception guarantee
/// from <see cref="ReapplySettings"/> — Application's, because a set of actions is a use case.
///
/// WHY THE SPLIT FALLS HERE, and it was forced rather than chosen. Driving the axes means calling this service
/// and collecting what the calling site's UI pass must show — and the axis values the UI shows are built out of
/// the persisted container's types (<c>FanPreset</c>, <c>GpuOcPreset</c>), which are this layer's. Application
/// may not name them, so the values it receives are DOMAIN ones (<see cref="ReapplyOutcome"/>), and the
/// translation from the stored preset to that vocabulary happens here, at the two accessors that already read
/// the preset for the UI (<c>LaptopService.AxisStateOf</c>).
///
/// WHAT IT DELIBERATELY DOES NOT DO — three things, each of which would be a silent failure:
/// <list type="number">
/// <item><b>It does not catch.</b> An axis driven on the caller's thread throws straight through to the site,
/// exactly as it did when the site called the axis itself. The sites' guarantees are genuinely different and
/// are NOT reconciled here: <c>ApplyStartupState</c> has no catch at all, <c>BackgroundPass</c>'s catch covers
/// its whole pass and not just the axes, and the resume handler's covers its three axes together. A single
/// catch in this class would unify all three — which the plan names as a change, not an improvement — and it
/// would also move an exception that currently reaches the AppController constructor.</item>
/// <item><b>It does not take a lock.</b> <c>ApplyModeFan</c> is the one axis that takes <c>_state</c> while
/// calling its port, and it does that inside <c>LaptopService</c>, where the reason is recorded
/// (docs/open-decisions.md §3). Nothing here holds a lock across a hardware call.</item>
/// <item><b>It does not drive a UI-owned axis.</b> The lighting axis is the coordinator's repaint burst, with
/// its own interval and its own retry count — a policy about the user's hand and the HID bus, not about the
/// hardware. The use case skips it before this class is reached.</item>
/// </list></summary>
internal sealed class HardwareReconciler : IReapplyTarget
{
    private readonly LaptopService _svc;

    internal HardwareReconciler(LaptopService service) => _svc = service;

    /// <summary>Re-apply every axis <paramref name="trigger"/> schedules, in the order
    /// <see cref="ReapplyPlan.Schedule"/> states them, and return the values the calling site's UI pass
    /// reflects. This is the site-facing entry point; the operation itself is
    /// <see cref="ReapplySettings.Run"/> (Application), and this class supplies only what it cannot:
    /// the writes.</summary>
    internal ReapplyOutcome Reapply(ReapplyTrigger trigger) => ReapplySettings.Run(trigger, this);

    /// <summary>Drive one axis against its port, and (for the three axes that have one) report the value the UI
    /// should show for it, in the domain's vocabulary.</summary>
    public void Apply(ModeAxis axis, ReapplyOutcome outcome)
    {
        switch (axis)
        {
            // A mode with no stored preset leaves the fans alone and reports nothing — the null the UI reads as
            // "leave the section alone" — and the GPU axis reports stock instead, because an unconfigured mode
            // is definitely stock (the driver zeroes its offsets) and switching to it must clear whatever the
            // previous mode applied. Both are the accessors' own behaviour, unchanged.
            case ModeAxis.Fans:     if (_svc.ApplyModeFan() is { } fan) outcome.Fan = LaptopService.AxisStateOf(fan); return;
            case ModeAxis.GpuOc:    outcome.GpuOc = LaptopService.AxisStateOf(_svc.ApplyModeGpuOc()); return;
            case ModeAxis.CpuPower: outcome.CpuPower = _svc.ApplyModeCpuPower(); return;
            // The Curve Optimizer's apply reports nothing HERE even though it returns the preset it applied:
            // the UI's row is filled from the STORED value on the caller's thread (see Reflect), because this
            // axis is the deferred one and the mode-switch site reflects what the user configured rather than
            // what the SMU accepted. It is also silent about failure on purpose — no caller of ApplyModeCo has
            // ever read one.
            case ModeAxis.Co:       _svc.ApplyModeCo(); return;
            // Covers the lighting axis — re-applied by the UI, and skipped before this switch is ever reached —
            // and with it any sixth axis. A domain that grows one without an operation here therefore throws
            // the FIRST time the new axis lands in a trigger's schedule, which is what the schedule test drives,
            // rather than re-applying nothing.
            default: throw new InvalidOperationException($"the {axis} axis has no re-apply operation here");
        }
    }

    /// <summary>The Curve Optimizer's stored rows, for the mode-switch site's UI pass. The other axes have no
    /// stored value to report here: the two that can be deferred on that trigger are the fans and the GPU
    /// offsets, and their values come from their apply — which is what the sites have always shown. Reading
    /// the rows takes the current mode key under <c>_state</c>, which is why the use case only asks for them
    /// when the trigger's caller actually reflects the outcome.</summary>
    public void Reflect(ModeAxis axis, ReapplyOutcome outcome)
    {
        if (axis == ModeAxis.Co) outcome.Co = _svc.CurrentCoDomains();
    }
}
