using System.Threading;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    public SensorSnapshot ReadSensors() => device.Sensors?.Read() ?? new SensorSnapshot();

    public BatteryInfoSnapshot ReadBatteryInfo() => device.Battery.Read();

    // ---- declared settings (each throws on refusal; the UI catches and composes the message) ----

    /// <summary>The settings this machine's backend declared, as the settings MODEL holds them
    /// (Domain/Settings.cs) — this is what the UI builds its hardware rows from, and the only source of them now
    /// that <see cref="IDevice"/> carries no member for the set.
    ///
    /// Read WITHOUT the graph lock, and that is not an oversight: the lock guards the graph's mutability, and this
    /// list is fixed in the constructor — the model's own COPY, which nothing can append to afterwards (see
    /// <c>Settings</c>'s constructor for why a copy rather than the backend's live list).</summary>
    public IReadOnlyList<SettingDeclaration> DeclaredSettings => Settings.DeclaredSettings;

    /// <summary>Apply one of the settings this machine declares, then record the value under the setting's own
    /// key. Both halves of the switch belong to the model — <c>Settings.Apply</c> hands the value to the option's
    /// own contract (a refusal is a <see cref="SettingNotAppliedException"/>) and <c>Settings.Remember</c> records
    /// what took; what stays here is what the model cannot own: the graph lock and the save.
    ///
    /// THE ORDER IS THE POINT. The hardware write runs OUTSIDE <c>_state</c>, because a setting is an EC/WMI
    /// write and the lock must never span one (docs/domain-refactoring-plan.md §4). Only the recording takes it,
    /// which is also what makes "released on throw" free: a refused write throws out of the model's Apply before
    /// the lock is ever taken, and nothing is recorded. An option this machine does not declare is refused by the
    /// same call, in the same place, with the same exception — see <c>Settings.Apply</c>.
    ///
    /// The refusal is an EXCEPTION rather than a returned pair because the reason it carries is information
    /// about what happened, not a sentence: this layer knows the setting only by its opaque key and has no name
    /// to put in a message (docs/domain-refactoring-plan.md §5, wave 9). The UI owns the label.</summary>
    public void ApplySetting(SettingDeclaration setting, string value)
    {
        Settings.Apply(setting, value);
        lock (_state) { Settings.Remember(setting, value); }
        Save();
    }

    // ---- hardware toggles that are NOT declared settings (each returns the write's outcome AND its reason) ----

    // The battery's properties are not ports: they are ops that answer both halves of their outcome
    // themselves (Domain/Battery.cs), so there is no LastError to fetch afterwards and the shape of the
    // wrapper differs — see the tuple overload of Attempt. Same for the plain-backlight level, a LevelPort (an
    // int) rather than a flag/choice. The declared settings above take neither road: they throw.
    public (bool ok, string? error) SetBatteryToggle(BatteryToggle toggle, bool on)
        => Attempt(() => toggle.Write(on));

    public (bool ok, string? error) SetBatteryChoice(BatteryChoice choice, string id)
        => Attempt(() => choice.Write(id));

    public (bool ok, string? error) SetKeyboardBrightness(int level)
        => device.KeyboardBrightness is { } kb ? Attempt(() => kb.Set(level), () => kb.LastError) : (false, null);

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
    /// (the key is owned by the backend, e.g. Infrastructure/Vendors/Acer). Missing key -> <paramref name="fallback"/>.
    ///
    /// This pair is for the backend-owned flags that are NOT declared settings — the lightbar's
    /// "follows performance profile" flag and the one-shot driver prompt. A DECLARED setting's value lands in
    /// the same bag under its own key (<see cref="ApplySetting"/>), and its own accessors are the declaration's
    /// read and write: a choice's value is an option id, which this bool-shaped reader could not answer for.</summary>
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
