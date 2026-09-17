using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>Who re-applies volatile hardware state after a boot, a mode switch or a wake.
///
/// WHY THIS EXISTS. The operation used to be written out by hand in four places — <c>ApplyStartupState</c>, the
/// refresh pass, the resume handler and the Acer device constructor — each with its own axis list (or, for the
/// device constructor, its own boot-state write), its own order, its own thread and its own catch. They
/// disagreed, and nothing could see it: <see cref="ModeAxisTable"/> stated the schedule and no production code
/// read it. This is the one place that executes the schedule, so a site now names only the MOMENT it is (a
/// <see cref="ReapplyTrigger"/>) and takes the axes and the order from the domain, and the thread from the
/// dispatch rule stated below.
///
/// WHAT IT DELIBERATELY DOES NOT DO — three things, each of which would be a silent failure:
/// <list type="number">
/// <item><b>It does not catch.</b> An axis driven on the caller's thread throws straight through to the site,
/// exactly as it did when the site called the axis itself. The sites' guarantees are genuinely different and
/// are NOT reconciled here: <c>ApplyStartupState</c> has no catch at all, <c>BackgroundPass</c>'s catch covers
/// its whole pass and not just the axes, and the resume handler's covers its three axes together. A single
/// catch in this class would unify all three — which the plan names as a change, not an improvement, and it
/// would also move an exception that currently reaches the AppController constructor.</item>
/// <item><b>It does not take a lock.</b> <c>ApplyModeFan</c> is the one axis that takes <c>_state</c> while
/// calling its port, and it does that inside <c>LaptopService</c>, where the reason is recorded
/// (docs/open-decisions.md §3). Nothing here holds a lock across a hardware call.</item>
/// <item><b>It does not drive a UI-owned axis.</b> The lighting axis is the coordinator's repaint burst, with
/// its own interval and its own retry count — a policy about the user's hand and the HID bus, not about the
/// hardware. The schedule names it so that "which layer puts this axis back" reads off one list; the loop below
/// skips it.</item>
/// </list></summary>
internal sealed class HardwareReconciler
{
    private readonly LaptopService _svc;

    internal HardwareReconciler(LaptopService service) => _svc = service;

    /// <summary>Re-apply every axis <paramref name="trigger"/> schedules, in the order <see cref="Schedule"/>
    /// states them, and return the values the calling site's UI pass reflects.
    ///
    /// Callers and what they discard: the mode-change site consumes the outcome (it feeds the view-models on its
    /// UI pass), the startup and resume sites discard it.</summary>
    internal ReapplyOutcome Reapply(ReapplyTrigger trigger)
    {
        var outcome = new ReapplyOutcome();
        List<ModeAxis>? deferred = null;

        foreach (var axis in Schedule(trigger))
        {
            if (ModeAxisTable.Owner(axis) != ReassertOwner.Hardware) continue;

            if (RunsOffTheCallersThread(trigger, axis))
            {
                // The Curve Optimizer is the one axis whose UI value is NOT the return of its apply: the apply
                // is deferred, so the row is filled here instead, from the STORED value — which is what it has
                // always shown (the refresh pass read CurrentCoDomains() at this exact point before the deferral
                // existed). Read before the task starts, so the two happen in the order they always have.
                if (axis == ModeAxis.Co && Reflects(trigger)) outcome.Co = _svc.CurrentCoDomains();
                (deferred ??= []).Add(axis);
                continue;
            }
            ReapplyAxis(axis, outcome);
        }

        if (deferred is not null)
            _ = Task.Run(() =>
            {
                // The sink is thrown away: a deferred axis has no UI value to report — the Curve Optimizer's row
                // was read on the caller's thread above. ONE catch for the whole deferred set, which is the
                // guarantee each site already had (resume had exactly these three calls under one catch). A
                // failure stops the rest and is never retried by this operation; the next trigger re-asserts.
                // Swallowed rather than surfaced because an escaping throw from here would be an unobserved task
                // exception, i.e. nothing at all.
                try { var sink = new ReapplyOutcome(); foreach (var axis in deferred) _ = ReapplyAxis(axis, sink); }
                catch { /* the next boot, mode switch or wake re-asserts */ }
            });

        return outcome;
    }

