using System.Threading;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    // ---- performance profiles ----

    /// <summary>The class of a profile, as the port that offers it declares it. THE ONLY WAY THIS LAYER learns
    /// which mode it is looking at, since the profile record stopped carrying it (2026-09-22): a profile is an
    /// opaque id plus a label, and "this one is Turbo / that one is Balanced" is the backend's own reading of
    /// its own table (<see cref="ProfileTraits.Of"/>). Null — no profile at all — reads as
    /// <see cref="ProfileKind.Other"/>, which is what "there is nothing to classify" has always been treated as
    /// everywhere below (none of the Turbo/Balanced branches is about a missing profile).</summary>
    private ProfileKind KindOf(PerformanceProfile? profile)
        => profile is { } p ? ProfileTraits.Of(device.PowerProfiles, p).Kind : ProfileKind.Other;

    /// <summary>The class and colours of <paramref name="profile"/>, for the layers above (the UI paints the
    /// profile segments and the tray icon with the accent, and hands the flash colour to the lighting
    /// coordinator). Exposed here rather than read off the port at each site because the UI already asks this
    /// service for everything else it knows about a profile, and because it makes the null-tolerant reading —
    /// "no profile, no colour" — the one the caller can state plainly.</summary>
    public ProfileTraits TraitsOf(PerformanceProfile profile) => ProfileTraits.Of(device.PowerProfiles, profile);

    // ---- the profile-switch use case (Application/ProfileSwitch.cs) ----
    //
    // EVERY profile write in this class goes through ONE SwitchProfile, and that is the point: the port write and
    // the lighting announcement are one action inside the use case, so no path — a pick, the tray, the hotkey, the
    // Turbo switch, or the per-source restore (ApplyStoredMode) — can
    // write the profile without also recording the light claim (the double palette flash this closes). The
    // per-source restore was the last hand-written port write, and it is routed through the use case too now; the
    // omission that used to be named here as a decision was the source-change half of the same double flash.
    //
    // The announcer is the one dependency that CANNOT be constructor-injected here: the real one is the UI's
    // LightingCoordinator, which is built ON TOP of this service, so wiring it at construction would be a cycle.
    // Composition builds the SwitchProfile (target = this service, announcer = the coordinator) and hands the SAME
    // instance back through <see cref="ProfileSwitch"/>, so the service's internal callers and the UI share one
    // use case. Where composition has not (a unit test, or a machine with no lighting), the getter builds one over
    // the no-op announcer, so a switch still writes and remembers and simply paints nothing.
    private SwitchProfile? _profileSwitch;
    private IProfileAnnouncer _announcer = NoLighting.Instance;

    /// <summary>The one switch use case over this service. Composition assigns the shared singleton (whose
    /// announcer is the UI's coordinator) so the service's own callers and <c>AppController</c> use the same
    /// instance; the getter lazily builds one over <see cref="ProfileAnnouncer"/> where nothing was assigned.</summary>
    internal SwitchProfile ProfileSwitch
    {
        get => _profileSwitch ??= new SwitchProfile(this, _announcer);
        set => _profileSwitch = value;
    }

    /// <summary>The fallback announcement sink, used only when <see cref="ProfileSwitch"/> is not assigned by
    /// composition. <c>internal</c> so a test can substitute a recorder; the running app lets composition set the
    /// whole switch instead.</summary>
    internal IProfileAnnouncer ProfileAnnouncer
    {
        get => _announcer;
        set { _announcer = value; _profileSwitch = null; }
    }

    /// <summary>A no-op announcer: the switch still writes the port and remembers the mode, and nothing paints.
    /// The honest default for a machine with no lighting and for a test that is about the switch itself.</summary>
    private sealed class NoLighting : IProfileAnnouncer
    {
        public static readonly NoLighting Instance = new();
        public void OnProfileApplied(PerformanceProfile applied) { }
    }

    /// <summary>The colour the firmware paints the operating-mode indicator for <paramref name="profile"/>, or
    /// null for no profile / a backend with no such palette. Null is the honest answer and it is also what the
    /// lighting coordinator's cache already means by it (no palette to re-send), so the caller needs no branch of
    /// its own — see <c>AppController</c>'s three hand-offs and <c>LightingCoordinator.OnProfileApplied</c>.</summary>
    public AccentColor? FlashColorOf(PerformanceProfile? profile)
        => profile is { } p ? TraitsOf(p).FlashColor : null;

    // Which power source we last saw, and its remembered mode slot. Access under _state (touched by the
    // background SyncPowerSource / SetPowerAdapter and UI-thread profile actions).
    //
    // TWO INPUTS, ONE EFFECTIVE ANSWER. _osOnAc is the OS's "is external power connected" (charging/idle =>
    // true), which is ALL Windows reports — and it reports USB-C Power Delivery as plain "AC". _adapter is the
    // TYPED source the Acer EC channel exposes (Barrel / USB-C / Battery), which distinguishes them. _onAc is
    // the EFFECTIVE answer the rest of this file reads, derived from both: USB-C PD is a limited source and is
    // treated exactly like the battery (same profile set), while the
    // barrel charger and machines with no EC channel keep the OS answer. See RecomputeOnAc.
    private bool? _osOnAc;
    private PowerSource? _adapter;
    private bool? _onAc;
    private ProfileMemory Slot => _onAc == false ? Settings.OnBattery : Settings.OnAc;

    /// <summary>The live power source as last seen, EFFECTIVE: true on the barrel/DC-in charger, false on the
    /// battery OR on USB-C Power Delivery, null before any reading (no battery, or a desktop). Derived by
    /// <see cref="RecomputeOnAc"/> from the OS state and the typed EC adapter. Null is deliberately distinct
    /// from false: it means "we do not know", and a caller that must fail closed treats null exactly as "not AC".
    /// This is a read only; nothing here changes the source or applies a mode.</summary>
    public bool? OnAc
    {
        get { lock (_state) return _onAc; }
    }

    /// <summary>Record the TYPED power source the device's EC channel reported (Barrel / USB-C / Battery), from
    /// the slow <c>AcerPowerSourceSchedule</c> read. This is what separates USB-C Power Delivery from the barrel
    /// charger, which the OS alone cannot: on a machine whose EC channel said <see cref="PowerSource.UsbC"/> the
    /// effective source is forced to "battery-like" (see <see cref="RecomputeOnAc"/>), so the profile set is the
    /// battery one — the owner's rule. <see cref="PowerSource.Unknown"/> is
    /// IGNORED (the last named reading is kept): a transient failed read must not flap the profiles, and the
    /// battery card hides the row for it. On a machine with no EC channel this is never called (the adapter stays
    /// null) and the OS answer stands. Runs OFF the UI thread, like <see cref="SyncPowerSource"/>.</summary>
    public void SetPowerAdapter(PowerSource source)
    {
        if (source is not (PowerSource.Barrel or PowerSource.UsbC or PowerSource.Battery)) return;
        lock (_state)
        {
            if (_adapter == source) return;
            _adapter = source;
            RecomputeOnAc();
        }
    }

    /// <summary>Fold the two inputs into the effective <see cref="_onAc"/>, and — when the effective source
    /// actually changes — seed or restore that source's remembered mode, exactly as the old single-input
    /// <see cref="SyncPowerSource"/> did. Held under <c>_state</c>; <c>SeedSlotFromHardware</c> and
    /// <c>ApplyStoredMode</c> re-enter the lock, which C# locks allow.
    ///
    /// THE RULE: USB-C Power Delivery and the battery demote to "not AC"; everything else (the barrel, and any
    /// machine without a typed read) keeps the OS answer. "AC" here therefore means the ORIGINAL barrel/DC-in
    /// charger, not external power in general — USB-C PD IS external power, and folding it into the battery is
    /// the owner's rule ("при питании от usb-c должны оставаться те же профили питания, что и от батареи").
    /// The demotion is one-way on purpose — the typed read never PROMOTES to AC, so a contradictory reading
    /// cannot show the AC profile set on a discharging machine.</summary>
    private void RecomputeOnAc()
    {
        bool? effective = _adapter is PowerSource.UsbC or PowerSource.Battery ? false : _osOnAc;
        if (effective == _onAc) return;                                  // no effective change
        _onAc = effective;
        if (effective == null) return;                                  // unknown: nothing to apply
        if (string.IsNullOrEmpty(Slot.BaseId)) SeedSlotFromHardware();  // first time on this source
        else
        {
            // A SYSTEM path: a present profile port's refusal is caught here and swallowed rather than escaping
            // the refresh loop's pool thread (an unobserved task exception is nothing at all). The source change
            // is the doubt moment that must move the machine off a disabled mode; the next trigger re-asserts.
            try { ApplyStoredMode(); }
            catch (PortWriteFailedException) { /* the next pass re-syncs */ }
        }
    }

    /// <summary>Key identifying the current performance "mode" for per-mode presets: the profile id, except
    /// Turbo used as a switch shares its base profile's key (Turbo isn't a standalone mode then). "default"
    /// when the device has no profiles.</summary>
    public string CurrentModeKey()
    {
        var cur = device.PowerProfiles?.Current();
        return CurrentModeKey(cur);
    }

    /// <summary>As <see cref="CurrentModeKey()"/> but reusing an already-read current profile — so a refresh
    /// pass can read the hardware profile ONCE and derive the key without a second EC round-trip.</summary>
    public string CurrentModeKey(PerformanceProfile? cur)
    {
        // This keeps the method's exact former shape, including the early return that does NOT take the lock —
        // the null case reads nothing, and the original deliberately answered it without acquiring _state.
        //
        // The SLOT is read here, under the lock, and passed by value. That is not a style choice: it is the
        // remembered mode of the LIVE power source, so reading it inside the derivation would either put the
        // lock there or drop it, and hoisting a guarded read out of its lock is the hazard recorded in
        // docs/open-decisions.md §3. The lock is held across the whole derivation, which is free: `cur` is
        // already materialised and ModeKeyFor is pure, so the hold is two field reads.
        if (cur == null) return ModeKey.None.Value;
        lock (_state)
            return ModeKeyFor(cur, KindOf(cur), Settings.TurboToggles, Slot).Value;
    }

    /// <summary>The key the per-mode presets are filed under for <paramref name="cur"/>: the profile's own id,
    /// EXCEPT Turbo used as a switch, which shares its base profile's key — so what the user configured for that
    /// base is what applies while Turbo sits over it, and the presets do not fragment when the switch is toggled.
    ///
    /// That exception lives HERE and not in <see cref="ModeKey"/>, because it is a rule about the switch and not
    /// about a profile switch: whether Turbo is layered on a base at all is <c>Settings.TurboToggles</c>, and
    /// which base it is layered over is the remembered <see cref="ProfileMemory"/> — both are this layer's, and
    /// the firmware's Turbo really is an ordinary profile the domain has no reason to single out. The keys are
    /// unchanged by where the rule sits: this is <see cref="ModeKey.For"/> with the sharing branch wrapped
    /// around it, and the fallback to the own id — including the verbatim stale base a device no longer offers —
    /// is unchanged.
    ///
    /// <paramref name="slot"/> and <paramref name="turboToggles"/> are PARAMETERS because the slot is read by
    /// the caller under <c>_state</c> and handed over by value; a static rule that read service state itself
    /// would either need that lock or drop it (see the sibling comment above). Internal rather than private so
    /// the rule can be asserted directly against literal keys (<c>ModeKeyTests</c>) instead of only through the
    /// service that calls it.
    ///
    /// <paramref name="curKind"/> is a parameter for the same reason and it is the shape the profile's class
    /// forced (2026-09-22): the class no longer travels on the profile, so the one input this rule needs from it
    /// — "is the current mode Turbo" — is handed over beside the profile rather than read off it. The caller
    /// gets it from the port (<see cref="KindOf"/>); a rule that went looking for a port itself would be reading
    /// service state inside what is deliberately a pure function of its four inputs.</summary>
    internal static ModeKey ModeKeyFor(PerformanceProfile? cur, ProfileKind curKind, bool turboToggles, ProfileMemory slot)
        => turboToggles && curKind == ProfileKind.Turbo && slot.BaseId.Length > 0
            ? ModeKey.From(slot.BaseId)
            : ModeKey.For(cur);

    // ---- the profile READ contracts (Application/Queries.cs) ----
    //
    // The UI's reads, implemented EXPLICITLY so nothing is added to this class's public surface: a caller
    // reaches a read by naming the use case (ReadCurrentProfile, ReadSelectableProfiles, ReadBaseProfile,
    // ReadSourceProfile), not the service. The bodies are the internal helpers below, kept under their old
    // names because this class's own paths (the Turbo switch, the hotkey's cycle, the sweep) still call them.

    /// <summary>The live profile, for the ReadCurrentProfile use case.</summary>
    PerformanceProfile? ICurrentProfileTarget.Current() => CurrentProfile();

    /// <summary>What the port reports as active right now, or null with no port. `internal` rather than public:
    /// the UI reaches it through the use case, and this class's own paths (the sweep's base read) call it by
    /// name.</summary>
    internal PerformanceProfile? CurrentProfile() => device.PowerProfiles?.Current();

    /// <summary>The profiles the UI may offer RIGHT NOW, for the ReadSelectableProfiles use case.</summary>
    IReadOnlyList<PerformanceProfile> ISelectableProfilesTarget.Selectable() => SelectableProfiles();

    /// <summary>The profiles the UI may offer RIGHT NOW: what the port lists as selectable, narrowed by the
    /// vendor's per-source availability policy when it declares one
    /// (<see cref="IProfileAvailability"/> — Acer's NitroSense parity: on battery Eco and Balanced, on AC
    /// Quiet, Balanced, Performance and Turbo). A port with no policy — ASUS, Dell, the generic ports — is
    /// returned verbatim, so nothing outside the declaring vendor is affected. The live source comes from
    /// <see cref="_onAc"/>, set by <see cref="SyncPowerSource"/> before this is read in the refresh pass;
    /// "unknown" (before the first battery reading) reads as AC, the safe default. `internal` rather than public:
    /// the hotkey's own cycle calls it by name and the UI reaches it through the use case.</summary>
    internal IReadOnlyList<PerformanceProfile> SelectableProfiles()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return [];
        var offered = pp.Selectable();
        if (pp is not IProfileAvailability availability) return offered;   // no vendor policy to apply
        bool onAc;
        lock (_state) onAc = _onAc ?? true;
        var available = availability.AvailableOn(onAc);
        return offered.Where(p => available.Any(a => a.Id == p.Id)).ToList();
    }

    /// <summary>Whether <paramref name="profile"/> may be selected on the given source. A port that declares no
    /// policy allows everything it lists, which is the whole point of <see cref="IProfileAvailability"/> being
    /// optional. Pure: the answer is the port's, not the hardware's.</summary>
    private bool AvailableFor(PerformanceProfile profile, bool onAc)
        => device.PowerProfiles is not IProfileAvailability a
           || a.AvailableOn(onAc).Any(p => p.Id == profile.Id);

    /// <summary>Where to land when the source's remembered (or current) profile is not offered there: Balanced
    /// if this backend offers it, else the first non-Turbo, else the first offered. Null only when nothing is
    /// offered at all. NitroSense does exactly this — it does not leave the machine in a mode the source
    /// disabled — and the fallback is remembered for the live source so the slot cannot keep pointing at a
    /// disabled profile.</summary>
    private PerformanceProfile? FallbackProfile(bool onAc)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        return pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Balanced && AvailableFor(p, onAc))
            ?? pp.All.FirstOrDefault(p => KindOf(p) != ProfileKind.Turbo && AvailableFor(p, onAc))
            ?? pp.All.FirstOrDefault(p => AvailableFor(p, onAc));
    }

    // ---- the profile-switch target (Application/ProfileSwitch.cs) ----
    //
    // THREE explicit members, the two halves of the write plus the memory, exactly as Application's contract
    // declares them. They are implemented EXPLICITLY so nothing is added to this class's public surface: a caller
    // reaches them by naming the use case (SwitchProfile), not the service. `Apply` is a port call and nothing
    // else; `Remember` is the graph write; `CanApply` is the source gate. The use case orders them.
    //
    // THE LOCK AND THE PORT CALL ARE SPLIT, deliberately: the old ApplyProfile held _state across pp.Set, and the
    // contract exists precisely so it need not — CanApply reads the source under the lock, Apply makes the port
    // call under it (the port is serialised process-wide by the WMI/EC layer anyway), and Remember is the graph
    // write under it. What no caller can do any more is perform the port write WITHOUT the announcement, which is
    // the double-flash the use case removes.

    bool IProfileTarget.CanApply(PerformanceProfile profile)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return false;
        lock (_state) return AvailableFor(profile, _onAc ?? true);
    }

    /// <summary>Write the profile to the port. Reached only after <see cref="IProfileTarget.CanApply"/> answered
    /// true (which is false when this machine has no port), so the port is present and a refusal — or a throwing
    /// port — is a real fault: it THROWS <see cref="PortWriteFailedException"/>, carrying the port's own words
    /// when it returned them and none when it threw. The store-before-write order of the other axes does not apply
    /// here: this axis remembers only what LANDED, and the exception escapes before <see cref="Remember"/>.</summary>
    PerformanceProfile? IProfileTarget.Apply(PerformanceProfile profile)
    {
        if (device.PowerProfiles is not { } pp) return null;   // absent port: a capability fact, not a refusal
        lock (_state)
        {
            WriteOrThrow("profile switch", () => pp.Set(profile), () => pp.LastError);
            return profile;
        }
    }

    void IProfileTarget.Remember(PerformanceProfile profile)
    {
        lock (_state)
        {
            // Remember this as the base for the current source; a direct profile pick clears the Turbo flag.
            Slot.BaseId = profile.Id;
            Slot.Turbo = false;
            Save();
        }
    }

    /// <summary>Apply a profile as a persistent switch (a direct pick): write the port, remember what landed as
    /// the live source's base (clearing the Turbo flag) and announce it so the lighting repaints. The rules — the
    /// source gate first, only what landed being remembered, one announcement per landed switch — are the use
    /// case's (<see cref="SwitchProfile"/>); this method is the door the UI and the per-source paths call.
    ///
    /// AN ABSENT PORT IS A VALUE: false, the same no-op a portless machine has always returned (the use case's
    /// gate answers it without ever reaching the port). A PRESENT port's refusal is a
    /// <see cref="PortWriteFailedException"/> and escapes to the caller — a UI action catches it at its boundary
    /// and shows the message.</summary>
    public bool ApplyProfile(PerformanceProfile p) => ProfileSwitch.Run(p) is not null;

    /// <summary>True if the hardware is currently in the Turbo profile. NOT a UI query and not on this class's
    /// public surface: its only reader is the hotkey's cycle, which reaches it through
    /// <see cref="ITogglePerformanceTarget.IsTurboOn"/>.</summary>
    internal bool IsTurboOn() => KindOf(device.PowerProfiles?.Current()) == ProfileKind.Turbo;

    // ---- the base-profile READ contract (Application/Queries.cs) ----

    /// <summary>The base (non-Turbo) profile to show as selected, reusing a profile the caller already holds —
    /// for the ReadBaseProfile use case. `internal` rather than public: the use case names the read, and this
    /// class's own path (<see cref="ISetTurboTarget.BaseProfileOrNull"/>) calls it by name.</summary>
    PerformanceProfile? IBaseProfileTarget.Base(PerformanceProfile? cur) => BaseProfile(cur);

    /// <summary>The base (non-Turbo) profile to show as selected: the current profile when it isn't Turbo,
    /// otherwise the remembered base (falling back to Balanced / the first non-Turbo profile).</summary>
    internal PerformanceProfile? BaseProfile() => BaseProfile(device.PowerProfiles?.Current());

    /// <summary>As <see cref="BaseProfile()"/> but reusing an already-read current profile.</summary>
    internal PerformanceProfile? BaseProfile(PerformanceProfile? cur)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        lock (_state)
        {
            // Availability narrows the answer too, so a segment the source has disabled is never the one the
            // section highlights: the current profile on a source that no longer offers it falls through to the
            // remembered base, then Balanced, then the first available non-Turbo.
            bool onAc = _onAc ?? true;
            bool Ok(PerformanceProfile p) => AvailableFor(p, onAc);
            if (cur != null && KindOf(cur) != ProfileKind.Turbo && Ok(cur)) return cur;
            // NO last-resort fallback here: with no usable non-Turbo profile the answer is null, exactly as
            // it was before availability existed. <see cref="FallbackProfile"/> is for the APPLY path (which
            // may settle on Turbo if that is genuinely all a source offers); a base for Turbo to sit over must
            // never be Turbo itself.
            return pp.All.FirstOrDefault(p => p.Id == Slot.BaseId && Ok(p))
                ?? pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Balanced && Ok(p))
                ?? pp.All.FirstOrDefault(p => KindOf(p) != ProfileKind.Turbo && Ok(p));
        }
    }

    // ---- the Turbo-switch contract (Application/ProfilePower.cs) ----

    /// <summary>Whether this machine has a power-profiles port at all. A machine with none is the no-op the Turbo
    /// key has always been on it.</summary>
    bool ISetTurboTarget.HasProfiles => device.PowerProfiles != null;

    /// <summary>Find the Turbo profile the LIVE source offers and, under the SAME hold, capture the base it is
    /// about to sit over. Null when there is no Turbo profile or the source disables it — the backstop for the
    /// hotkey, which has no other way to learn the switch did nothing. The capture is BEFORE the port write
    /// because after it the live profile IS Turbo; this member exists so the two cannot be ordered wrongly.</summary>
    PerformanceProfile? ISetTurboTarget.BeginTurbo()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        lock (_state)
        {
            var found = pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Turbo);
            // Turbo is an AC-only mode under the declared policy (NitroSense parity): on battery the switch is
            // disabled, and this is the backstop for the hotkey, which reports "nothing landed".
            if (found == null || !AvailableFor(found, _onAc ?? true)) return null;
            // Capture the base we sit over BEFORE the write: after it, the live profile IS Turbo and there would
            // be nothing to remember as the base.
            var cur = pp.Current();
            if (cur != null && KindOf(cur) != ProfileKind.Turbo) Slot.BaseId = cur.Id;
            return found;
        }
    }

    /// <summary>The base (non-Turbo) profile to return to when switching off, read under the lock, or null when
    /// this source has none.</summary>
    PerformanceProfile? ISetTurboTarget.BaseProfileOrNull()
    {
        lock (_state) return BaseProfile();
    }

    /// <summary>Record (or clear) the Turbo flag over the preserved base — the Turbo switch's OWN memory, a graph
    /// write under the lock, persisted. Called by the use case only for a write that landed.</summary>
    void ISetTurboTarget.RememberTurbo(bool on)
    {
        lock (_state) { Slot.Turbo = on; Save(); }
    }

    // ---- the source-profile READ contract (Application/Queries.cs) ----

    /// <summary>The profile a power source is set to use, for the ReadSourceProfile use case. `internal` rather
    /// than public: the UI reaches it through the use case, and this class's own path
    /// (<see cref="RestoreSweepProfile"/>) calls it by name.</summary>
    PerformanceProfile? ISourceProfileReadTarget.Source(bool onAc) => SourceProfile(onAc);

    /// <summary>The profile a power source is set to use — what <see cref="SyncPowerSource"/> applies when the
    /// machine switches to it. Null when nothing is remembered for that source yet (fresh install, before the
    /// first time it was seen). Turbo used as a switch reports as the Turbo profile, because that is what the
    /// slot means to the user even though it is stored as base + flag.
    ///
    /// A slot pointing at a profile the source does not offer reports the FALLBACK rather than the stale id
    /// (NitroSense parity): the per-source row in Options then shows what the machine would actually use, not a
    /// mode the source disables. A port with no availability policy keeps returning the stored id verbatim.
    /// `internal` rather than public: the UI reaches it through the use case.</summary>
    internal PerformanceProfile? SourceProfile(bool onAc)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        lock (_state)
        {
            var slot = onAc ? Settings.OnAc : Settings.OnBattery;
            if (Settings.TurboToggles && slot.Turbo &&
                pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Turbo) is { } turbo
                && AvailableFor(turbo, onAc)) return turbo;
            var baseP = pp.All.FirstOrDefault(p => p.Id == slot.BaseId);
            if (baseP == null) return null;                      // nothing remembered for this source
            return AvailableFor(baseP, onAc) ? baseP : FallbackProfile(onAc);
        }
    }

    // ---- the source-profile contract (Application/ProfilePower.cs) ----
    //
    // The decision — Turbo as a flag over a preserved/seeded base, store-then-apply for the live source — is the
    // use case's (Application.SetSourceProfile). What stays is the graph (the two slots), the port and the
    // live-source comparison, implemented EXPLICITLY so nothing is added to this class's public surface.

    bool ISourceProfileTarget.HasProfiles => device.PowerProfiles != null;

    bool ISourceProfileTarget.TurboToggles
    {
        get { lock (_state) return Settings.TurboToggles; }
    }

    /// <summary>The port's own reading of whether <paramref name="profile"/> is its Turbo mode.</summary>
    bool ISourceProfileTarget.IsTurbo(PerformanceProfile profile) => KindOf(profile) == ProfileKind.Turbo;

    /// <summary>The base to seed under Turbo when the source has never held one — Balanced if this backend offers
    /// it, else the first non-Turbo profile. Null when this machine has neither.</summary>
    PerformanceProfile? ISourceProfileTarget.TurboSeed()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        return pp.All.FirstOrDefault(x => KindOf(x) == ProfileKind.Balanced)
            ?? pp.All.FirstOrDefault(x => KindOf(x) != ProfileKind.Turbo);
    }

    /// <summary>Store <paramref name="profile"/> as the source's base, clearing the Turbo flag.</summary>
    void ISourceProfileTarget.RememberBase(bool onAc, PerformanceProfile profile)
    {
        lock (_state)
        {
            var slot = onAc ? Settings.OnAc : Settings.OnBattery;
            slot.BaseId = profile.Id;
            slot.Turbo = false;
            Save();
        }
    }

    /// <summary>Record Turbo over the source's preserved base, seeding it from <paramref name="seed"/> only when
    /// the source has never held one (otherwise dropping Turbo later would leave nothing to return to).</summary>
    void ISourceProfileTarget.RememberTurbo(bool onAc, PerformanceProfile? seed)
    {
        lock (_state)
        {
            var slot = onAc ? Settings.OnAc : Settings.OnBattery;
            slot.Turbo = true;
            if (slot.BaseId.Length == 0) slot.BaseId = seed?.Id ?? "";
            Save();
        }
    }

    /// <summary>Whether <paramref name="onAc"/> names the LIVE source. Reference equality against the live slot is
    /// exact, and it also covers the "source still unknown" case (Slot then reads as the AC slot), so setting the
    /// AC profile before any battery reading applies immediately rather than silently waiting for a source
    /// change. Read under the lock, because the slot the comparison selects is guarded by it (the original held
    /// <c>_state</c> across the whole method).</summary>
    bool ISourceProfileTarget.SourceIsLive(bool onAc)
    {
        lock (_state) return ReferenceEquals(Slot, onAc ? Settings.OnAc : Settings.OnBattery);
    }

    /// <summary>Apply the live source's remembered mode to the port — the private <see cref="ApplyStoredMode"/>,
    /// reached by the use case.</summary>
    void ISourceProfileTarget.ApplyStoredMode() => ApplyStoredMode();

    // ---- the power-source sync contract (Application/ProfilePower.cs) ----

    /// <summary>Record the OS's own "external power" reading and recompute the effective source, which seeds or
    /// restores that source's remembered mode when the effective source actually changes. The USB-C demotion and
    /// the seeding/restore are <see cref="RecomputeOnAc"/>'s, unchanged. The use case has already dropped the
    /// unknown state; only a known reading reaches here.</summary>
    void IPowerSourceSyncTarget.ObserveOsPower(bool onAc)
    {
        lock (_state)
        {
            _osOnAc = onAc;
            RecomputeOnAc();
        }
    }

    /// <summary>Populate the current source's slot from the hardware's current profile, so a fresh install
    /// remembers what the machine was already on rather than forcing a default. Usually no change is applied;
    /// the exception is a machine sitting in a profile THIS SOURCE does not offer (e.g. Turbo on battery, or Eco
    /// after plugging in) — there the source's fallback is remembered AND applied, which is the NitroSense
    /// behaviour: the source change is the doubt moment that must move the machine off a disabled mode.</summary>
    private void SeedSlotFromHardware()
    {
        var pp = device.PowerProfiles;
        var cur = pp?.Current();
        if (cur == null) return;
        bool onAc = _onAc ?? true;

        if (!AvailableFor(cur, onAc))
        {
            var fb = FallbackProfile(onAc);
            if (fb == null) return;
            Slot.BaseId = fb.Id;
            Slot.Turbo = false;
            Save();
            // A SYSTEM path: the fallback write goes through the switch use case, whose present-port refusal is
            // caught here rather than escaping the seeding/refresh path. The slot now points at the fallback, and
            // the next source change re-asserts it.
            try { ApplyProfile(fb); } catch (PortWriteFailedException) { /* the next pass re-syncs */ }
            return;
        }

        if (KindOf(cur) == ProfileKind.Turbo)
        {
            Slot.Turbo = true;                                       // base under Turbo is unknown -> best guess
            Slot.BaseId = (pp!.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Balanced)
                        ?? pp.All.FirstOrDefault(p => KindOf(p) != ProfileKind.Turbo))?.Id ?? "";
        }
        else { Slot.BaseId = cur.Id; Slot.Turbo = false; }
        Save();
    }

    /// <summary>Apply the live source's remembered mode. "Nothing to do" — no port, nothing remembered, already in
    /// that mode — returns normally; a PRESENT profile port's refusal THROWS
    /// <see cref="PortWriteFailedException"/>, which each caller catches on its own terms (the Options row shows
    /// the message, the refresh loop swallows it).</summary>
    private void ApplyStoredMode()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return;
        var slot = Slot;
        if (string.IsNullOrEmpty(slot.BaseId)) return;

        bool onAc = _onAc ?? true;
        var baseP = pp.All.FirstOrDefault(p => p.Id == slot.BaseId && AvailableFor(p, onAc));
        if (baseP == null)
        {
            // The remembered mode is not offered on this source — e.g. Turbo remembered on AC, then unplugged
            // (NitroSense parity). Fall back to a valid one and REMEMBER it for the live source, so the slot
            // never keeps pointing at a disabled profile. An unknown BaseId (no match at all) still no-ops, as
            // before: only a profile we recognise but the source disallows is rewritten.
            if (pp.All.Any(p => p.Id == slot.BaseId))
            {
                baseP = FallbackProfile(onAc);
                if (baseP == null) return;
                slot.BaseId = baseP.Id;
                slot.Turbo = false;
                Save();
            }
            else return;
        }

        var current = pp.Current();
        var turbo = pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Turbo);
        var wantTurbo = slot.Turbo && Settings.TurboToggles
                        && turbo != null && pp.Selectable().Any(p => p.Id == turbo.Id)
                        && AvailableFor(turbo, onAc);

        // Only touch the profile when the hardware isn't already in the target mode. Each Acer profile Set makes
        // the firmware re-flash the keyboard/lightbar palette, so blindly re-applying an already-active mode on
        // every AC<->battery change — the common case, same mode on both sources — just blinks the keyboard for
        // nothing. THIS GUARD IS WHAT KEEPS A SAME-MODE SOURCE CHANGE FLASH-FREE; everything below it is a real
        // move and must paint exactly once.
        //
        // IT IS A PORT-READ GUARD, and that is deliberately not the whole story: it sees only what the port
        // currently REPORTS, which between two LOCAL writes is stale (the ~1 s/3 s poll has not caught up). The
        // complementary guard for that gap is the write-side coalescing in Application/ProfileSwitch.cs
        // (CoalesceWindowSeconds), which remembers the profile we just WROTE and dedups a repeat of it — the
        // owner's «подключил зарядку и быстро поменял на turbo» flash. Keep both: this one is free and exact for
        // the already-settled case; the use case's covers the not-yet-polled one.
        //
        // A SOURCE CHANGE WRITES THROUGH THE SWITCH USE CASE, as a TRANSIENT. Two things follow, and both are the
        // fix for the resume double flash:
        //   * The port write and the light claim are one action, so the landing is ANNOUNCED (OnProfileApplied
        //     records _pendingId and paints) and the ~1 s stale refresh pass is suppressed — exactly as for a
        //     pick or the sweep's force/restore. The hand-written pp.Set here used to skip that claim, so the
        //     firmware's own flash on the write was followed by the poll's repaint: two palette cycles.
        //   * TRANSIENT because the slot ALREADY holds the mode — this restores what was remembered, it does not
        //     re-remember it (that would re-seed/re-flag on every source change). SetSourceProfile is the door that
        //     remembers a choice; this door only applies one.
        // The base under Turbo is bookkeeping in the slot (BaseProfile reads it), not the active profile, so we DO
        // NOT re-drive it: the old `pp.Set(baseP)` before the Turbo write was the intermediate "old profile" flash
        // — the machine briefly dropped out of Turbo and back. The Acer EC takes the Turbo profile byte directly
        // and needs no base established over it (AcerDevice.Windows.SetProfile writes one gmInput; the base is a
        // slot concept, not a platform precondition), so that write is gone rather than coalesced.
        if (wantTurbo)
        {
            if (KindOf(current) == ProfileKind.Turbo) return;   // already in Turbo -> nothing to do
            ProfileSwitch.Run(turbo!, transient: true);         // a present port's refusal throws to the caller
            return;
        }
        if (current?.Id == baseP.Id) return;          // already in the remembered base profile
        ProfileSwitch.Run(baseP, transient: true);    // a present port's refusal throws to the caller
    }

    // ---- the performance-hotkey contract (Application/ProfilePower.cs) ----
    //
    // The branch choice (Turbo flip vs cycle), the fresh read of the preference and the two writes go through the
    // shared switch use case are Application's now (Application.TogglePerformance). What stays is the pure wrap
    // rule NextSelectable and the port reads it needs, implemented EXPLICITLY.

    bool ITogglePerformanceTarget.TurboToggles
    {
        get { lock (_state) return Settings.TurboToggles; }
    }

    bool ITogglePerformanceTarget.IsTurboOn() => IsTurboOn();

    /// <summary>The next profile the cycle should land on, from the live source's FILTERED selectable set, so the
    /// hotkey cannot land on a profile the source disables (Acer's NitroSense parity); a port with no availability
    /// policy passes its own set through. Null when this machine offers no profiles at all.</summary>
    PerformanceProfile? ITogglePerformanceTarget.NextSelectableProfile()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        return NextSelectable(pp, pp.Current(), SelectableProfiles());
    }

    private static PerformanceProfile? NextSelectable(IPowerProfiles pp, PerformanceProfile? current,
                                                      IReadOnlyList<PerformanceProfile> sel)
    {
        var all = pp.All;
        if (all.Count == 0) return null;

        var start = 0;
        if (current != null)
            for (var i = 0; i < all.Count; i++) if (all[i].Id == current.Id) { start = i; break; }

        for (var step = 1; step <= all.Count; step++)
        {
            var cand = all[(start + step) % all.Count];
            if (Ok(cand)) return cand;
        }
        return current ?? all[0];

        bool Ok(PerformanceProfile p) => sel.Count == 0 || sel.Any(a => a.Id == p.Id);
    }
}
