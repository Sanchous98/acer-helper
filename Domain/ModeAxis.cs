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
/// WHAT IT IS NOT. This is not a refactor of the axes into a model, and nothing in the product reads it yet. It
/// is the one place the five policies and the volatility set are stated, so that the test beside it can assert
/// them against the service's OBSERVED behaviour — which is the only reason it earns its place over the
/// comments it replaces. Prose that a test cannot check is what let the drift go unnoticed.
/// </summary>
public static class ModeAxisTable
{
    /// <summary>Every axis, in the order a mode switch applies them. The order is not decorative: the profile
    /// itself is applied first (it moves the power envelope), and the axes follow.</summary>
    public static readonly IReadOnlyList<ModeAxis> All =
        [ModeAxis.Fans, ModeAxis.GpuOc, ModeAxis.CpuPower, ModeAxis.Co, ModeAxis.Lights];

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
    /// (the dGPU power-cycles and comes back at zero offset) or a driver reload.
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
