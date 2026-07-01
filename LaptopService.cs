using AcerHelper.Features;

namespace AcerHelper;

/// <summary>
/// Application facade / use-case layer. The UI talks only to this and to the Domain model;
/// this talks only to Domain feature ports (<see cref="IDevice"/>) and the settings store.
/// All orchestration (profile cycling/toggling, persistence of changes) lives here, never in
/// the UI or in Infrastructure.
/// </summary>
public sealed class LaptopService(IDevice device, ISettingsStore store) : IDisposable
{
    /// <summary>The connected device. The UI reads its (nullable) feature ports to decide which
    /// sections to show; it must route all mutations through this service's methods.</summary>
    public IDevice Device => device;
    public Settings Settings { get; } = store.Load();
    public string? LastError { get; private set; }

    private void Save() => store.Save(Settings);

    /// <summary>Re-apply persisted state that the OS doesn't remember on its own.</summary>
    public void ApplyStartupState()
    {
        if (Settings.Clamshell) device.Clamshell?.SetEnabled(true);
        if (Settings.Bluelight > 0) device.DisplayTint?.Apply(Settings.Bluelight);
    }

    // ---- performance profiles ----

    // Which power source we last saw, and its remembered mode slot. Null = unknown (no reading yet).
    private bool? _onAc;
    private ProfileMemory Slot => _onAc == false ? Settings.OnBattery : Settings.OnAc;

    /// <summary>Key identifying the current performance "mode" for per-mode presets: the profile id, except
    /// Turbo used as a switch shares its base profile's key (Turbo isn't a standalone mode then). "default"
    /// when the device has no profiles.</summary>
    public string CurrentModeKey()
    {
        var cur = device.PowerProfiles?.Current();
        if (cur == null) return "default";
        if (Settings.TurboToggles && cur.Kind == ProfileKind.Turbo && Slot.BaseId.Length > 0) return Slot.BaseId;
        return cur.Id;
    }

    public PerformanceProfile? CurrentProfile() => device.PowerProfiles?.Current();

    public IReadOnlyList<PerformanceProfile> SelectableProfiles() =>
        device.PowerProfiles?.Selectable() ?? [];

