using AcerHelper.Domain;

namespace AcerHelper.Application;

/// <summary>What applying the CURRENT performance mode's fan preset needs from whoever owns the fan graph and the
/// EC — the re-apply half of the fan axis, implemented by Infrastructure/Composition/LaptopService.Fans.cs. The
/// value crosses as the DOMAIN's <see cref="FanAxisState"/> (built where the stored preset is read), so Application
/// never names the container. Null means "this mode has no fan preset", which the UI reads as "leave the section
/// alone" — the same null it has always received.</summary>
public interface IFanModeTarget
{
    /// <summary>Apply the current mode's saved fan preset: Auto/Max are pushed immediately, Custom is left to the
    /// refresh loop's <see cref="ApplyCustomMode"/> so per-fan curves track temperature. Returns the applied state,
    /// or null when this mode has none.</summary>
    FanAxisState? ApplyCurrentMode();
}

/// <summary>The fan axis's mode-change apply as a use case: push the current mode's fan preset. The decision it
/// states is the one the axis has always had and that the EC forces: a mode with NO preset leaves the fans alone
/// (the fan mode cannot be read back, so an absent preset is not a known state), while a Custom preset is left to
/// the temperature-tracking loop rather than pushed once.</summary>
public sealed class ApplyModeFan(IFanModeTarget target)
{
    public FanAxisState? Run() => target.ApplyCurrentMode();
}

/// <summary>What applying the CURRENT mode's GPU clock offsets needs from whoever owns the driver, the EC
/// power-envelope port and the graph — implemented by Infrastructure/Composition/LaptopService.Tuning.cs. The
/// offsets cross as <see cref="GpuAxisState"/>. There is no nullable return: an unconfigured mode is definitely
/// STOCK (the driver zeroes offsets on boot), so switching to it must actively clear whatever the previous mode
/// applied.
///
/// IT APPLIES BOTH HALVES OF THE GPU AXIS. The offsets are one member of <see cref="GpuAxisState"/> and the
/// chosen power level (<see cref="GpuAxisState.Power"/>) is the other, and the two move together on a mode
/// switch, at boot and on resume — so one use case drives them and the outcome carries both. The power half's
/// rule is the ENVELOPE's, and it is asymmetrical to the offsets on purpose: an explicit level is written to the
/// EC (so a mode that pinned a row gets it back after the mode switch or a boot), while "no override" (a null
/// <see cref="GpuAxisState.Power"/>) WRITES NOTHING — the envelope is then the profile's own row, which the
/// profile switch already drove, and forcing a row on it would be the very coupling this axis exists to break.
/// The target owns that branch (see <c>IGpuOcModeTarget.ApplyCurrentMode</c>).</summary>
public interface IGpuOcModeTarget
{
    /// <summary>Apply the current mode's saved GPU offsets (stock 0/0 when this mode has none) and its power
    /// level when it pinned one, and return what was applied — both halves, so the UI reflects the same state
    /// the hardware was given.</summary>
    GpuAxisState ApplyCurrentMode();
}

/// <summary>The GPU axis's mode-change apply as a use case: write the current mode's offsets, treating an absent
/// preset as STOCK rather than as an absence, and write the mode's explicit power level when it has one. This is
/// the axis's rule and not the edit path's — a mode the user never configured must have the previous mode's
/// overclock cleared off it, while an envelope that follows the profile must be LEFT to the profile switch.</summary>
public sealed class ApplyModeGpuOc(IGpuOcModeTarget target)
{
    public GpuAxisState Run() => target.ApplyCurrentMode();
}

/// <summary>What applying the CURRENT mode's CPU power overlay needs from whoever owns the OS power API and the
/// graph — implemented by Infrastructure/Composition/LaptopService.Tuning.cs. The overlay id is the OS's own
/// opaque string, so nothing is converted.</summary>
public interface ICpuPowerModeTarget
{
    /// <summary>Apply the current mode's overlay IF one is stored for it, and return the id the UI should reflect
    /// (the stored one, or the live effective overlay on an unconfigured mode). A mode with no entry is LEFT
    /// UNTOUCHED — we do not force an OS power mode on a profile the user has not configured — which is why the
    /// return is nullable and the write conditional.</summary>
    string? ApplyCurrentMode();
}

/// <summary>The CPU-power axis's mode-change apply as a use case: write the current mode's overlay only when one is
/// configured, and report the value the UI shows either way. The asymmetry with the GPU/fan axes is stated here:
/// this axis reads the live overlay back on an unconfigured mode instead of forcing one.</summary>
public sealed class ApplyModeCpuPower(ICpuPowerModeTarget target)
{
    public string? Run() => target.ApplyCurrentMode();
}

