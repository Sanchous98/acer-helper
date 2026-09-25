using System.Threading;
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

    /// <summary>The colour the firmware paints the operating-mode indicator for <paramref name="profile"/>, or
    /// null for no profile / a backend with no such palette. Null is the honest answer and it is also what the
    /// lighting coordinator's cache already means by it (no palette to re-send), so the caller needs no branch of
    /// its own — see <c>AppController</c>'s three hand-offs and <c>LightingCoordinator.OnProfileApplied</c>.</summary>
    public AccentColor? FlashColorOf(PerformanceProfile? profile)
        => profile is { } p ? TraitsOf(p).FlashColor : null;

    // Which power source we last saw, and its remembered mode slot. Null = unknown (no reading yet). Access
    // under _state (touched by the background SyncPowerSource and UI-thread profile actions).
    private bool? _onAc;
    private ProfileMemory Slot => _onAc == false ? Settings.OnBattery : Settings.OnAc;

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

    public PerformanceProfile? CurrentProfile() => device.PowerProfiles?.Current();

    /// <summary>The profiles the UI may offer RIGHT NOW: what the port lists as selectable, narrowed by the
    /// vendor's per-source availability policy when it declares one
    /// (<see cref="IProfileAvailability"/> — Acer's NitroSense parity: on battery Eco and Balanced, on AC
    /// Quiet, Balanced, Performance and Turbo). A port with no policy — ASUS, Dell, the generic ports — is
    /// returned verbatim, so nothing outside the declaring vendor is affected. The live source comes from
    /// <see cref="_onAc"/>, set by <see cref="SyncPowerSource"/> before this is read in the refresh pass;
    /// "unknown" (before the first battery reading) reads as AC, the safe default.</summary>
    public IReadOnlyList<PerformanceProfile> SelectableProfiles()
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

    public (bool ok, string? error) ApplyProfile(PerformanceProfile p)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return (false, null);
        lock (_state)
        {
            // A profile this source does not offer is refused rather than written: the UI disables those
            // segments, and this is the backstop for the tray, the hotkey and the per-source paths. Null error
            // means the standard "Failed to set X" line — the reason is already on screen, where the segment
            // is greyed out, so no new message is invented for it.
            if (!AvailableFor(p, _onAc ?? true)) return (false, null);
            var r = Attempt(() => pp.Set(p), () => pp.LastError);
            if (!r.ok) return r;
            // Remember this as the base for the current source; a direct profile pick clears the Turbo flag.
            Slot.BaseId = p.Id;
            Slot.Turbo = false;
            Save();
            return r;
        }
    }

    /// <summary>True if the hardware is currently in the Turbo profile.</summary>
    public bool IsTurboOn() => KindOf(device.PowerProfiles?.Current()) == ProfileKind.Turbo;

    /// <summary>The base (non-Turbo) profile to show as selected: the current profile when it isn't Turbo,
    /// otherwise the remembered base (falling back to Balanced / the first non-Turbo profile).</summary>
    public PerformanceProfile? BaseProfile() => BaseProfile(device.PowerProfiles?.Current());

    /// <summary>As <see cref="BaseProfile()"/> but reusing an already-read current profile.</summary>
    public PerformanceProfile? BaseProfile(PerformanceProfile? cur)
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

    /// <summary>Turbo used as a switch (the "Turbo toggles" mode): on = apply Turbo over the current base;
    /// off = return to the remembered base. The base id is preserved; only the Turbo flag flips.
    /// Returns the profile that actually landed (null on failure) so the caller doesn't have to read it back
    /// out of the hardware to know what it just applied — see AppController's lighting hand-off.</summary>
    public (PerformanceProfile? applied, string? error) SetTurbo(bool on)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return (null, null);
        lock (_state)
        {
            if (on)
            {
                var turbo = pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Turbo);
                // Turbo is an AC-only mode under the declared policy (NitroSense parity): on battery the switch
                // is disabled, and this is the backstop for the hotkey, which reports "nothing landed".
                if (turbo == null || !AvailableFor(turbo, _onAc ?? true)) return (null, null);
                var cur = pp.Current();
                if (cur != null && KindOf(cur) != ProfileKind.Turbo) Slot.BaseId = cur.Id;   // capture the base we sit over
                var r = Attempt(() => pp.Set(turbo), () => pp.LastError);
                if (!r.ok) return (null, r.error);
                Slot.Turbo = true;
                Save();
                return (turbo, null);
            }
            var baseP = BaseProfile();
            if (baseP == null) return (null, null);
            var applied = ApplyProfile(baseP);   // clears Turbo flag + persists base
            return applied.ok ? (baseP, null) : (null, applied.error);
        }
    }

    /// <summary>The profile a power source is set to use — what <see cref="SyncPowerSource"/> applies when the
    /// machine switches to it. Null when nothing is remembered for that source yet (fresh install, before the
    /// first time it was seen). Turbo used as a switch reports as the Turbo profile, because that is what the
    /// slot means to the user even though it is stored as base + flag.
    ///
    /// A slot pointing at a profile the source does not offer reports the FALLBACK rather than the stale id
    /// (NitroSense parity): the per-source row in Options then shows what the machine would actually use, not a
    /// mode the source disables. A port with no availability policy keeps returning the stored id verbatim.</summary>
    public PerformanceProfile? SourceProfile(bool onAc)
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

    /// <summary>Set the profile a power source should use, and apply it right away when that source is the live
    /// one. Stored exactly the way a manual pick stores it (<see cref="ApplyProfile"/>), so the two can't
    /// disagree: base id + a Turbo flag, since Turbo is a switch over a base rather than a mode of its own
    /// while "Turbo toggles" is on. Reports a failure only if the immediate apply failed.</summary>
    public (bool ok, string? error) SetSourceProfile(bool onAc, PerformanceProfile p)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return (false, null);
        lock (_state)
        {
            var slot = onAc ? Settings.OnAc : Settings.OnBattery;
            if (Settings.TurboToggles && KindOf(p) == ProfileKind.Turbo)
            {
                // Turbo is not a base: keep whatever base it sits over, falling back to Balanced when this
                // source has never held one (otherwise dropping Turbo later would leave nothing to return to).
                slot.Turbo = true;
                if (slot.BaseId.Length == 0)
                    slot.BaseId = (pp.All.FirstOrDefault(x => KindOf(x) == ProfileKind.Balanced)
                                ?? pp.All.FirstOrDefault(x => KindOf(x) != ProfileKind.Turbo))?.Id ?? "";
            }
            else { slot.BaseId = p.Id; slot.Turbo = false; }
            Save();
            // Reference equality against the live slot: exact, and it also covers the "source still unknown"
            // case (Slot then reads as the AC slot), so setting the AC profile before any battery reading
            // applies immediately rather than silently waiting for a source change.
            return !ReferenceEquals(Slot, slot) ? (true, (string?)null) : ApplyStoredMode();
        }
    }

    /// <summary>Keep the per-source remembered mode in sync with live battery telemetry. On an AC<->battery
    /// change (and on the first reading, i.e. startup): if this source already has a remembered mode, re-apply
    /// it; if not (fresh install), seed the slot from whatever the hardware is currently set to — never force
    /// a change on a source we've not seen before. A no-op while the source is unchanged or unknown
    /// (desktop / no battery). Called from the refresh loop.</summary>
    public void SyncPowerSource(BatteryInfoSnapshot battery)
    {
        if (battery.State == BatteryState.Unknown) return;            // no battery/source info
        bool onAc = battery.State != BatteryState.Discharging;        // Charging/Idle => on AC
        lock (_state)
        {
            if (_onAc == onAc) return;                                    // no change
            _onAc = onAc;

            if (string.IsNullOrEmpty(Slot.BaseId)) SeedSlotFromHardware();   // first time on this source
            else ApplyStoredMode();                                          // restore what we remembered
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
            ApplyProfile(fb);   // re-entrant lock; applies and re-saves
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

    /// <summary>Apply the live source's remembered mode. Reports a failure only when a hardware write failed —
    /// "nothing to do" (already in that mode, nothing remembered) is success.
    ///
    /// It carries a channel because ONE of its two callers reads it: <see cref="SetSourceProfile"/>'s result is
    /// surfaced by the power-source row in Options, so an error reaching it today through the shared field must
    /// keep reaching it. The other caller (<see cref="SyncPowerSource"/>, from the refresh loop) discards it,
    /// exactly as it did before.</summary>
    private (bool ok, string? error) ApplyStoredMode()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return (false, null);
        var slot = Slot;
        if (string.IsNullOrEmpty(slot.BaseId)) return (true, null);

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
                if (baseP == null) return (true, null);
                slot.BaseId = baseP.Id;
                slot.Turbo = false;
                Save();
            }
            else return (true, null);
        }

        var current = pp.Current();
        var turbo = pp.All.FirstOrDefault(p => KindOf(p) == ProfileKind.Turbo);
        var wantTurbo = slot.Turbo && Settings.TurboToggles
                        && turbo != null && pp.Selectable().Any(p => p.Id == turbo.Id)
                        && AvailableFor(turbo, onAc);

        // Only touch the profile when the hardware isn't already in the target mode. Each Acer profile Set makes
        // the firmware re-flash the keyboard/lightbar palette, so blindly re-applying an already-active mode on
        // every AC<->battery change — the common case, same mode on both sources — just blinks the keyboard for
        // nothing (TWICE in Turbo mode: the old code did base-Set + Turbo-Set unconditionally, briefly dropping
        // out of Turbo and back). The base under Turbo is bookkeeping in the slot (BaseProfile reads it), not the
        // active profile, so we don't need to re-drive it while Turbo is engaged.
        if (wantTurbo)
        {
            if (KindOf(current) == ProfileKind.Turbo) return (true, null);   // already in Turbo -> nothing to do
            if (current?.Id != baseP.Id) pp.Set(baseP);            // establish the base we sit over (skip if on it)
            return Attempt(() => pp.Set(turbo!), () => pp.LastError);
        }
        if (current?.Id == baseP.Id) return (true, null);          // already in the remembered base profile
        return Attempt(() => pp.Set(baseP), () => pp.LastError);
    }

    /// <summary>Performance hotkey: cycle profiles, or toggle Turbo (per the "Turbo toggles" setting).
    /// Returns the profile that was applied, or null.</summary>
    public PerformanceProfile? TogglePerformance()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;

        bool turboToggles;
        lock (_state) turboToggles = Settings.TurboToggles;
        if (turboToggles)
            return SetTurbo(!IsTurboOn()).applied;   // returns what landed — no read-back needed

        // The cycle runs over the FILTERED set, so the hotkey cannot land on a profile the live source
        // disables (Acer's NitroSense parity); a port with no availability policy passes its own set through.
        var target = NextSelectable(pp, pp.Current(), SelectableProfiles());
        return target != null && ApplyProfile(target).ok ? target : null;
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
