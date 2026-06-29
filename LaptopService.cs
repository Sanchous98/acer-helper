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
    private PerformanceProfile? _lastNonTurbo;

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

    public PerformanceProfile? CurrentProfile() => device.PowerProfiles?.Current();

    public IReadOnlyList<PerformanceProfile> SelectableProfiles() =>
        device.PowerProfiles?.Selectable() ?? [];

    public bool ApplyProfile(PerformanceProfile p)
    {
        var pp = device.PowerProfiles;
        if (pp == null) return false;
        if (pp.Set(p)) return true;
        LastError = pp.LastError;
        return false;
    }

    /// <summary>Performance hotkey: cycle profiles, or toggle the Turbo profile (per user setting).
    /// Returns the profile that was applied, or null.</summary>
    public PerformanceProfile? TogglePerformance()
    {
        var pp = device.PowerProfiles;
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

    public void PersistFan(FanMode mode, byte cpu, byte gpu)
    {
        Settings.FanMode = (int)mode;
        Settings.CpuFan = cpu;
        Settings.GpuFan = gpu;
        Save();
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
