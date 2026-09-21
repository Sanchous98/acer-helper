using System.Threading;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    public SensorSnapshot ReadSensors() => device.Sensors?.Read() ?? new SensorSnapshot();

    public BatteryInfoSnapshot ReadBatteryInfo() => device.Battery.Read();

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

    /// <summary>Apply one of the settings this machine declares, then record the value under the setting's own
    /// key. Both halves of the switch belong to the model — <c>Settings.Apply</c> hands the value to the option's
    /// own contract (a refusal is a <see cref="SettingNotAppliedException"/>) and <c>Settings.Remember</c> records
    /// what took; what stays here is what the model cannot own: the graph lock and the save.
    ///
    /// THE ORDER IS THE POINT, and it is stated in Application now — <see cref="ApplyDeclaredSetting"/> decides that
    /// the hardware write comes first and stands alone, so a refusal records nothing; the three members below are
    /// only the doing.</summary>
    public void ApplySetting(SettingDeclaration setting, string value)
        => ApplyDeclaredSetting.Run(setting, value, this);

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

    /// <summary>
    /// Set the blue-light level: RECORD it here, then hand the hardware write to the schedule above.
    ///
    /// THE TWO HALVES ARE IN THAT ORDER, and it is the one that keeps the app honest about a level it did not get
    /// to finish: the recorded value is what <see cref="ApplyStartupState"/> re-applies on the next run, so a
    /// level the user picked is remembered even if the process dies between the click and the write landing. The
    /// hardware write is deliberately NOT under <c>_state</c> (design doc D17, and the rule every port call in this
    /// class follows) — it is now not on this thread at all.
    ///
    /// <paramref name="onApplied"/> is answered ONLY for the newest level asked for; a superseded one reports
    /// nothing, because its outcome is about a level the user has already left. It is called on the schedule's
    /// thread, so a caller that touches a control must marshal (the Options row does: <c>OptionsAssembler.Fail</c>
    /// posts). No port at all answers success — there is nothing to report, and the row is not built without one —
    /// and a throwing port is a failed one, as for every other port in this class (<see cref="Attempt(Func{bool}, Func{string?})"/>).
    /// </summary>
    public void SetBlueLight(int level, Action<bool>? onApplied = null)
    {
        lock (_state) { Settings.Bluelight = level; Save(); }
        _tintApplies.Submit(() => device.DisplayTint is { } tint ? tint.Apply(level) : true, onApplied);
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

    // There is deliberately no `PersistLighting` here any more. It existed because the lighting view-models
    // mutated the LIVE per-mode dictionary and then asked the service to write it out — two acts, the first of
    // which had no owner. Both now belong to the per-mode door (LaptopService.Lighting.cs, `LightZoneMode`),
    // which persists through the same `Save()` under the graph lock, and the UI holds values instead.

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
