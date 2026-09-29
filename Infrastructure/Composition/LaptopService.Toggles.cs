using System.Threading;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    // ---- the sensor and battery READ contracts (Application/Queries.cs) ----
    //
    // Two port reads with no graph and no policy, implemented EXPLICITLY so nothing is added to this class's
    // public surface: the UI reaches them by naming the use case (ReadSensors, ReadBatteryInfo). The bodies are
    // the internal helpers below, which this class's own paths (the fan drive) still call by name.

    /// <summary>The live sensors, for the ReadSensors use case.</summary>
    SensorSnapshot ISensorsReadTarget.Read() => ReadSensors();

    /// <summary>The live sensor snapshot, or the all-unavailable one with no sensor port. `internal` rather than
    /// public: the UI reaches it through the ReadSensors use case, and the fan drive calls it by name.</summary>
    internal SensorSnapshot ReadSensors() => device.Sensors?.Read() ?? new SensorSnapshot();

    /// <summary>The battery reading, for the ReadBatteryInfo use case.</summary>
    BatteryInfoSnapshot IBatteryReadTarget.Read() => ReadBatteryInfo();

    /// <summary>The live battery snapshot through the battery object, or the all-unknown one with no telemetry.
    /// `internal` rather than public: the UI reaches it through the ReadBatteryInfo use case.</summary>
    internal BatteryInfoSnapshot ReadBatteryInfo() => device.Battery.Read();

    // ---- declared settings (each throws on refusal; the UI catches and composes the message) ----

    /// <summary>The settings this machine's backend declared, as the settings MODEL holds them
    /// (Infrastructure/Composition/Settings.cs) — this is what the UI builds its hardware rows from, and the model's
    /// copy of the set is the only source: the machine carries what was declared, and the service hands it to the
    /// model at construction (<c>Settings</c>'s own docstring says why the copy).
    ///
    /// Read WITHOUT the graph lock, and that is not an oversight: the lock guards the graph's mutability, and this
    /// list is fixed in the constructor — the model's own COPY, which nothing can append to afterwards (see
    /// <c>Settings</c>'s constructor for why a copy rather than the backend's live list).</summary>
    public IReadOnlyList<SettingDeclaration> DeclaredSettings => Settings.DeclaredSettings;

    /// <summary>The hardware write, OUTSIDE the graph lock — a setting is an EC/WMI write and the lock must never
    /// span one (docs/domain-refactoring-plan.md §4). That is also what makes "released on throw" free: a refused
    /// write throws out of the model's Apply before any lock is taken, and nothing is recorded.</summary>
    void IDeclaredSettingTarget.Write(SettingDeclaration setting, string value)
        => Settings.Apply(setting, value);

    /// <summary>Record what took, under the option's own key — a write into the shared bag, so this member takes
    /// the lock itself rather than being handed one.</summary>
    void IDeclaredSettingTarget.Remember(SettingDeclaration setting, string value)
    {
        lock (_state) { Settings.Remember(setting, value); }
    }

    /// <summary>Write the graph out. A separate member rather than folded into the record above, because the
    /// record has always landed under one hold and the file been written under the next.</summary>
    void IDeclaredSettingTarget.Persist() => Save();

    // ---- the battery, keyboard-backlight and autostart contracts (Application/HardwareToggles.cs) ----
    //
    // The battery's properties are not ports: they are ops that answer both halves of their outcome themselves
    // (Domain/Battery.cs), so there is no LastError to fetch afterwards and the shape of the wrapper differs — see
    // the tuple overload of Attempt. Same for the plain-backlight level, a LevelPort (an int) rather than a
    // flag/choice. All are implemented EXPLICITLY so nothing is added to this class's public surface; a caller
    // reaches them by naming a use case. The declared settings above take neither road: they throw.

    /// <summary>Write a battery on/off property and report both halves: a throw is a failed write carrying no
    /// reason, exactly as the port shape produces when its value was never assigned.</summary>
    (bool ok, string? error) IBatteryControlTarget.Toggle(BatteryToggle toggle, bool on)
        => Attempt(() => toggle.Write(on));

    (bool ok, string? error) IBatteryControlTarget.Choice(BatteryChoice choice, string id)
        => Attempt(() => choice.Write(id));

    /// <summary>Write the plain-backlight level: false with no reason when this machine has no backlight port, and
    /// the port's own <c>LastError</c> when a write that RETURNED refused (a throw reports none).</summary>
    (bool ok, string? error) IKeyboardBrightnessTarget.Set(int level)
        => device.KeyboardBrightness is { } kb ? Attempt(() => kb.Set(level), () => kb.LastError) : (false, null);

    /// <summary>Register or remove the run-at-logon entry. A machine with no autostart port reports false, which is
    /// the same "this machine has nothing to write" the row's read reports for an absent property.</summary>
    bool IAutostartTarget.Set(bool on) => device.Autostart?.SetEnabled(on) ?? false;

    /// <summary>
    /// The blue-light applies, one at a time and OFF THE CALLER'S THREAD. One instance for the service's life, so
    /// the rules in <c>Vendors/Generic/TintApplyPolicy.cs</c> hold across a UI rebuild as well as across two
    /// clicks: a level change is a slow, verified write (KWin walks to the new temperature before the link's
    /// read-back can confirm it) and it used to be made inline on the UI thread, because the row has no read-back
    /// and <c>ChoiceRowViewModel</c> applies such a pick inline — a click could freeze the window for ~2 s.
    /// </summary>
    private readonly TintApplyPolicy _tintApplies = new();

    /// <summary>
    /// How long teardown waits for an in-flight tint apply (<see cref="LaptopService.Dispose"/>).
    ///
    /// Sized over the TRANSPORT's own wait rather than picked: the slow half of an apply is the link's commit poll,
    /// whose ceiling is 2500 ms, and everything else in one is a handful of ~6 ms process spawns. This is the
    /// outer bound only — it exists so a wedged apply cannot hold the app open, not to be reached.
    /// </summary>
    private static readonly TimeSpan TintDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Test seam: the blue-light apply schedule, so a test can WAIT for work that is deliberately on no
    /// caller's thread (<c>TintApplyPolicy.Drain</c>). Exists for the same reason as <see cref="LaptopService.StateHeld"/>:
    /// "the apply is not on the caller's thread" is not observable from a port fake, which sees the call and never
    /// the thread it arrived on.</summary>
    internal TintApplyPolicy TintApplies => _tintApplies;

    /// <summary>Record the blue-light level, under the graph lock, and persist it. The use case calls this FIRST;
    /// the hardware write is <see cref="IBlueLightTarget.Schedule"/>, and the order is what makes a level survive
    /// a death between the click and the write (see <see cref="SetBlueLight"/>).</summary>
    void IBlueLightTarget.Remember(int level)
    {
        lock (_state) { Settings.Bluelight = level; Save(); }
    }

    /// <summary>Hand the hardware write for <paramref name="level"/> to the schedule above. NOT under
    /// <c>_state</c> (design doc D17, and the rule every port call in this class follows) and not on this thread
    /// at all. The recording stays synchronous (the schedule must not delay what the app remembers);
    /// <paramref name="onApplied"/> is answered only for the newest level asked for, on the schedule's thread, so a
    /// caller that touches a control must marshal.</summary>
    void IBlueLightTarget.Schedule(int level, Action<bool>? onApplied)
        => _tintApplies.Submit(() => device.DisplayTint is { } tint ? tint.Apply(level) : true, onApplied);

    // ---- the preference contracts (Application/Preferences.cs) ----
    //
    // The three shell preferences and the clamshell port, implemented EXPLICITLY so nothing is added to this
    // class's public surface — a caller reaches them by naming a use case (SetTurboToggles, SetLanguage,
    // SetClamshell, EvaluateClamshell), not the service. The clamshell is split across two contracts for the one
    // reason the lock rule states: the port call must not run under the graph lock, so <see cref="IClamshellTarget"/>
    // owns the port half and <see cref="IPreferenceStore"/> the file half, and the use case orders them.

    /// <summary>Remember whether the lid-closed keep-awake takeover is on. The hardware half is
    /// <see cref="IClamshellTarget.SetEnabled"/>, called by the use case BEFORE this is reached; a refused or
    /// absent port leaves this recorded, which is the order SetClamshell's own docstring states.</summary>
    void IPreferenceStore.Clamshell(bool on)
    {
        lock (_state) { Settings.Clamshell = on; Save(); }
    }

    /// <summary>Ask the port to enable or disable the clamshell takeover, OUTSIDE the graph lock (the lock must
    /// never span a port call, docs/domain-refactoring-plan.md §4). A machine with no port is a no-op.</summary>
    void IClamshellTarget.SetEnabled(bool on) => device.Clamshell?.SetEnabled(on);

    /// <summary>Recompute the clamshell decision from the live topology — a port call and nothing else. Called by
    /// the refresh pass off the UI thread.</summary>
    void IClamshellTarget.Evaluate() => device.Clamshell?.Evaluate();

    /// <summary>Remember the Turbo hotkey's behaviour, under the graph lock, persisted.</summary>
    void IPreferenceStore.TurboToggles(bool on)
    {
        lock (_state) { Settings.TurboToggles = on; Save(); }
    }

    /// <summary>Persist the chosen UI language. Activating it (and rebuilding the UI) is the app layer's job
    /// (see AppController) — this only records the preference.</summary>
    void IPreferenceStore.Language(AppLanguage language)
    {
        lock (_state) { Settings.Language = language; Save(); }
    }

    // There is deliberately no `PersistLighting` here any more. It existed because the lighting view-models
    // mutated the LIVE per-mode dictionary and then asked the service to write it out — two acts, the first of
    // which had no owner. Both now belong to the per-mode door (LaptopService.Lighting.cs, `LightZoneMode`),
    // which persists through the same `Save()` under the graph lock, and the UI holds values instead.

    /// <summary>Read a vendor-specific device flag from the neutral <see cref="Settings.DeviceSettings"/> bag
    /// (the key is owned by the backend, e.g. Infrastructure/Vendors/Acer). Missing key -> <paramref name="fallback"/>.
    ///
    /// This pair is for the backend-owned flags that are NOT declared settings — the lightbar's
    /// "follows performance profile" flag and the one-shot driver prompt. A DECLARED setting's value lands in
    /// the same bag under its own key (through the declared-setting edit use case, <c>ApplyDeclaredSetting</c>,
    /// which the assembler's rows call), and its own accessors are the declaration's
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
}