/// <summary>What applying the CURRENT mode's Curve-Optimizer offsets needs from whoever owns the SMU and the graph
/// — implemented by Infrastructure/Composition/LaptopService.Tuning.cs. It reports NOTHING: the row the mode-switch
/// site shows is read separately from the STORED value, because this axis is the deferred one and its write can
/// block for seconds on the machine-wide PCI lock. The two facts — what was applied and what the row shows — are
/// deliberately separate questions (see Application/ReapplySettings.cs's Reflect).</summary>
public interface ICoModeTarget
{
    /// <summary>Apply the current mode's Curve-Optimizer offset: an unconfigured mode is stock and is actively
    /// cleared once ANY mode has a preset, while an install that never configured one is left untouched (no SMU
    /// mailbox message on a CPU that does not confirm it).</summary>
    void ApplyCurrentMode();
}

/// <summary>The Curve-Optimizer axis's mode-change apply as a use case. It holds one decision — the never-configured
/// guard that distinguishes "this mode is stock" from "the user never opted into undervolting" — and reports
/// nothing, because its only callers are fire-and-forget re-applies (the startup path, the refresh pass, a resume)
/// and neither reads the preset it applies.</summary>
public sealed class ApplyModeCo(ICoModeTarget target)
{
    public void Run() => target.ApplyCurrentMode();
}

/// <summary>What driving the fans from their curves needs from whoever owns the fan graph, the EC and the live
/// sensors — the refresh loop's fan tick, implemented by Infrastructure/Composition/LaptopService.Fans.cs. The
/// sensors cross as the Domain's <see cref="SensorSnapshot"/>.</summary>
public interface IFanDriveTarget
{
    /// <summary>If the current mode is Custom, drive each fan from its curve (or fixed speed) at the live
    /// temperatures, with a deadband so the fans do not hunt; a no-op outside Custom. The lock is held across the
    /// EC call HERE, which is the one deliberate hold docs/open-decisions.md §3 records.</summary>
    void Drive(SensorSnapshot sensors);
}

/// <summary>The refresh loop's fan tick as a use case: drive the fans from the current mode's curves. It is thin by
/// design and exists so the refresh pass names the action rather than the service. The deadband, the Custom guard
/// and the atomic lock-through-EC are the implementation's, which is where they have always been.</summary>
public sealed class ApplyCustom(IFanDriveTarget target)
{
    public void Run(SensorSnapshot sensors) => target.Drive(sensors);
}

/// <summary>What re-applying the state the OS does not remember on its own needs from whoever owns the graph and
/// the ports — implemented by Infrastructure/Composition/LaptopService.cs. The members are the two scalars read
/// under the lock, the two synchronous port writes made OUTSIDE it, and the reconciler hand-off; the ORDER is the
/// use case's.</summary>
public interface IStartupStateTarget
{
    /// <summary>The persisted clamshell takeover flag, read under the graph lock.</summary>
    bool Clamshell { get; }

    /// <summary>The persisted blue-light level, read under the graph lock. 0 means off and nothing is applied.</summary>
    int Bluelight { get; }

    /// <summary>Apply the blue-light level to the port — a write made OUTSIDE the graph lock. The clamshell port
    /// write is <see cref="IClamshellTarget.SetEnabled"/>, shared with the shell row's use case.</summary>
    void ApplyBluelight(int level);

    /// <summary>Re-apply the volatile axes for a boot (the schedule is Application's, the writes are the
    /// reconciler's). Called LAST, after the two scalar axes.</summary>
    void ReapplyStartup();
}

/// <summary>Re-applying persisted state the OS does not remember, as a use case: the clamshell takeover, then the
/// blue-light tint, then the volatile axes.
///
/// WHAT IT DECIDES:
/// <list type="number">
/// <item><b>The two scalars are read FIRST, under one hold, and the hardware calls are made OUTSIDE it.</b> The lock
/// guards the graph, not hardware: hoisting the CALLS out of the lock only shortens the hold, because the mode axes
/// re-read the current mode under their own acquisition (design doc D17). Hoisting an ARGUMENT is a different
/// change and unsafe, which is why the values are captured here and not computed lazily.</item>
/// <item><b>Each scalar port is written only when its flag says to</b> — clamshell when on, blue-light when
/// non-zero — exactly as before.</item>
/// <item><b>The volatile axes are reconciled last</b>, so the clamshell takeover is in place before the option rows
/// read it. This use case does not own that schedule (Application/ReapplySettings.cs), only the moment.</item>
/// </list>
///
/// A throw from a synchronous axis escapes to the caller (the AppController constructor), which is the old,
/// recorded gap rather than something this move closes.</summary>
public sealed class ApplyStartupState(IStartupStateTarget target, IClamshellTarget clamshell)
{
    public void Run()
    {
        // Read both scalars under one hold; the hardware calls are made outside it.
        var clamshellOn = target.Clamshell;
        var bluelight = target.Bluelight;

        if (clamshellOn) clamshell.SetEnabled(true);
        if (bluelight > 0) target.ApplyBluelight(bluelight);

        target.ReapplyStartup();
    }
}
