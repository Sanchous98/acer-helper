using AcerHelper.Features;
using AcerHelper.Localization;

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
        ApplyModeGpuOc();   // GPU clock offsets reset to 0 on boot/driver-reload -> re-apply the current mode's
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
            if (current?.Kind == ProfileKind.Turbo) return;    // already in Turbo -> nothing to do
            if (current?.Id != baseP.Id) pp.Set(baseP);        // establish the base we sit over (skip if on it)
            pp.Set(turbo!);
        }
        else
        {
            if (current?.Id == baseP.Id) return;               // already in the remembered base profile
            pp.Set(baseP);
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

    // The emulated fan-curve controller (anchors, default ramp, hysteresis/deadband). Custom mode drives the
    // fans through it on the sensor loop; Reset() on any out-of-band change so the deadband can't swallow the
    // first write.
    private readonly FanCurveEngine _fanCurve = new();

    public bool ApplyFan(FanMode mode, byte cpu, byte gpu)
    {
        var fc = device.FanControl;
        if (fc == null) return false;
        bool ok;
        if (mode == FanMode.Custom)
        {
            // The EC honours a manual speed only once the fan is in CUSTOM behaviour; setting the speed alone
            // (without switching behaviour out of Auto) is silently ignored. So put the fans in custom mode
            // first, then push the speeds. (Both Custom and the emulated curve go through here.)
            fc.SetMode(FanMode.Custom);
            ok = fc.SetCustomSpeeds(cpu, gpu);
        }
        else ok = fc.SetMode(mode);
        if (!ok) LastError = fc.LastError;
        return ok;
    }

    /// <summary>The stored preset for the current mode, created on first write (user is configuring it).</summary>
    private FanPreset StoredFan()
    {
        var key = CurrentModeKey();
        if (!Settings.FanPresets.TryGetValue(key, out var f)) Settings.FanPresets[key] = f = new FanPreset();
        return f;
    }

    /// <summary>Set the fan mode + fixed speeds for the CURRENT mode and apply now. Per-fan curve settings are
    /// preserved; in Custom mode a fan's real speed is its curve value when that fan's curve is on, else the
    /// fixed speed set here (see <see cref="ApplyCustom"/>).</summary>
    public void SetFan(FanMode mode, byte cpu, byte gpu)
    {
        var f = StoredFan();
        f.Mode = (int)mode; f.Cpu = cpu; f.Gpu = gpu;
        _fanCurve.Reset();
        Save();
        if (mode == FanMode.Custom) ApplyCustom(ReadSensors());
        else ApplyFan(mode, cpu, gpu);
    }

    /// <summary>Turn one fan's curve on/off and store its points, for the CURRENT mode, then apply now.</summary>
    public void SetFanCurve(bool gpu, bool use, int[] points)
    {
        var f = StoredFan();
        if (gpu) { f.GpuUseCurve = use; f.GpuCurve = points; }
        else     { f.CpuUseCurve = use; f.CpuCurve = points; }
        _fanCurve.Reset();
        Save();
        ApplyCustom(ReadSensors());
    }

    /// <summary>The fan preset for the current mode, or defaults if none is saved yet (not stored).</summary>
    public FanPreset CurrentFan()
        => Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f) ? f : new FanPreset();

    /// <summary>Apply the current mode's saved fan preset on a mode change. Auto/Max are pushed immediately;
    /// Custom is left to <see cref="ApplyCustom"/> (the refresh loop) so per-fan curves track temperature.
    /// Returns the preset so the UI reflects it, or null if this mode has none (fans left untouched).</summary>
    public FanPreset? ApplyModeFan()
    {
        if (!Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f)) return null;
        _fanCurve.Reset();
        if ((FanMode)f.Mode != FanMode.Custom) ApplyFan((FanMode)f.Mode, (byte)f.Cpu, (byte)f.Gpu);
        return f;
    }

    /// <summary>Drive the fans in Custom mode: each fan uses its curve value (mapped from the live temp) when
    /// its curve is on, otherwise its fixed speed. Applied with a deadband so the fans don't hunt. A no-op
    /// outside Custom mode. Called every refresh (reuses the already-read sensors — no extra hardware access).</summary>
    public void ApplyCustom(SensorSnapshot s)
    {
        if (device.FanControl == null ||
            !Settings.FanPresets.TryGetValue(CurrentModeKey(), out var f) || (FanMode)f.Mode != FanMode.Custom)
        { _fanCurve.Reset(); return; }

        if (_fanCurve.Step(f, s) is not { } duty) return;                       // within deadband
        if (ApplyFan(FanMode.Custom, (byte)duty.cpu, (byte)duty.gpu))
            _fanCurve.Commit(duty.cpu, duty.gpu);                               // advance state only on success
    }

    // ---- lighting (per-mode) ----

    /// <summary>The per-zone lighting state for the current mode (created empty on first use).</summary>
    public Dictionary<string, LightSettings> LightsForCurrentMode()
    {
        var key = CurrentModeKey();
        if (!Settings.LightPresets.TryGetValue(key, out var p)) Settings.LightPresets[key] = p = new LightPreset();
        return p.Zones;
    }

    // ---- GPU overclock (per-mode) ----

    /// <summary>The stored GPU-OC preset for the current mode, created on first write (user is configuring it).</summary>
    private GpuOcPreset StoredGpuOc()
    {
        var key = CurrentModeKey();
        if (!Settings.GpuOcPresets.TryGetValue(key, out var g)) Settings.GpuOcPresets[key] = g = new GpuOcPreset();
        return g;
    }

    /// <summary>The GPU-OC preset for the current mode, or stock (0/0) if none is saved yet (not stored).</summary>
    public GpuOcPreset CurrentGpuOc()
        => Settings.GpuOcPresets.TryGetValue(CurrentModeKey(), out var g) ? g : new GpuOcPreset();

    /// <summary>Set the GPU core+memory clock offsets (MHz) for the CURRENT mode, persist, and apply now.</summary>
    public bool SetGpuOc(int core, int mem)
    {
        var g = StoredGpuOc();
        g.Core = core; g.Mem = mem;
        Save();
        var oc = device.GpuOverclock;
        if (oc == null) return false;
        if (!oc.Set(core, mem)) { LastError = oc.LastError; return false; }
        return true;
    }

    /// <summary>Apply the current mode's GPU offsets to the hardware. Defaults to stock (0/0) when the mode has
    /// no saved preset — the driver zeroes offsets on boot, so a never-configured mode is definitely stock and
    /// switching to it must clear whatever the previous mode applied. Returns the preset so the UI reflects it.
    /// Called on a mode change, at startup, and on resume.</summary>
    public GpuOcPreset ApplyModeGpuOc()
    {
        var g = CurrentGpuOc();
        device.GpuOverclock?.Set(g.Core, g.Mem);
        return g;
    }

    public SensorSnapshot ReadSensors() => device.Sensors?.Read() ?? new SensorSnapshot();

    public BatteryInfoSnapshot ReadBatteryInfo() => device.BatteryInfo?.Read() ?? new BatteryInfoSnapshot();

    // ---- hardware toggles (return success; LastError holds the reason on failure) ----

    // Every simple on/off or pick-one control routes through one of these two: the port carries its own
    // error channel, so the caller (OptionsAssembler) passes the device's nullable port + the new value.
    // SetKeyboardBrightness stays its own method — it's a LevelPort (an int), not a flag/choice.
    public bool SetFlag(IFlagPort? port, bool on)       => Run(port, x => x.Set(on), x => x.LastError);
    public bool SetChoice(IChoicePort? port, string id) => Run(port, x => x.Set(id), x => x.LastError);
    public bool SetKeyboardBrightness(int level)        => Run(device.KeyboardBrightness, x => x.Set(level), x => x.LastError);

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

    /// <summary>Persist the chosen UI language. Activating it (and rebuilding the UI) is the app layer's job
    /// (see AppController) — this only records the preference.</summary>
    public void SetLanguage(AppLanguage language)
    {
        Settings.Language = language;
        Save();
    }

    /// <summary>Persist the lighting state (the lighting view-models mutate <see cref="Settings"/>'s
    /// LightSettings in place, then call this to write them out).</summary>
    public void PersistLighting() => Save();

    /// <summary>Read a vendor-specific device flag from the neutral <see cref="Settings.DeviceSettings"/> bag
    /// (the key is owned by the backend, e.g. Vendors/Acer). Missing key -> <paramref name="fallback"/>.</summary>
    public bool GetDeviceFlag(string key, bool fallback)
        => Settings.DeviceSettings.TryGetValue(key, out var v) ? v == "1" : fallback;

    /// <summary>Set a vendor-specific device flag and persist.</summary>
    public void SetDeviceFlag(string key, bool on) { Settings.DeviceSettings[key] = on ? "1" : "0"; Save(); }

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