    /// <summary>The axes a trigger drives, in the order it drives them — including the ones a layer above owns,
    /// because WHICH layer drives an axis is <see cref="ModeAxisTable.Owner"/> and stating it twice is how the two
    /// facts drift apart. The executing layer skips what is not its own.
    ///
    /// WHY THIS IS HERE AND NOT ON THE TABLE IN <c>Domain/</c>. A set of actions is a use case, and a use case is
    /// Application — that is the owner's rule, and the schedule is the plan of action this class executes. The
    /// TABLE keeps the facts about the problem, which are the domain's: which axes carry a preset, what an absent
    /// one means for each (<see cref="ModeAxisTable.Policy"/>, <see cref="ModeAxisTable.PolicyWhenNoModeWasEverConfigured"/>),
    /// which the platform forgets (<see cref="ModeAxisTable.IsVolatile"/>) and who re-asserts it
    /// (<see cref="ModeAxisTable.Owner"/>). This method is a reader of those, not a second copy: the lists below
    /// are BUILT from <see cref="ModeAxisTable.All"/> and <see cref="ModeAxisTable.IsVolatile"/> rather than
    /// written out a third time. A hand-written copy of the volatility set is the disease the table exists to
    /// cure, and it had already been written twice (the prose blocks it replaced, and the sites' own axis lists).
    ///
    /// Boot and wake drive exactly the volatile set, in <see cref="ModeAxisTable.All"/> order — that is what
    /// <see cref="ModeAxisTable.IsVolatile"/> means ("the app must re-assert it after a reboot, a resume or a
    /// driver reload"), so spelling the list out could only be a chance to disagree with it. A mode switch drives
    /// every axis, which is why <see cref="ModeAxisTable.All"/> is documented as the order a mode switch applies
    /// them. On a wake the lighting axis comes FIRST and on a mode switch LAST: the wake path repaints before it
    /// re-asserts the hardware, and the mode-switch path repaints after it.</summary>
    internal static IReadOnlyList<ModeAxis> Schedule(ReapplyTrigger trigger) => trigger switch
    {
        ReapplyTrigger.Startup => [.. ModeAxisTable.All.Where(ModeAxisTable.IsVolatile)],
        ReapplyTrigger.ModeChange => ModeAxisTable.All,
        ReapplyTrigger.Resume => [ModeAxis.Lights, .. ModeAxisTable.All.Where(ModeAxisTable.IsVolatile)],
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "a new ReapplyTrigger needs a schedule here"),
    };

    /// <summary>Drive one axis against its port, and (for the three axes that have one) report the value the UI
    /// should show for it. The return value is always discarded — the arms write into the outcome, and a switch
    /// expression needs to produce something.</summary>
    private object? ReapplyAxis(ModeAxis axis, ReapplyOutcome outcome) => axis switch
    {
        ModeAxis.Fans => outcome.Fan = _svc.ApplyModeFan(),
        ModeAxis.GpuOc => outcome.GpuOc = _svc.ApplyModeGpuOc(),
        ModeAxis.CpuPower => outcome.CpuPower = _svc.ApplyModeCpuPower(),
        ModeAxis.Co => _svc.ApplyModeCo(),
        // Covers the lighting axis — re-applied by the UI, and skipped before the switch is ever reached — and
        // with it any sixth axis. A domain that grows one without an operation here therefore throws the FIRST
        // time the new axis lands in a trigger's schedule, which is what the schedule test drives, rather than
        // re-applying nothing. The compiler cannot be the guard: an exhaustive switch expression over an enum
        // still needs this arm to build at zero warnings (CS8524, the unnamed-value case).
        _ => throw new InvalidOperationException($"the {axis} axis has no re-apply operation here"),
    };

    /// <summary>Whether this trigger's site must have the axis driven somewhere other than the thread it fires
    /// on. Each answer is a measured fact about that site, and stated here once instead of at each call site:
    ///
    /// <list type="bullet">
    /// <item><b>Startup and mode change</b> — only the Curve Optimizer, whose SMU mailbox transaction waits on
    /// the machine-wide PCI access lock that HWiNFO/Ryzen Master/RyzenAdj also take, so it can block for
    /// seconds. The rest of those two schedules is a fast NvAPI call and a powrprof transaction, and they stay
    /// on the calling thread. That is load-bearing beyond latency: neither site has a catch around an inline
    /// axis, so a port that throws there must still reach it.</item>
    /// <item><b>Resume</b> — the whole hardware schedule, because the trigger fires on the UI thread and the EC
    /// can stall right after wake. One task for all of them, so the three keep their relative order.</item>
    /// </list></summary>
    private static bool RunsOffTheCallersThread(ReapplyTrigger trigger, ModeAxis axis) => trigger switch
    {
        ReapplyTrigger.Startup or ReapplyTrigger.ModeChange => axis == ModeAxis.Co,
        ReapplyTrigger.Resume => true,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "a new ReapplyTrigger needs a dispatch decision here"),
    };

    /// <summary>Whether this trigger's caller hands the outcome to a UI pass. Only the mode-change pass does;
    /// startup and resume discard it. The distinction is not cosmetic — filling the Curve Optimizer's row means
    /// reading its stored domains, and that read takes the current mode key under <c>_state</c>, which is an EC
    /// transaction. Doing it for a caller that throws the answer away would be a new hardware read on the UI
    /// thread at every boot, not a free no-op.</summary>
    private static bool Reflects(ReapplyTrigger trigger) => trigger == ReapplyTrigger.ModeChange;
}

