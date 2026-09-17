namespace AcerHelper.Domain;

/// <summary>The per-mode axes a performance mode carries a preset for — the five things a mode switch
/// re-applies. The list is also the enum: <see cref="ModeAxisTable.All"/> is the single place that knows the
/// set, so a sixth axis is added here and nowhere else.</summary>
public enum ModeAxis
{
    Fans,
    GpuOc,
    CpuPower,
    Co,
    Lights,
}

/// <summary>What a mode switch does when the CURRENT mode has no preset for an axis. These are three genuinely
/// different answers, and each one is forced by the hardware rather than chosen:
///
/// <list type="bullet">
/// <item><see cref="LeaveUntouched"/> — the hardware's current value cannot be read back, so "no preset" cannot
/// be interpreted as a known state; forcing a default would overwrite a setting the user made elsewhere.</item>
/// <item><see cref="ForceStock"/> — the value is definitely stock when it was never set (the driver zeroes it),
/// so the axis must be ACTIVELY written, or the mode would inherit whatever the previous mode left behind.</item>
/// <item><see cref="InheritPrevious"/> — the axis is write-only and the app is already the source of truth, so
/// it must show something; the previous mode's appearance is carried over rather than reset.</item>
/// </list></summary>
public enum EmptyAxisPolicy
{
    LeaveUntouched,
    ForceStock,
    InheritPrevious,
}

/// <summary>Who re-asserts an axis after the platform forgets it. The lighting axis is a UI-side retry policy
/// (a burst of repaints over a contended HID bus), which is a policy about the user's hand and the display, not
/// about the hardware — see docs/hardware-actor-design.md.</summary>
public enum ReassertOwner
{
    Hardware,
    Ui,
}

/// <summary>What asks for volatile state to be put back. These are the three moments
/// <c>docs/state-and-events.md</c> calls a re-apply rather than a read — a boot, a mode switch and a wake — and
/// each drives a different set of axes. A trigger is therefore a fact about the SCHEDULE, not about any one
/// axis: "which axes are volatile" is <see cref="ModeAxisTable.IsVolatile"/>, "what puts them back" is
/// <see cref="ModeAxisTable.Schedule"/>.</summary>
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

/// <summary>
/// The five per-mode axes as DATA: which "no preset" policy each one follows, which the platform forgets on a
/// reboot / resume / driver reload, and who re-asserts it.
///
/// WHY THIS EXISTS. Every fact in this table is already true and already written down — but in five blocks of
/// prose comments spread across <c>Domain/Settings.cs</c> and <c>LaptopService.Tuning.cs</c>, plus a fourth
/// copy of the volatility list in <c>Ports.cs</c>. Prose cannot be cross-checked against the code it describes,
/// and the consequence is on record: adding a sixth axis means remembering four separate places (the preset
/// bag, the accessors, the schedule, and the UI section), with no compiler and no test to catch a forgotten one.
///
/// WHAT IT IS NOT. This is not a refactor of the axes into a model: it holds no state, and it does not know how
/// to talk to a port. What it does is answer "which axes, in what order" for the operation that does — the
/// re-apply schedule (<see cref="Schedule"/>) is read by <c>Application/HardwareReconciler</c>, which is the one
/// place that executes it. Until that reconciler existed this table was read by NOTHING but the test beside it,
/// which is the drift it was written to prevent.
/// </summary>
public static class ModeAxisTable
{
    /// <summary>Every axis, in the order a mode switch applies them. The order is not decorative: the profile
    /// itself is applied first (it moves the power envelope), and the axes follow.</summary>
    public static readonly IReadOnlyList<ModeAxis> All =
        [ModeAxis.Fans, ModeAxis.GpuOc, ModeAxis.CpuPower, ModeAxis.Co, ModeAxis.Lights];

