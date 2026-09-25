using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

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
///
/// THE PROFILE'S CLASS COMES OFF THE INNER PORT (<see cref="ProfileTraits.Of"/>), not off the profile: the
/// profile record stopped carrying it on 2026-09-22, and who the mode "is" is the offering backend's reading in
/// any case. This decorator therefore sits transparently in front of that answer — an outer wrapper that
/// classified what its inner port already knows would be a second table for the same machine, which is what the
/// Acer-vs-kernel vocabulary trap is made of.
/// </summary>
internal sealed class EcSyncedProfiles(IPowerProfiles inner, Func<ProfileKind, bool>? applyEnvelope)
    : IPowerProfiles, IProfileTraits, IProfileAvailability
{
    public string? LastError => inner.LastError;
    public IReadOnlyList<PerformanceProfile> All => inner.All;
    public PerformanceProfile? Current() => inner.Current();
    public IReadOnlyList<PerformanceProfile> Selectable() => inner.Selectable();

    /// <summary>Forwarded from the port underneath when it declares a policy, otherwise "everything offered".
    /// This decorator's job is the EC envelope, not availability, so it must not swallow a capability its inner
    /// port carries — the same pass-through rule as <see cref="Traits"/>.</summary>
    public IReadOnlyList<PerformanceProfile> AvailableOn(bool onAc)
        => inner is IProfileAvailability a ? a.AvailableOn(onAc) : inner.All;

    /// <summary>The inner port's own reading, forwarded unchanged — including its refusals: a profile the inner
    /// port does not classify reaches the envelope as <see cref="ProfileTraits.Unknown"/>, whose kind
    /// <c>AcerEcHidController.ModeFor</c> maps to nothing, so the EC is left alone rather than sent another
    /// mode's envelope.</summary>
    public ProfileTraits Traits(PerformanceProfile profile) => ProfileTraits.Of(inner, profile);

    public bool Set(PerformanceProfile profile)
    {
        applyEnvelope?.Invoke(Traits(profile).Kind);
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
internal sealed class AcerMappedProfiles(IPowerProfiles inner)
    : IPowerProfiles, IProfileTraits, IProfileAvailability
{
    // The decorator's own refusals have nowhere else to be reported: LastError is a read-only property and the
    // inner port was never asked. Kept to one call — a stale refusal must not be read after a later success
    // (LaptopService reads port.LastError only straight after a false Set, but "only" is not a contract).
    private string? _refusal;

    public string? LastError => _refusal ?? inner.LastError;

    /// <summary>The Acer table's reading, and NOT the inner source's — the second half of what this decorator is
    /// for. The source classifies the kernel token <c>"performance"</c> as <see cref="ProfileKind.Performance"/>,
    /// which on this node is Turbo's byte: handing that reading upwards would classify the app's Turbo as
    /// Performance and send the EC envelope mode 1 (93 W) where the hardware wants mode 0 (108 W) — the live bug
    /// this class exists to close. Profiles handed out here are the table's own, so the traits are the byte's
    /// traits.</summary>
    public ProfileTraits Traits(PerformanceProfile profile) => AcerProfiles.TraitsOf(profile);

    /// <summary>Only the profiles the source actually offers, in ACER display order — Eco, Quiet, Balanced,
    /// Performance, Turbo, which is also the order the performance hotkey cycles through. The inner source's own
    /// order is not used: it is the kernel's, and on this hardware the source may list the same five names in a
    /// different sequence (or offer a subset).</summary>
    public IReadOnlyList<PerformanceProfile> All => Offered();

    /// <summary>The same list as <see cref="All"/>, deliberately: this port does NOT gate its SELECTABLE set on
    /// the power source. All five profiles were measured WRITABLE on battery through the Acer handler on
    /// 2026-09-20 (that is the Acer handler, not the AMD one — see the tombstone at the end of this file), so
    /// "the hardware let us write it" stays true. Which of them the UI OFFERS per source is a separate,
    /// vendor-declared policy, and it is exposed as data rather than applied here: see
    /// <see cref="AvailableOn"/> and the tombstone below. <c>All</c> and this can therefore only differ by a
    /// source that stops offering a profile — the available SET is the EC's supported-mask on Windows and the
    /// class node's <c>choices</c> here, and both are hardware facts.</summary>
    public IReadOnlyList<PerformanceProfile> Selectable() => Offered();

    /// <summary>The Acer table's own NitroSense-parity policy, over exactly the profiles this source offers:
    /// on battery Eco and Balanced, on AC Quiet, Balanced, Performance and Turbo. Pure data — no node, no
    /// machine input (see <see cref="AcerProfiles.IsAvailable"/>) — so it is testable off the laptop. The
    /// decorator DECLARES it; deciding and applying it is <c>LaptopService</c>'s, which is why
    /// <see cref="Selectable"/> above is left ungated.</summary>
    public IReadOnlyList<PerformanceProfile> AvailableOn(bool onAc)
        => Offered().Where(p => AcerProfiles.IsAvailable(p, onAc)).ToList();

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
// and that names the deleted decorator in full — is TheMappedPortDeclaresAvailabilityWithoutGatingItsSelectable
// in AcerProfilePortsTests.
//
// Why it went, and what replaced it: it GREYED THE SET itself while unplugged, on a Linux-only I/O walk, and its
// stated justification — the driver refusing profile writes on battery — was measured against the AMD handler
// (platform-profile-0, EOPNOTSUPP on battery), which is a DIFFERENT handler on a different node; all five were
// written on battery through the Acer handler on platform-profile-1 on 2026-09-20. The owner has since asked for
// NitroSense parity (on battery only Eco and Balanced; on AC Quiet, Balanced, Performance and Turbo), so the
// policy is back — but as a PURE vendor declaration (AcerProfiles.IsAvailable, surfaced through
// IProfileAvailability) that both OSes carry, with the DECISION and the apply left to LaptopService. That split
// is what keeps the old failure modes closed: the policy file does no I/O (AcerLinuxWiringTests), and a port
// with no policy (ASUS, Dell, the generic ports) is never asked for one. See docs/acer-linux.md.