/// <summary>What asks for volatile state to be put back. These are the three moments
/// <c>docs/state-and-events.md</c> calls a re-apply rather than a read — a boot, a mode switch and a wake — and
/// each drives a different set of axes. A trigger is therefore a fact about the SCHEDULE, not about any one axis:
/// "which axes are volatile" is <see cref="ModeAxisTable.IsVolatile"/>, "what puts them back" is
/// <see cref="HardwareReconciler.Schedule"/>.
///
/// It lives beside the schedule rather than with the axes because a set of actions is a use case: the axes and
/// their policies are the domain's facts, and which ones a given moment drives is this layer's plan of action.</summary>
public enum ReapplyTrigger
{
    /// <summary>The app has just started: the driver has zeroed the GPU offsets, the SMU holds stock and the OS
    /// power overlay is the firmware's. The EC has NOT forgotten the fan mode, so that axis is not in this
    /// trigger's schedule.</summary>
    Startup,

    /// <summary>The performance mode changed — a pick, the Turbo key, or a power-source restore. Every axis
    /// takes the new mode's preset, absent or not: that is what stops a mode inheriting the previous one's
    /// settings.</summary>
    ModeChange,

    /// <summary>Wake from sleep or hibernation. The same volatile set as a boot (the dGPU power-cycles and comes
    /// back at zero offset) plus the lighting, which the EC drops over suspend.</summary>
    Resume,
}

/// <summary>What a re-apply left for the calling site's UI pass to show: one member per axis that can report a
/// value, left null for an axis this trigger did not drive (and for the fan axis when the mode has no preset,
/// which the UI reads as "leave the section alone" — the same null it has always received). Discarded by the
/// startup and resume sites, which have no UI pass of their own.</summary>
internal sealed class ReapplyOutcome
{
    internal FanPreset? Fan { get; set; }
    internal GpuOcPreset? GpuOc { get; set; }
    internal string? CpuPower { get; set; }
    internal int[]? Co { get; set; }
}
