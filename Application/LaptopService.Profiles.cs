using System.Threading;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Application;

public sealed partial class LaptopService
{
    // ---- performance profiles ----

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
        // The derivation itself now lives in Domain/ModeKey.cs; this keeps the method's exact former shape,
        // including the early return that does NOT take the lock — the null case reads nothing, and the original
        // deliberately answered it without acquiring _state.
        //
        // The SLOT is read here, under the lock, and passed by value. That is not a style choice: it is the
        // remembered mode of the LIVE power source, so reading it inside the domain type would either put the
        // lock there or drop it, and hoisting a guarded read out of its lock is the hazard recorded in
        // docs/open-decisions.md §3. The lock is now held across the whole derivation rather than only the Turbo
        // branch — a superset, and free: `cur` is already materialised and ModeKey.For is pure, so the hold is
        // two field reads.
        if (cur == null) return ModeKey.None.Value;
        lock (_state)
            return ModeKey.For(cur, Settings.TurboToggles, Slot).Value;
    }

    public PerformanceProfile? CurrentProfile() => device.PowerProfiles?.Current();

    public IReadOnlyList<PerformanceProfile> SelectableProfiles() =>
        device.PowerProfiles?.Selectable() ?? [];

    public (bool ok, string? error) ApplyProfile(PerformanceProfile p)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return (false, null);
        lock (_state)
        {
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
    public bool IsTurboOn() => device.PowerProfiles?.Current()?.Kind == ProfileKind.Turbo;

    /// <summary>The base (non-Turbo) profile to show as selected: the current profile when it isn't Turbo,
    /// otherwise the remembered base (falling back to Balanced / the first non-Turbo profile).</summary>
    public PerformanceProfile? BaseProfile() => BaseProfile(device.PowerProfiles?.Current());

    /// <summary>As <see cref="BaseProfile()"/> but reusing an already-read current profile.</summary>
    public PerformanceProfile? BaseProfile(PerformanceProfile? cur)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        if (cur != null && cur.Kind != ProfileKind.Turbo) return cur;
        lock (_state)
            return pp.All.FirstOrDefault(p => p.Id == Slot.BaseId)
                ?? pp.All.FirstOrDefault(p => p.Kind == ProfileKind.Balanced)
                ?? pp.All.FirstOrDefault(p => p.Kind != ProfileKind.Turbo);
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
                var turbo = pp.All.FirstOrDefault(p => p.Kind == ProfileKind.Turbo);
                if (turbo == null) return (null, null);
                var cur = pp.Current();
                if (cur != null && cur.Kind != ProfileKind.Turbo) Slot.BaseId = cur.Id;   // capture the base we sit over
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
    /// slot means to the user even though it is stored as base + flag.</summary>
    public PerformanceProfile? SourceProfile(bool onAc)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        lock (_state)
        {
            var slot = onAc ? Settings.OnAc : Settings.OnBattery;
            if (Settings.TurboToggles && slot.Turbo &&
                pp.All.FirstOrDefault(p => p.Kind == ProfileKind.Turbo) is { } turbo) return turbo;
            return pp.All.FirstOrDefault(p => p.Id == slot.BaseId);
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
            if (Settings.TurboToggles && p.Kind == ProfileKind.Turbo)
            {
                // Turbo is not a base: keep whatever base it sits over, falling back to Balanced when this
                // source has never held one (otherwise dropping Turbo later would leave nothing to return to).
                slot.Turbo = true;
                if (slot.BaseId.Length == 0)
                    slot.BaseId = (pp.All.FirstOrDefault(x => x.Kind == ProfileKind.Balanced)
                                ?? pp.All.FirstOrDefault(x => x.Kind != ProfileKind.Turbo))?.Id ?? "";
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

    /// <summary>Populate the current source's slot from the hardware's current profile (no change applied),
    /// so a fresh install remembers what the machine was already on rather than forcing a default.</summary>
    private void SeedSlotFromHardware()
    {
        var pp = device.PowerProfiles;
        var cur = pp?.Current();
        if (cur == null) return;
        if (cur.Kind == ProfileKind.Turbo)
        {
            Slot.Turbo = true;                                       // base under Turbo is unknown -> best guess
            Slot.BaseId = (pp!.All.FirstOrDefault(p => p.Kind == ProfileKind.Balanced)
                        ?? pp.All.FirstOrDefault(p => p.Kind != ProfileKind.Turbo))?.Id ?? "";
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

        var baseP = pp.All.FirstOrDefault(p => p.Id == slot.BaseId);
        if (baseP == null) return (true, null);

        var current = pp.Current();
        var turbo = pp.All.FirstOrDefault(p => p.Kind == ProfileKind.Turbo);
        var wantTurbo = slot.Turbo && Settings.TurboToggles
                        && turbo != null && pp.Selectable().Any(p => p.Id == turbo.Id);

        // Only touch the profile when the hardware isn't already in the target mode. Each Acer profile Set makes
        // the firmware re-flash the keyboard/lightbar palette, so blindly re-applying an already-active mode on
        // every AC<->battery change — the common case, same mode on both sources — just blinks the keyboard for
        // nothing (TWICE in Turbo mode: the old code did base-Set + Turbo-Set unconditionally, briefly dropping
        // out of Turbo and back). The base under Turbo is bookkeeping in the slot (BaseProfile reads it), not the
        // active profile, so we don't need to re-drive it while Turbo is engaged.
        if (wantTurbo)
        {
            if (current?.Kind == ProfileKind.Turbo) return (true, null);   // already in Turbo -> nothing to do
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

        var target = NextSelectable(pp, pp.Current());
        return target != null && ApplyProfile(target).ok ? target : null;
    }

    private static PerformanceProfile? NextSelectable(IPowerProfiles pp, PerformanceProfile? current)
    {
        var all = pp.All;
        if (all.Count == 0) return null;
        var sel = pp.Selectable();

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
