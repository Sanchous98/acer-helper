using AcerHelper.Domain;

namespace AcerHelper.Application;

// THE READ SIDE OF THE SERVICE'S SURFACE, moved off it the same way the applied edits and the shell actions
// were. The service owns the machine (its ports) and the settings graph (the per-mode presets and the
// per-source memory); a QUERY is a use case that OWNS the read it makes, so the UI names the QUESTION
// (ReadCurrentProfile, ReadFanState, …) and never the layer that owns the graph. That is the whole point of
// the seam: the service's public surface stops carrying readers the UI happened to reach for, and a reader
// that states a rule — what an absent preset means, which profile the UI should show while Turbo sits over a
// base — states it here, where a stub can pin it without naming Infrastructure
// (ArchitectureMapTests.ApplicationPointsAtDomainNotInfrastructure).
//
// WHAT CROSSES. Nothing here names the stored container (<c>FanPreset</c>, <c>GpuOcPreset</c>, …): those are
// Infrastructure's (Infrastructure/Composition/Settings.cs), and Application may not name them. A per-mode
// read therefore crosses in the DOMAIN's vocabulary — <see cref="FanAxisState"/>, <see cref="GpuAxisState"/>
// (Domain/AxisState.cs) — built where the preset is read, exactly as the re-apply outcome and the applied
// edits already cross. The profiles, sensors and battery already had domain values
// (<see cref="PerformanceProfile"/>, <see cref="SensorSnapshot"/>, <see cref="BatteryInfoSnapshot"/>), so
// those cross unchanged.
//
// WHY SOME CONTRACTS HAVE TWO MEMBERS AND OTHERS ONE. A parameterless read that derives the current mode key
// reads the PowerProfiles PORT, and on this layer that read has historically happened WITHIN the graph lock
// for the per-mode presets (docs/open-decisions.md §3). The UI build path already reads the live profile ONCE
// and hands it to every per-mode reader (the D16 amortisation, PresetReadsWithProfileTests), so the contract
// states both forms: <c>Current()</c> reads the port for the key, <c>Current(cur)</c> reuses a profile the
// caller already holds. The two are genuinely different reads and keeping them apart is what lets the
// amortised path stay a REUSE rather than a substitution. <see cref="ICurrentProfileTarget"/> and
// <see cref="ISelectableProfilesTarget"/> have one member because there is no second form to distinguish —
// they are port reads with no key of their own.

/// <summary>What reading the live performance profile needs from whoever owns the profile port — implemented by
/// Infrastructure/Composition/LaptopService.Profiles.cs. ONE member: <c>device.PowerProfiles?.Current()</c> and
/// nothing else. There is no rule in this read — a machine with no profiles has none, and the null travels.</summary>
public interface ICurrentProfileTarget
{
    /// <summary>The profile the port reports as active right now, or null on a machine with no profile port.</summary>
    PerformanceProfile? Current();
}

/// <summary>The live profile as a use case: the ONE hardware profile read call sites amortise. The
/// UI build path and the refresh pass each call this once and hand the result to the per-mode readers, so
/// none of them pays a second EC round-trip (see <see cref="IFanStateTarget"/>'s two members).</summary>
public sealed class ReadCurrentProfile(ICurrentProfileTarget target)
{
    public PerformanceProfile? Run() => target.Current();
}

/// <summary>What reading the profiles the UI may offer RIGHT NOW needs from whoever owns the profile port and
/// the live power source — implemented by Infrastructure/Composition/LaptopService.Profiles.cs. ONE member: the
/// port's <c>Selectable()</c> narrowed by the vendor's optional per-source availability policy, with the live
/// source read under the graph lock. The narrowing is the read's whole decision and it is stated at the
/// implementation, where the policy and the source live.</summary>
public interface ISelectableProfilesTarget
{
    /// <summary>The profiles the UI may offer on the live source, in the port's display order; empty on a machine
    /// with no profile port.</summary>
    IReadOnlyList<PerformanceProfile> Selectable();
}

/// <summary>The selectable profiles as a use case: what the refresh pass and the profile section build from.
/// The availability policy (Acer's NitroSense parity) is the port's, narrowed against the live source here —
/// this use case only names the question so the UI does not depend on the service.</summary>
public sealed class ReadSelectableProfiles(ISelectableProfilesTarget target)
{
    public IReadOnlyList<PerformanceProfile> Run() => target.Selectable();
}

/// <summary>What reading the BASE profile — which one the UI shows as selected while Turbo sits over it — needs
/// from whoever owns the profile port, the per-source memory and the graph lock — implemented by
/// Infrastructure/Composition/LaptopService.Profiles.cs. ONE member, and it takes the profile the caller may
/// already hold: null means "there is no live profile to key off", NOT "read the port" (the port-reading form is
/// the use case's, which composes <see cref="ICurrentProfileTarget"/> with this).</summary>
public interface IBaseProfileTarget
{
    /// <summary>The base (non-Turbo) profile to highlight: <paramref name="cur"/> when it is not Turbo and the
    /// live source still offers it, otherwise the remembered base with the Balanced/first-non-Turbo fallbacks;
    /// null when this source has no usable non-Turbo profile.</summary>
    PerformanceProfile? Base(PerformanceProfile? cur);
}

