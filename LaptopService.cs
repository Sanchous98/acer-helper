namespace AcerHelper;

/// <summary>
/// Application facade / use-case layer. The UI talks only to this and to the Domain model;
/// this talks only to Domain feature ports (<see cref="IDevice"/>) and the settings store.
/// All orchestration (profile cycling/toggling, persistence of changes) lives here, never in
/// the UI or in Infrastructure.
/// </summary>
public sealed class LaptopService : IDisposable
{
    private readonly IDevice _device;
    private readonly ISettingsStore _store;
    private PerformanceProfile? _lastNonTurbo;

    public LaptopService(IDevice device, ISettingsStore store)
    {
        _device = device;
        _store = store;
        Settings = store.Load();
    }

    /// <summary>The connected device. The UI reads its (nullable) feature ports to decide which
    /// sections to show; it must route all mutations through this service's methods.</summary>
    public IDevice Device => _device;
    public Settings Settings { get; }
    public string? LastError { get; private set; }

    public void Save() => _store.Save(Settings);

    /// <summary>Re-apply persisted state that the OS doesn't remember on its own.</summary>
    public void ApplyStartupState()
    {
        if (Settings.Clamshell) _device.Clamshell?.SetEnabled(true);
        if (Settings.Bluelight > 0) _device.DisplayTint?.Apply(Settings.Bluelight);
    }

    // ---- performance profiles ----

    public PerformanceProfile? CurrentProfile() => _device.PowerProfiles?.Current();

    public IReadOnlyList<PerformanceProfile> SelectableProfiles() =>
        _device.PowerProfiles?.Selectable() ?? [];

    public bool ApplyProfile(PerformanceProfile p)
    {
        var pp = _device.PowerProfiles;
        if (pp == null) return false;
        if (pp.Set(p)) return true;
        LastError = pp.LastError;
        return false;
    }

    /// <summary>Performance hotkey: cycle profiles, or toggle the Turbo profile (per user setting).
    /// Returns the profile that was applied, or null.</summary>
    public PerformanceProfile? TogglePerformance()
    {
        var pp = _device.PowerProfiles;
        if (pp == null) return null;

        var cur = pp.Current();
        PerformanceProfile? target;
        if (Settings.TurboToggles)
        {
            var turbo = pp.All.FirstOrDefault(p => p.Kind == ProfileKind.Turbo);
            if (cur?.Kind == ProfileKind.Turbo) target = _lastNonTurbo ?? turbo;
            else { if (cur != null) _lastNonTurbo = cur; target = turbo; }
        }
        else target = NextSelectable(pp, cur);

        return target != null && pp.Set(target) ? target : null;
    }

    private static PerformanceProfile? NextSelectable(IPowerProfiles pp, PerformanceProfile? current)
    {
        var all = pp.All;
        if (all.Count == 0) return null;
        var sel = pp.Selectable();
        bool Ok(PerformanceProfile p) => sel.Count == 0 || sel.Any(a => a.Id == p.Id);

        var start = 0;
        if (current != null)
            for (var i = 0; i < all.Count; i++) if (all[i].Id == current.Id) { start = i; break; }

        for (var step = 1; step <= all.Count; step++)
        {
            var cand = all[(start + step) % all.Count];
            if (Ok(cand)) return cand;
        }
        return current ?? all[0];
    }

    // ---- fans ----

    public bool ApplyFan(FanMode mode, byte cpu, byte gpu)
    {
        var fc = _device.FanControl;
        if (fc == null) return false;
        var ok = mode == FanMode.Custom ? fc.SetCustomSpeeds(cpu, gpu) : fc.SetMode(mode);
        if (!ok) LastError = fc.LastError;
        return ok;
    }

    public void PersistFan(FanMode mode, byte cpu, byte gpu)
    {
        Settings.FanMode = (int)mode;
        Settings.CpuFan = cpu;
        Settings.GpuFan = gpu;
        Save();
    }

    public SensorSnapshot ReadSensors() =>
        _device.Sensors?.Read() ?? new SensorSnapshot { CpuTempC = -1, GpuTempC = -1, CpuFanRpm = -1, GpuFanRpm = -1 };

    // ---- hardware toggles (return success; LastError holds the reason on failure) ----

    public bool SetBatteryLimit(bool on)       => Run(_device.BatteryChargeLimit, x => x.Set(on), x => x.LastError);
    public bool SetBatteryCalibration(bool on) => Run(_device.BatteryCalibration, x => x.Set(on), x => x.LastError);
    public bool SetLcdOverdrive(bool on)       => Run(_device.LcdOverdrive,       x => x.Set(on), x => x.LastError);
    public bool SetUsbCharging(int level)      => Run(_device.UsbCharging,        x => x.Set(level), x => x.LastError);
    public bool SetBacklightTimeout(bool on)   => Run(_device.KeyboardBacklight,  x => x.SetTimeout(on), x => x.LastError);

    public bool SetAutostart(bool on) => _device.Autostart?.SetEnabled(on) ?? false;

    public void SetBlueLight(int level)
    {
        _device.DisplayTint?.Apply(level);
        Settings.Bluelight = level;
        Save();
    }

    public void SetClamshell(bool on)
    {
        _device.Clamshell?.SetEnabled(on);
        Settings.Clamshell = on;
        Save();
    }

    public void SetTurboToggles(bool on)
    {
        Settings.TurboToggles = on;
        Save();
    }

    public void EvaluateClamshell() => _device.Clamshell?.Evaluate();

    private bool Run<T>(T? svc, Func<T, bool> set, Func<T, string?> err) where T : class
    {
        if (svc == null) return false;
        bool ok;
        try { ok = set(svc); }
        catch { ok = false; }
        if (!ok) LastError = err(svc);
        return ok;
    }

    public void Dispose() => _device.Dispose();
}
