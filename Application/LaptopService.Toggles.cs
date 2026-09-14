using System.Threading;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Application;

public sealed partial class LaptopService
{
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
        lock (_state) { Settings.Bluelight = level; Save(); }
    }

    public void SetClamshell(bool on)
    {
        device.Clamshell?.SetEnabled(on);
        lock (_state) { Settings.Clamshell = on; Save(); }
    }

    public void SetTurboToggles(bool on)
    {
        lock (_state) { Settings.TurboToggles = on; Save(); }
    }

    /// <summary>Persist the chosen UI language. Activating it (and rebuilding the UI) is the app layer's job
    /// (see AppController) — this only records the preference.</summary>
    public void SetLanguage(AppLanguage language)
    {
        lock (_state) { Settings.Language = language; Save(); }
    }

    /// <summary>Persist the lighting state (the lighting view-models mutate <see cref="Settings"/>'s
    /// LightSettings in place, then call this to write them out).</summary>
    public void PersistLighting() => Save();

    /// <summary>Read a vendor-specific device flag from the neutral <see cref="Settings.DeviceSettings"/> bag
    /// (the key is owned by the backend, e.g. Infrastructure/Vendors/Acer). Missing key -> <paramref name="fallback"/>.</summary>
    public bool GetDeviceFlag(string key, bool fallback)
    {
        lock (_state)
            return Settings.DeviceSettings.TryGetValue(key, out var v) ? v == "1" : fallback;
    }

    /// <summary>Set a vendor-specific device flag and persist.</summary>
    public void SetDeviceFlag(string key, bool on)
    {
        lock (_state) { Settings.DeviceSettings[key] = on ? "1" : "0"; Save(); }
    }

    public void EvaluateClamshell() => device.Clamshell?.Evaluate();
}