    public bool ApplyProfile(PerformanceProfile p)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return false;
        if (!pp.Set(p)) { LastError = pp.LastError; return false; }
        // Remember this as the base for the current source; a direct profile pick clears the Turbo flag.
        Slot.BaseId = p.Id;
        Slot.Turbo = false;
        Save();
        return true;
    }

    /// <summary>True if the hardware is currently in the Turbo profile.</summary>
    public bool IsTurboOn() => device.PowerProfiles?.Current()?.Kind == ProfileKind.Turbo;

    /// <summary>The base (non-Turbo) profile to show as selected: the current profile when it isn't Turbo,
    /// otherwise the remembered base (falling back to Balanced / the first non-Turbo profile).</summary>
    public PerformanceProfile? BaseProfile()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;
        var cur = pp.Current();
        if (cur != null && cur.Kind != ProfileKind.Turbo) return cur;
        return pp.All.FirstOrDefault(p => p.Id == Slot.BaseId)
            ?? pp.All.FirstOrDefault(p => p.Kind == ProfileKind.Balanced)
            ?? pp.All.FirstOrDefault(p => p.Kind != ProfileKind.Turbo);
    }

    /// <summary>Turbo used as a switch (the "Turbo toggles" mode): on = apply Turbo over the current base;
    /// off = return to the remembered base. The base id is preserved; only the Turbo flag flips.</summary>
    public bool SetTurbo(bool on)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return false;
        if (on)
        {
            var turbo = pp.All.FirstOrDefault(p => p.Kind == ProfileKind.Turbo);
            if (turbo == null) return false;
            var cur = pp.Current();
            if (cur != null && cur.Kind != ProfileKind.Turbo) Slot.BaseId = cur.Id;   // capture the base we sit over
            if (!pp.Set(turbo)) { LastError = pp.LastError; return false; }
            Slot.Turbo = true;
            Save();
            return true;
        }
        var baseP = BaseProfile();
        return baseP != null && ApplyProfile(baseP);   // clears Turbo flag + persists base
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
        if (_onAc == onAc) return;                                    // no change
        _onAc = onAc;

        if (string.IsNullOrEmpty(Slot.BaseId)) SeedSlotFromHardware();   // first time on this source
        else ApplyStoredMode();                                          // restore what we remembered
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

    private void ApplyStoredMode()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return;
        var slot = Slot;
        if (string.IsNullOrEmpty(slot.BaseId)) return;

        var baseP = pp.All.FirstOrDefault(p => p.Id == slot.BaseId);
        if (baseP == null) return;
        pp.Set(baseP);                                                // set directly: don't rewrite the slot

        if (slot.Turbo && Settings.TurboToggles)
        {
            var turbo = pp.All.FirstOrDefault(p => p.Kind == ProfileKind.Turbo);
            if (turbo != null && pp.Selectable().Any(p => p.Id == turbo.Id)) pp.Set(turbo);
        }
    }

    /// <summary>Performance hotkey: cycle profiles, or toggle Turbo (per the "Turbo toggles" setting).
    /// Returns the profile that was applied, or null.</summary>
    public PerformanceProfile? TogglePerformance()
    {
        var pp = device.PowerProfiles;
        if (pp == null) return null;

        if (Settings.TurboToggles)
            return SetTurbo(!IsTurboOn()) ? pp.Current() : null;

        var target = NextSelectable(pp, pp.Current());
        return target != null && ApplyProfile(target) ? target : null;
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

    // ---- fans ----

    public bool ApplyFan(FanMode mode, byte cpu, byte gpu)
    {
        var fc = device.FanControl;
        if (fc == null) return false;
        var ok = mode == FanMode.Custom ? fc.SetCustomSpeeds(cpu, gpu) : fc.SetMode(mode);
        if (!ok) LastError = fc.LastError;
        return ok;
    }

    /// <summary>Save the fan selection for the CURRENT performance mode.</summary>
    public void PersistFan(FanMode mode, byte cpu, byte gpu)
    {
        Settings.FanPresets[CurrentModeKey()] = new FanPreset { Mode = (int)mode, Cpu = cpu, Gpu = gpu };
        Save();
    }

    /// <summary>The fan preset for the current mode, or defaults (Auto) if none is saved yet.</summary>
    public FanPreset CurrentFan()
        => Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f) ? f : new FanPreset();

    /// <summary>Apply the current mode's saved fan preset to the hardware (called on a mode change). Returns
    /// the applied preset so the UI can reflect it, or null if this mode has no saved preset — then the fans
    /// are left as they are (we can't read the current fan mode back to seed one).</summary>
    public FanPreset? ApplyModeFan()
    {
        if (!Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f)) return null;
        ApplyFan((FanMode)f.Mode, (byte)f.Cpu, (byte)f.Gpu);
        return f;
    }

    // ---- lighting (per-mode) ----

    /// <summary>The per-zone lighting state for the current mode (created empty on first use).</summary>
    public Dictionary<string, LightSettings> LightsForCurrentMode()
    {
        var key = CurrentModeKey();
        if (!Settings.LightPresets.TryGetValue(key, out var p)) Settings.LightPresets[key] = p = new LightPreset();
        return p.Zones;
    }

    public SensorSnapshot ReadSensors() => device.Sensors?.Read() ?? new SensorSnapshot();

    public BatteryInfoSnapshot ReadBatteryInfo() => device.BatteryInfo?.Read() ?? new BatteryInfoSnapshot();

    // ---- hardware toggles (return success; LastError holds the reason on failure) ----

    public bool SetBatteryLimit(bool on)       => Run(device.BatteryChargeLimit, x => x.Set(on), x => x.LastError);
    public bool SetBatteryCalibration(bool on) => Run(device.BatteryCalibration, x => x.Set(on), x => x.LastError);
    public bool SetLcdOverdrive(bool on)       => Run(device.LcdOverdrive,       x => x.Set(on), x => x.LastError);
    public bool SetUsbCharging(int level)      => Run(device.UsbCharging,        x => x.Set(level), x => x.LastError);
    public bool SetBacklightTimeout(bool on)   => Run(device.KeyboardBacklight,  x => x.SetTimeout(on), x => x.LastError);

    public bool SetAutostart(bool on) => device.Autostart?.SetEnabled(on) ?? false;

    public void SetBlueLight(int level)
    {
        device.DisplayTint?.Apply(level);
        Settings.Bluelight = level;
        Save();
    }

    public void SetClamshell(bool on)
    {
        device.Clamshell?.SetEnabled(on);
        Settings.Clamshell = on;
        Save();
    }

    public void SetTurboToggles(bool on)
    {
        Settings.TurboToggles = on;
        Save();
    }

    /// <summary>Persist the lighting state (the lighting view-models mutate <see cref="Settings"/>'s
    /// LightSettings in place, then call this to write them out).</summary>
    public void PersistLighting() => Save();

    public void EvaluateClamshell() => device.Clamshell?.Evaluate();

    private bool Run<T>(T? svc, Func<T, bool> set, Func<T, string?> err) where T : class
    {
        if (svc == null) return false;
        bool ok;
        try { ok = set(svc); }
        catch { ok = false; }
        if (!ok) LastError = err(svc);
        return ok;
    }

    public void Dispose() => device.Dispose();
}
