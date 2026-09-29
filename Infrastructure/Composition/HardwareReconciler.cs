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
/// exactly as it did when the site called the axis itself — UNLESS the axis's own target already caught its port's
/// throw. The writes reach their ports through the mode-apply use cases, whose targets catch a throwing port and
/// discard it (the axes are SYSTEM paths with no reader; see <c>IGpuOcModeTarget.ApplyCurrentMode</c> and
/// docs/open-decisions.md §2's 2026-09-28 note). What this class must not do is add a catch of its own: the
/// sites' guarantees are genuinely different and are NOT reconciled here: <c>ApplyStartupState</c> has no catch at
/// all, <c>BackgroundPass</c>'s catch covers its whole pass and not just the axes, and the resume handler's covers
/// its three axes together. A single catch in this class would unify all three — which the plan names as a change,
/// not an improvement. The one throw that still reaches a caller is a non-port fault (a programming error below
/// the use case), which no axis swallows.</item>
/// <item><b>It does not take a lock.</b> <c>ApplyModeFan</c> is the one axis that takes <c>_state</c> while
/// calling its port, and it does that inside <c>LaptopService</c>, where the reason is recorded
/// (docs/open-decisions.md §3). Nothing here holds a lock across a hardware call.</item>
/// <item><b>It does not drive a UI-owned axis.</b> The lighting axis is the coordinator's repaint burst, with
/// its own interval and its own retry count — a policy about the user's hand and the HID bus, not about the
/// hardware. The use case skips it before this class is reached.</item>
/// </list>
///
/// IT NO LONGER CONSTRUCTS THE CALL. <see cref="ReapplySettings"/> owns this contract through ITS constructor
/// and names only the moment; this class is now purely the <see cref="IReapplyTarget"/> implementation it always
/// was, and the site-facing entry point is the use case, not a method here. Composition builds the one use case
/// over one reconciler and threads it to the sites (and the service's boot path takes it back through
/// <c>LaptopService.Reapply</c>).</summary>
internal sealed class HardwareReconciler : IReapplyTarget
{
    private readonly LaptopService _svc;
    private readonly ApplyModeFan _fan;
    private readonly ApplyModeGpuOc _gpuOc;
    private readonly ApplyModeCpuPower _cpuPower;
    private readonly ApplyModeCo _co;

    /// <summary>The mode-apply use cases are built over the service by composition (or, in a unit test, by the
    /// fixture), and they are stateless wrappers over the same targets the composition container registers — so a
    /// second instance over the same service is equivalent, and the reconciler reaching the service through the
    /// use cases rather than by name is what keeps the action methods off the service's public surface.</summary>
    internal HardwareReconciler(LaptopService service, ApplyModeFan fan, ApplyModeGpuOc gpuOc,
                                ApplyModeCpuPower cpuPower, ApplyModeCo co)
    {
        _svc = service;
        _fan = fan;
        _gpuOc = gpuOc;
        _cpuPower = cpuPower;
        _co = co;
    }

    /// <summary>Drive one axis against its port, and (for the three axes that have one) report the value the UI
    /// should show for it, in the domain's vocabulary. The writes are the mode-apply use cases' (Application),
    /// which own the target through their constructors. A THROWING port is caught at each axis's target — these
    /// are SYSTEM paths with no reader for the failure, and a throw must not escape a background re-apply
    /// (docs/open-decisions.md §2, the applied-edit refinement of 2026-09-28).</summary>
    public void Apply(ModeAxis axis, ReapplyOutcome outcome)
    {
        switch (axis)
        {
            // A mode with no stored preset leaves the fans alone and reports nothing — the null the UI reads as
            // "leave the section alone" — and the GPU axis reports stock instead, because an unconfigured mode
            // is definitely stock (the driver zeroes its offsets) and switching to it must clear whatever the
            // previous mode applied. Both are the use cases' own behaviour, unchanged.
            case ModeAxis.Fans:     if (_fan.Run() is { } fan) outcome.Fan = fan; return;
            case ModeAxis.GpuOc:    outcome.GpuOc = _gpuOc.Run(); return;
            case ModeAxis.CpuPower: outcome.CpuPower = _cpuPower.Run(); return;
            // The Curve Optimizer's apply reports nothing HERE even though its target returns the preset it
            // applied: the UI's row is filled from the STORED value on the caller's thread (see Reflect), because
            // this axis is the deferred one and the mode-switch site reflects what the user configured rather than
            // what the SMU accepted. It is also silent about failure on purpose — no caller of the CO apply has
            // ever read one.
            case ModeAxis.Co:       _co.Run(); return;
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
