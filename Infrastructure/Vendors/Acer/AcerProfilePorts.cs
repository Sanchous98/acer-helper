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
/// Presents the five Acer profiles over a <c>platform_profile</c> source, which speaks a DIFFERENT vocabulary
/// for the same five modes: the kernel's choice names (low-power / quiet / balanced / balanced-performance /
/// performance — see <see cref="AcerProfiles"/>). The port exists so the app keeps ONE identity for a mode —
/// the Acer EC byte in <c>Id</c> — all the way through: the tray icon, the per-mode presets, the lightbar
/// palette, <c>settings.json</c> and the EC envelope all key off those bytes, and a source offering
/// <c>"low-power"</c> would otherwise put a second, incompatible id into the per-power-source memory the app
/// restores from (<c>ProfileMemory.BaseId</c>).
///
/// It is what makes the two vocabularies meet in one place, and that is where the live bug lived:
/// <c>SysfsPowerProfiles</c> classifies the token <c>"performance"</c> as <see cref="ProfileKind.Performance"/>,
/// but the kernel's <c>"performance"</c> is Acer TURBO — so the envelope went out as mode 1 (93 W) where the
/// hardware wanted mode 0 (108 W). Profiles handed out here are the table's own, so the kind is the byte's kind.
///
/// The inner port writes <c>profile.Id</c> verbatim (<c>SysfsPowerProfiles.Set</c> puts that id straight into the
/// node), which is why <see cref="Set"/> must pass the inner port's OWN object for the resolved choice rather than
/// a re-badged Acer one: wrapping an Acer profile in the choice name would mean building a second identity for a
/// mode that already has one, and the inner port stays untouched.
/// </summary>
internal sealed class AcerMappedProfiles(IPowerProfiles inner) : IPowerProfiles
{
    // The decorator's own refusals have nowhere else to be reported: LastError is a read-only property and the
    // inner port was never asked. Kept to one call — a stale refusal must not be read after a later success
    // (LaptopService reads port.LastError only straight after a false Set, but "only" is not a contract).
    private string? _refusal;

    public string? LastError => _refusal ?? inner.LastError;

    /// <summary>Only the profiles the source actually offers, in ACER display order — Eco, Quiet, Balanced,
    /// Performance, Turbo, which is also the order the performance hotkey cycles through. The inner source's own
    /// order is not used: it is the kernel's, and on this hardware the source may list the same five names in a
    /// different sequence (or offer a subset).</summary>
    public IReadOnlyList<PerformanceProfile> All => Offered();

    /// <summary>The same list as <see cref="All"/>, deliberately: this port does NOT gate on the power source.
    /// All five profiles were measured WRITABLE on battery through the Acer handler on 2026-09-20 (that is the
    /// Acer handler, not the AMD one — see the tombstone at the end of this file), and Windows has no such gate
    /// either: parity with Windows is the whole point of this port. <c>All</c> and this can therefore only
    /// differ by a source that stops offering a profile — the "available SET" is the EC's supported-mask on
    /// Windows and the class node's <c>choices</c> here, and both are hardware facts, not policy.</summary>
    public IReadOnlyList<PerformanceProfile> Selectable() => Offered();

    /// <summary>The Acer profile whose choice name the source reports as active, or null when it reports
    /// something this table cannot name (an unknown token, or nothing readable at all).
    ///
    /// Null is the only honest answer there and it has to stay null: a mode the app cannot name must not
    /// silently apply another mode's presets, because the presets move fan curves, GPU offsets and the EC
    /// envelope. The failure mode being avoided is a <c>?? Balanced</c> fallback — plausible-looking, and it
    /// would rewrite the EC usage mode of a machine that was sitting in a mode of its own.</summary>
    public PerformanceProfile? Current()
        => inner.Current() is { } cur ? AcerProfiles.FromChoiceName(cur.Id) : null;

    /// <summary>Resolve the profile to its choice name, then write that name through the inner port — by
    /// handing over the inner port's own profile object for it, so the id it writes is its own.
    ///
    /// A refusal is a refusal on purpose: when the source does not offer the target (or the profile is not one
    /// of ours), nothing is written and <see cref="LastError"/> says why. The tempting alternative — falling
    /// back to the nearest token, or to whatever is offered — is how a user clicks Performance and gets Turbo,
    /// given that the kernel spells Turbo "performance".</summary>
    public bool Set(PerformanceProfile profile)
    {
        _refusal = null;

        var choice = AcerProfiles.ToChoiceName(profile);
        var target = choice == null ? null : inner.All.FirstOrDefault(p => p.Id == choice);
        if (target == null)
        {
            _refusal = choice == null
                ? $"\"{profile.DisplayName}\" is not one of the machine's performance profiles"
                : $"this machine's profile source does not offer \"{choice}\"";
            return false;
        }

        return inner.Set(target);
    }

    /// <summary>The profiles <see cref="AcerProfiles.All"/> reduced to what the source offers, in table order.
    /// An empty source reduces to nothing rather than to everything — "the source was not found" is a reason to
    /// hide the section, not a licence to offer five modes that cannot be set.</summary>
    private IReadOnlyList<PerformanceProfile> Offered()
    {
        var offered = inner.All.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return AcerProfiles.All.Where(p => AcerProfiles.ToChoiceName(p) is { } c && offered.Contains(c)).ToList();
    }
}

// The battery-gated profiles decorator stood here and is deliberately gone rather than merely unwired. Its name
// is not spelled in this file on purpose: the removal is guarded by a source-text check over Infrastructure/, and
// the guard is a plain substring test, so the pin that keeps the removal a decision rather than an accident —
// and that names the deleted decorator in full — is TheMappedPortDoesNotGateOnPowerSource in
// AcerProfilePortsTests.
//
// Why it went: it greyed out everything but balanced/low-power while unplugged, and both halves of that were
// Linux-only policy. It had no Windows analogue at all (Windows has no power-source gate — see
// AcerProfiles.FromMask, where the available set comes from the EC supported-mask), and its stated justification
// — the driver refusing profile writes on battery — was measured against the AMD handler (platform-profile-0,
// EOPNOTSUPP on battery), which is a DIFFERENT handler on a different node. On 2026-09-20 all five were written
// on battery through the Acer handler on platform-profile-1 and read back. Keeping the gate would have been the
// one place where Linux offered less than Windows for no reason the hardware gives.
