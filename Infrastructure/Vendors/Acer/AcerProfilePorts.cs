using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Acer;

// Profile-port decorators used by the Acer backends. They live in this UN-SUFFIXED file deliberately, and moving
// them into AcerDevice.Linux.cs would be a silent loss of coverage rather than a tidy-up: the test project
// targets net10.0-windows while AcerHelper.csproj excludes **/*.Linux.cs from that TFM, so any policy left in the
// Linux file cannot be reached by the suite at all. The policy is therefore shared and testable here, and only
// the I/O stays per-OS, arriving as delegates — the same shape DelegatePorts.cs uses.

/// <summary>
/// Drives the EC "system usage mode" alongside a profile switch. Both channels are needed and they are
/// independent: the reported profile is what the tray, the per-mode presets and the lightbar palette follow, but
/// on EC-HID models it does not move the power envelope — the EC usage mode does (see docs/power-an18-61.md).
///
/// The envelope write is enqueue-only and best-effort, so its result is deliberately ignored: a controller that
/// cannot take the mode must not fail the profile switch. That is Windows' semantics exactly
/// (AcerDevice.Windows.SetProfile), and it is why the operation arrives as a <see cref="Func{T,TResult}"/> rather
/// than as the controller itself — <c>AcerEcHidController</c> is sealed, internal, has no port interface and no
/// overridable transport, so a decorator typed against the concrete class could not be driven by any test.
///
/// A null delegate means this model has no EC channel, and the inner port then behaves exactly as it would
/// unwrapped.
/// </summary>
internal sealed class EcSyncedProfiles(IPowerProfiles inner, Func<ProfileKind, bool>? applyEnvelope) : IPowerProfiles
{
    public string? LastError => inner.LastError;
    public IReadOnlyList<PerformanceProfile> All => inner.All;
    public PerformanceProfile? Current() => inner.Current();
    public IReadOnlyList<PerformanceProfile> Selectable() => inner.Selectable();

    public bool Set(PerformanceProfile profile)
    {
        applyEnvelope?.Invoke(profile.Kind);
        return inner.Set(profile);
    }
}

/// <summary>
/// Greys out the profiles the EC refuses while unplugged: on battery it rejects everything except balanced and
/// low-power (EOPNOTSUPP straight from the driver — verified on AN18-61), mirroring the Windows supported-mask
/// behaviour where Turbo drops out. Only <see cref="Selectable"/> is filtered — <see cref="All"/> and
/// <see cref="Current"/> stay whole, so the UI can still describe the mode the hardware is actually in.
///
/// The AC probe arrives as a delegate because it walks /sys/class/power_supply, which only a Linux box has; the
/// "no Mains node at all means AC" fallback stays with the caller that does the walking.
/// </summary>
internal sealed class BatteryGatedProfiles(IPowerProfiles inner, Func<bool> onAc) : IPowerProfiles
{
    private static readonly string[] BatterySafe = ["balanced", "low-power"];

    public string? LastError => inner.LastError;
    public IReadOnlyList<PerformanceProfile> All => inner.All;
    public PerformanceProfile? Current() => inner.Current();
    public bool Set(PerformanceProfile profile) => inner.Set(profile);

    public IReadOnlyList<PerformanceProfile> Selectable()
        => onAc() ? inner.Selectable() : inner.Selectable().Where(p => BatterySafe.Contains(p.Id)).ToList();
}