/// <summary>The base profile as a use case. <see cref="Run()"/> reads the live profile first (outside the graph
/// lock, as the parameterless service accessor always did) and passes it in; <see cref="Run(PerformanceProfile?)"/>
/// reuses one the caller already holds. The rule — Turbo is not its own base, so the remembered base is shown,
/// and a base must never itself be Turbo — is the target's and is stated there.</summary>
public sealed class ReadBaseProfile(ICurrentProfileTarget current, IBaseProfileTarget target)
{
    public PerformanceProfile? Run() => target.Base(current.Current());

    public PerformanceProfile? Run(PerformanceProfile? cur) => target.Base(cur);
}

/// <summary>What reading the profile a POWER SOURCE remembers needs from whoever owns the per-source memory, the
/// profile port and the graph lock — implemented by Infrastructure/Composition/LaptopService.Profiles.cs. ONE
/// member: the source is a parameter, so there is no live-source read to amortise. The decision (Turbo reported
/// as the Turbo profile, a stale base reported as the source's fallback) is stated at the implementation.</summary>
public interface ISourceProfileReadTarget
{
    /// <summary>The profile <paramref name="onAc"/> is set to use: the remembered mode (Turbo folded to its
    /// profile when the switch is on), the source's fallback when the remembered one is no longer offered, or
    /// null when nothing is remembered for that source yet.</summary>
    PerformanceProfile? Source(bool onAc);
}

/// <summary>The per-source profile as a use case: the two rows in the Options drawer read through it, so the
/// row shows what the machine would actually use rather than the stored id.</summary>
public sealed class ReadSourceProfile(ISourceProfileReadTarget target)
{
    public PerformanceProfile? Run(bool onAc) => target.Source(onAc);
}

/// <summary>What reading the current mode's fan state for the UI needs from whoever owns the fan graph and the
/// profile port — implemented by Infrastructure/Composition/LaptopService.Fans.cs. It crosses as
/// <see cref="FanAxisState"/> (the domain's vocabulary), never as the stored <c>FanPreset</c>.
///
/// TWO MEMBERS for the same reason the other per-mode axes have two: <c>Current()</c> derives the mode key from
/// the port UNDER the graph lock (the historical lock scope for this read), while <c>Current(cur)</c> reuses a
/// profile the caller already read. An absent preset reads as the type's DEFAULTS, never as an absence — the
/// fan axis cannot be read back from the hardware, so "never configured" is the default ramp, and the UI has
/// always shown it that way.</summary>
public interface IFanStateTarget
{
    /// <summary>The current mode's fan state as domain values, defaults when this mode has no preset. Reads the
    /// profile port for the key, under the graph lock.</summary>
    FanAxisState Current();

    /// <summary>As <see cref="Current()"/> but reusing an already-read current profile, so a caller that holds it
    /// pays no EC round-trip for the key.</summary>
    FanAxisState Current(PerformanceProfile? cur);
}

/// <summary>The current mode's fan state as a use case. Thin by design — the read already answers in the domain's
/// vocabulary — and it exists so the UI build path names the read rather than the service.</summary>
public sealed class ReadFanState(IFanStateTarget target)
{
    public FanAxisState Run() => target.Current();

    public FanAxisState Run(PerformanceProfile? cur) => target.Current(cur);
}

/// <summary>What reading the current mode's GPU clock offsets and power level for the UI needs from whoever owns
/// the GPU graph and the profile port — implemented by Infrastructure/Composition/LaptopService.Tuning.cs. It
/// crosses as <see cref="GpuAxisState"/>, whose <c>Power</c> member is null for "follow the profile" and an
/// explicit <see cref="GpuPowerLevel"/> otherwise. TWO MEMBERS, the same port-key/reuse pair as
/// <see cref="IFanStateTarget"/>; an absent preset is STOCK (0/0) with no power override, because the driver
/// zeroes offsets and a mode the user never configured is definitely stock and definitely following its
/// profile. The LEVELS the control offers are not part of this read — they are the port's own
/// (<see cref="IGpuPowerEnvelope.Levels"/>), read where the section is built.</summary>
public interface IGpuOcStateTarget
{
    /// <summary>The current mode's GPU offsets and power choice as domain values, stock/follow-profile when
    /// this mode has no preset. Reads the profile port for the key, under the graph lock.</summary>
    GpuAxisState Current();

    /// <summary>As <see cref="Current()"/> but reusing an already-read current profile.</summary>
    GpuAxisState Current(PerformanceProfile? cur);
}

/// <summary>The current mode's GPU offsets as a use case.</summary>
public sealed class ReadGpuOcState(IGpuOcStateTarget target)
{
    public GpuAxisState Run() => target.Current();