    /// <summary>The axes a trigger drives, in the order it drives them — including the ones a layer above owns,
    /// because WHICH layer drives an axis is <see cref="Owner"/> and stating it twice is how the two facts drift
    /// apart. The executing layer skips what is not its own.
    ///
    /// These lists are BUILT from <see cref="All"/> and <see cref="IsVolatile"/> rather than written out a third
    /// time. A hand-written copy of the volatility set is the disease this class exists to cure, and it had
    /// already been written twice (the prose blocks it replaced, and the sites' own axis lists).
    ///
    /// Boot and wake drive exactly the volatile set, in <see cref="All"/> order — that is what
    /// <see cref="IsVolatile"/> means ("the app must re-assert it after a reboot, a resume or a driver reload"),
    /// so spelling the list out could only be a chance to disagree with it. A mode switch drives every axis,
    /// which is why <see cref="All"/> is documented as the order a mode switch applies them. On a wake the
    /// lighting axis comes FIRST and on a mode switch LAST: the wake path repaints before it re-asserts the
    /// hardware, and the mode-switch path repaints after it.</summary>
    public static IReadOnlyList<ModeAxis> Schedule(ReapplyTrigger trigger) => trigger switch
    {
        ReapplyTrigger.Startup => [.. All.Where(IsVolatile)],
        ReapplyTrigger.ModeChange => All,
        ReapplyTrigger.Resume => [ModeAxis.Lights, .. All.Where(IsVolatile)],
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "a new ReapplyTrigger needs a schedule here"),
    };

    /// <summary>What happens when the current mode has no preset for <paramref name="axis"/>.</summary>
    public static EmptyAxisPolicy Policy(ModeAxis axis) => axis switch
    {
        // The EC's fan mode cannot be read back, so an absent preset is not a known state.
        ModeAxis.Fans => EmptyAxisPolicy.LeaveUntouched,
        // The NVIDIA driver zeroes clock offsets on every boot and driver reload -> stock is a fact.
        ModeAxis.GpuOc => EmptyAxisPolicy.ForceStock,
        // No OS power mode is forced onto a profile the user has not configured.
        ModeAxis.CpuPower => EmptyAxisPolicy.LeaveUntouched,
        // A stale undervolt carried into an unconfigured profile is an unexplained instability -> clear it.
        ModeAxis.Co => EmptyAxisPolicy.ForceStock,
        // Write-only, and the app is already the source of truth: carry the previous appearance over.
        ModeAxis.Lights => EmptyAxisPolicy.InheritPrevious,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "a new ModeAxis needs a policy here"),
    };

    /// <summary>The answer for an install where NO mode has ever had a preset for <paramref name="axis"/>.
    ///
    /// For four of the five axes this is the same answer as <see cref="Policy"/>, and collapsing them would be
    /// tempting — but the Curve Optimizer genuinely differs, and the difference is not cosmetic: an empty store
    /// means the user never opted into undervolting, so the SMU mailbox is not written at all, whereas a mode
    /// that merely lacks a preset gets stock written actively. Sending the message anyway would be traffic
    /// nobody asked for on an opcode this class of CPU does not confirm.</summary>
    public static EmptyAxisPolicy PolicyWhenNoModeWasEverConfigured(ModeAxis axis)
        => axis == ModeAxis.Co ? EmptyAxisPolicy.LeaveUntouched : Policy(axis);

    /// <summary>Whether the platform forgets this axis, so the app must re-assert it after a reboot, a resume
    /// (the dGPU power-cycles and comes back at zero offset) or a driver reload. This is also the SELECTOR for
    /// two triggers' schedules — a boot and a wake drive exactly this set (see <see cref="Schedule"/>).
    ///
    /// The fans are deliberately NOT here: the EC latches the fan mode, so it survives sleep and reboot and
    /// there is nothing to put back. The lighting axis is absent for a different reason — it IS forgotten, but
    /// by the firmware's own palette repaint rather than by a power transition, and it is re-applied by the UI
    /// (see <see cref="Owner"/>).</summary>
    public static bool IsVolatile(ModeAxis axis)
        => axis is ModeAxis.GpuOc or ModeAxis.CpuPower or ModeAxis.Co;

    /// <summary>Which layer re-asserts the axis. Lighting is the one axis whose re-apply is a UI-side retry
    /// policy; everything else is a hardware write with no timing component.</summary>
    public static ReassertOwner Owner(ModeAxis axis)
        => axis == ModeAxis.Lights ? ReassertOwner.Ui : ReassertOwner.Hardware;
}