    public GpuAxisState Run(PerformanceProfile? cur) => target.Current(cur);
}

/// <summary>What reading the current mode's Curve-Optimizer offsets for the UI needs from whoever owns the
/// Curve-Optimizer port, the graph and the profile port — implemented by
/// Infrastructure/Composition/LaptopService.Tuning.cs. The value is the port's own index-aligned row shape
/// (one entry per voltage domain, or one all-core value), so nothing is converted. TWO MEMBERS, the same
/// port-key/reuse pair as the other per-mode reads; a machine with no Curve-Optimizer port reports an empty
/// array, which is what the UI reads as "no section".</summary>
public interface ICoDomainsTarget
{
    /// <summary>The current mode's offsets, index-aligned with the port's domains (a single all-core entry on a
    /// CPU without domain control), empty when this machine has no Curve-Optimizer port. Reads the profile port
    /// for the key, under the graph lock.</summary>
    int[] Current();

    /// <summary>As <see cref="Current()"/> but reusing an already-read current profile.</summary>
    int[] Current(PerformanceProfile? cur);
}

/// <summary>The current mode's Curve-Optimizer rows as a use case.</summary>
public sealed class ReadCoDomains(ICoDomainsTarget target)
{
    public int[] Run() => target.Current();

    public int[] Run(PerformanceProfile? cur) => target.Current(cur);
}

/// <summary>What reading the CPU power-mode overlay for the UI needs from whoever owns the overlay port and the
/// graph — implemented by Infrastructure/Composition/LaptopService.Tuning.cs. ONE member: unlike the other
/// per-mode axes there is no profile-reusing form, because the one caller (the refresh pass's CPU-power prime)
/// holds no profile and the fallback — the LIVE overlay on an unconfigured mode — is a port read regardless.</summary>
public interface ICpuPowerStateTarget
{
    /// <summary>The current mode's overlay id: the stored choice when the user set one, otherwise the live
    /// effective overlay so the row reflects reality on an unconfigured mode; null with no port.</summary>
    string? Current();
}

/// <summary>The CPU power overlay as a use case.</summary>
public sealed class ReadCpuPower(ICpuPowerStateTarget target)
{
    public string? Run() => target.Current();
}

/// <summary>What reading the live sensors needs from whoever owns the sensor port — implemented by
/// Infrastructure/Composition/LaptopService.Toggles.cs. ONE member: a port read with no graph and no policy; a
/// machine with no sensor port reports the "unavailable" snapshot, exactly as the service accessor always did.
/// The snapshot is the domain's own (<see cref="SensorSnapshot"/>).</summary>
public interface ISensorsReadTarget
{
    /// <summary>The live temperature/RPM snapshot, or the all-unavailable snapshot on a machine with no sensor
    /// port.</summary>
    SensorSnapshot Read();
}

/// <summary>The live sensors as a use case: the refresh pass reads through it once per tick.</summary>
public sealed class ReadSensors(ISensorsReadTarget target)
{
    public SensorSnapshot Run() => target.Read();
}

/// <summary>What reading the battery needs from whoever owns the battery object — implemented by
/// Infrastructure/Composition/LaptopService.Toggles.cs. ONE member: a read through the battery's own telemetry
/// op, falling back to the "unknown" snapshot when this machine reports no battery
/// (<see cref="BatteryInfoSnapshot"/>, Domain/Models.cs).</summary>
public interface IBatteryReadTarget
{
    /// <summary>The live battery snapshot, or the all-unknown snapshot on a machine with no telemetry.</summary>
    BatteryInfoSnapshot Read();
}

/// <summary>The battery as a use case: the refresh pass and the fast battery poll read through it.</summary>
public sealed class ReadBatteryInfo(IBatteryReadTarget target)
{
    public BatteryInfoSnapshot Run() => target.Read();
}

/// <summary>The moved QUERY use cases the UI invokes, grouped into ONE record so the controller (and the options
/// assembler, which reads the per-source profile) takes the set as a whole rather than as ten more positional
/// constructor arguments.
///
/// WHY A BUNDLE RATHER THAN INDIVIDUAL PARAMETERS, exactly as for <see cref="AppActions"/>: this family is the
/// app-level READ surface and it is large; spreading it across the controller's constructor would make that
/// constructor a positional list long enough to transpose by accident. The type names nothing
/// infrastructure-shaped — every member is one of the use cases declared in this layer.</summary>
public sealed record AppQueries(
    ReadCurrentProfile CurrentProfile,
    ReadSelectableProfiles SelectableProfiles,
    ReadBaseProfile BaseProfile,
    ReadSourceProfile SourceProfile,
    ReadFanState FanState,
    ReadGpuOcState GpuOcState,
    ReadCoDomains CoDomains,
    ReadCpuPower CpuPower,
    ReadSensors Sensors,
    ReadBatteryInfo Battery);
