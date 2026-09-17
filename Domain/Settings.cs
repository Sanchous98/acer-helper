using AcerHelper.Localization;

namespace AcerHelper.Domain;

/// <summary>User preferences, and the set of settings THIS machine declares plus the logic of switching them.
///
/// The two halves are different in kind, and holding both in one type is a recorded state rather than a
/// claim that they belong together: the persisted half is the shape of settings.json — written by an
/// <see cref="ISettingsStore"/> (Infrastructure), vendor-neutral so it survives a hardware/vendor change —
/// while <see cref="DeclaredSettings"/> is runtime state the backend supplies and that must never reach the
/// file. Splitting them into two types (the persisted container moving to Infrastructure, this declarative
/// half staying in Domain) is a separate, larger move: docs/domain-layering-map.md, move 4.</summary>
public sealed class Settings
{
    // UI language. Default follows the OS UI culture (see Loc); serialized as the enum's numeric value by the
    // source-generated JSON context, so it round-trips under Native AOT.
    public AppLanguage Language { get; set; } = AppLanguage.System;

    public bool TurboToggles { get; set; }
    public bool Clamshell    { get; set; }

    // Remembered performance mode per power source, re-applied when the source changes and at startup.
    // Each slot holds the base (non-Turbo) profile plus a Turbo flag, so Turbo is treated as a switch over
    // the base (never a standalone mode) — matching the "Turbo toggles" behaviour. BaseId also drives the
    // UI highlight while in Turbo, and survives a restart taken in Turbo.
    public ProfileMemory OnAc      { get; set; } = new();
    public ProfileMemory OnBattery { get; set; } = new();

    // Fan selection remembered PER performance mode (keyed by profile id; "default" when the device has no
    // profiles). Switching mode re-applies that mode's fan preset. A mode with no entry here = never set, so
    // its fans are left untouched (we can't read the hardware fan mode back to seed it).
    public Dictionary<string, FanPreset> FanPresets { get; set; } = new();

    public int Bluelight { get; set; }       // 0=off, 1=Low, 2=Medium, 3=High, 4=Long-use

    // Publish the keyboard's zones as a virtual HID LampArray so Windows Dynamic Lighting (and any
    // LampArray-aware app) can paint them — see Domain/LampArrayBridge.cs and docs/lamparray.md. Off by
    // default: it needs the separately-installed driver, and while a host holds the surface the app's own
    // per-mode lighting is not what's on the keyboard.
    public bool DynamicLighting { get; set; }

    // Applied lighting remembered PER performance mode (same key scheme as FanPresets), each holding the
    // per-RGB-zone state. Switching mode re-applies that mode's lighting. The app is the source of truth —
    // the controllers are largely write-only and can't report their state — so this is restored on startup
    // and on mode switch.
    public Dictionary<string, LightPreset> LightPresets { get; set; } = new();

    // GPU clock offsets remembered PER performance mode (same key scheme as FanPresets/LightPresets). Switching
    // mode re-applies that mode's offsets. Unlike fans, an unconfigured mode is stock (0/0), NOT "leave
    // untouched": the GPU driver zeroes clock offsets on every boot/driver-reload, so the app is the source of
    // truth and re-applies on startup, on resume, and on each mode switch — a mode with no entry is definitely
    // stock, so switching to it must clear any offset the previous mode had applied.
    public Dictionary<string, GpuOcPreset> GpuOcPresets { get; set; } = new();

    // CPU Curve-Optimizer offset remembered PER performance mode (same key scheme as GpuOcPresets). Follows the
    // GPU-offset contract, NOT the fan one: an unconfigured mode is stock (0), not "leave untouched" — the offset
    // lives in volatile SMU state that a power cycle clears, so the app is the source of truth and re-applies on
    // startup, on resume, and on each mode switch. A mode with no entry is therefore definitely stock, and
    // switching to it must clear whatever undervolt the previous mode had applied — a stale negative offset
    // carried into a profile the user never configured is exactly how an unexplained instability happens.
    public Dictionary<string, CoPreset> CoPresets { get; set; } = new();

    // CPU power mode (Windows power-mode overlay GUID) remembered PER performance mode (same key scheme).
    // Switching mode re-applies that mode's overlay IF one is stored; a mode with no entry is "leave untouched"
    // (like fans) — we don't force an OS power mode on profiles the user hasn't configured. Value = overlay
    // GUID string.
    public Dictionary<string, string> CpuPowerModes { get; set; } = new();

    // Vendor-specific device state, keyed by an opaque string the owning backend defines (e.g. Acer's lightbar
    // "follows performance profile", or any setting a backend DECLARES — see SettingDeclaration below). Kept as a
    // neutral bag so Settings stays vendor-agnostic — on different hardware the unused keys just sit inert. The
    // key + its meaning live in the backend (Infrastructure/Vendors/*); access via LaptopService.GetDeviceFlag /
    // SetDeviceFlag, and via LaptopService.ApplySetting for a declared setting.
    public Dictionary<string, string> DeviceSettings { get; set; } = new();

    // ---- the options this machine declares (this type's runtime half) ----------------------------------------
    //
    // A backend declares which settings THIS machine has, each under its own opaque key; this type HOLDS that set
    // and carries the logic of switching it — applying one goes through the option's own contract, and a refusal
    // comes out of that call as an exception. The FORM of a declaration and what applying one is are stated once,
    // with the contract types at the bottom of this file; nothing here interprets a key or invents a declaration.
    //
    // THE SET IS SUPPLIED FROM OUTSIDE (Install). What a machine has is what its probe found, so this type builds
    // nothing itself; the backend fills the list while it is constructed and the composition path hands it over.
    //
    // NOT PERSISTED, and the accessor below is `internal` rather than public because of it: the guard
    // EveryDeclaredPropertyStillReachesTheDisk (tests) holds every PUBLIC property of this type to reaching
    // settings.json, and a declaration must not reach it — it says what the MACHINE has, not what the user chose.
    // A public property would either put a list of live transports in the user's file or force that guard to grow
    // an exemption, and noticing exactly that is what the guard is for.

    private IReadOnlyList<SettingDeclaration> _declaredSettings = [];

    /// <summary>The settings this machine's backend declared, in the order their rows should read. Empty on a
    /// machine that declares none.</summary>
    internal IReadOnlyList<SettingDeclaration> DeclaredSettings => _declaredSettings;

    /// <summary>Hand over the set of options this machine declares. Called once, from the composition path
    /// (<c>LaptopService</c>'s constructor), with the list the backend filled while probing.
    ///
    /// The list is held BY REFERENCE rather than copied, and the difference is deliberate: the shipped backends
    /// have finished declaring before the app service exists, so there is nothing a copy would protect, while a
    /// test's fake backend declares AFTER the service exists — the same live list is how a test says which row it
    /// means, and copying would silently make a later declaration invisible to the model.</summary>
    internal void Install(IReadOnlyList<SettingDeclaration> options) => _declaredSettings = options;

    /// <summary>Switch one declared option: hand the value to the option's own contract, which is where the write
    /// happens and where a refusal comes from — a <see cref="SettingNotAppliedException"/> carrying the key and
    /// the transport's own reason.
    ///
    /// THE CALLER MUST NOT HOLD THE GRAPH LOCK ACROSS THIS CALL. It is an EC/WMI write and the lock must never
    /// span one (docs/domain-refactoring-plan.md §4), so the recording that follows a write that took is a
    /// separate call — <see cref="Remember"/> — which the caller makes under the lock instead. This type keeps no
    /// lock of its own: a lock in Domain/ is the same thing §4 forbids.</summary>
    internal void Apply(SettingDeclaration option, string value) => option.Apply(value);

    /// <summary>Remember a value the hardware took, under the option's OWN key — the bag is keyed by the
    /// backend's name for the setting, which is the only name either layer has for it, and a choice's value is an
    /// option id rather than a bool, which the bag carries as a string.
    ///
    /// That a REFUSED switch records nothing is the ORDER, not a check: a throw out of <see cref="Apply"/> leaves
    /// this call unreached (pinned by <c>DeclaredSettingTests.ARefusedSetting_RecordsNothing_AndSavesNothing</c>).
    /// The caller holds the graph lock: <see cref="DeviceSettings"/> is a shared collection.</summary>
    internal void Remember(SettingDeclaration option, string value) => DeviceSettings[option.Key] = value;
}

// ---- the declared-settings contract ------------------------------------------------------------------------
//
// The domain knows no hardware-specific setting names — not "LCD overdrive", not "acer.lightbarFollowsProfile".
// A backend declares which settings THIS machine has, each under its own opaque key, and this file declares the
// two halves of what that means: the FORM of a declaration (a flag or a choice, and for a choice its legal
// values) and what APPLYING one is (a write, whose refusal is an exception rather than a returned message).
// Switching one is <see cref="Settings.Apply"/> plus <see cref="Settings.Remember"/>: the apply goes through the
// declaration below, the remembering is the write into the bag above, and the two are separate calls only because
// the graph lock must not span the write.
//
// Nothing here interprets a key or names a setting. The row's label, its localization and the sentence the user
// reads on a failure all belong to the UI, which is the only layer that knows what to call a `lcd_override`.

/// <summary>One setting a machine's backend declares, so the row that drives it can be built from the
/// declaration instead of from a hard-coded port slot and a hard-coded name.
///
/// Subtypes ARE the fork a row needs: <see cref="FlagSetting"/> carries a bool and <see cref="ChoiceSetting"/> a
/// pick-one-of-N set with the ids the backend accepts. A <c>Dictionary&lt;string,string&gt;</c> cannot express
/// that fork, which is why the bag holds this setting's VALUE while the declaration holds its shape.</summary>
public abstract record SettingDeclaration
{
    /// <summary>The backend's own name for this setting, OPAQUE to the domain: nothing above reads it except to
    /// match it with a row label and to record the value. It is also the key this setting's value is recorded
    /// under in <see cref="Settings.DeviceSettings"/>.</summary>
    public required string Key { get; init; }

    /// <summary>Whether a row verifies a write by reading the setting back afterwards. TRUE where the transport
    /// can accept a write and silently not take it — the row then corrects its own switch instead of leaving it
    /// lying. FALSE where the write itself reports the outcome (Acer's LCD overdrive returns a status byte),
    /// because there the readback would be a second hardware transaction the user hears as a second click.</summary>
    public bool ReadbackVerifiesWrite { get; init; } = true;

    /// <summary>Apply <paramref name="value"/> to the hardware, or throw. A refusal is
    /// <see cref="SettingNotAppliedException"/> — information about what happened, and nothing else: the
    /// sentence the user reads is composed by the UI, which is where setting names live.</summary>
    public void Apply(string value)
    {
        var (ok, error) = Write(value);
        if (!ok) throw new SettingNotAppliedException(Key, error);
    }

    /// <summary>The hardware write behind <see cref="Apply"/>: whether it took, and the transport's own reason
    /// when it did not.</summary>
    protected abstract (bool ok, string? error) Write(string value);
}

/// <summary>An on/off setting. Its values are the strings <see cref="Settings.DeviceSettings"/> already uses for
/// a flag — "1" and "0" — so a declared flag needs no second encoding.</summary>
public sealed record FlagSetting : SettingDeclaration
{
    /// <summary>The transport this setting is read and written through.</summary>
    public required IFlagPort Port { get; init; }

    /// <summary>What the hardware holds now.</summary>
    public bool Read() => Port.Get();

    /// <summary>The stored form of a flag, for callers that hold a bool.</summary>
    public static string Value(bool on) => on ? "1" : "0";

    /// <summary>A port that THROWS is a failed write, not a crash, and its reason still has to reach the user:
    /// every transport in the tree that throws reports its cause through <see cref="IFlagPort.LastError"/>, and
    /// that is what a rejected write shows. This is the rule <c>LaptopService.Attempt</c> stated for the tuple
    /// channel, kept on this side of the exception so the same writes are reported the same way.</summary>
    protected override (bool ok, string? error) Write(string value)
    {
        bool ok;
        try { ok = Port.Set(value == "1"); } catch { ok = false; }
        return (ok, ok ? null : Port.LastError);
    }
}

/// <summary>A pick-one-of-N setting. Its values are option ids verbatim: the ids are the backend's own stable
/// keys, and <see cref="Options"/> is the display set in the order the dropdown shows it — which is what makes
/// the id-to-index mapping a row needs expressible here (<see cref="IndexOf"/>) and not in the bag.</summary>
public sealed record ChoiceSetting : SettingDeclaration
{
    /// <summary>The transport this setting is read and written through.</summary>
    public required IChoicePort Port { get; init; }

    /// <summary>The legal values, in display order.</summary>
    public IReadOnlyList<ChoiceOption> Options => Port.Options;

    /// <summary>The id the hardware is in now, or null when it would not answer.</summary>
    public string? Read() => Port.Get();

    /// <summary>The dropdown index of <paramref name="id"/>, or 0 when the hardware reports an id this build
    /// does not offer — the same answer the port being unreadable gives, and the same one the row showed before
    /// the mapping lived here.</summary>
    public int IndexOf(string? id)
    {
        for (var i = 0; i < Options.Count; i++)
            if (Options[i].Id == id)
                return i;
        return 0;
    }

    /// <summary>A port that THROWS is a failed write, not a crash — see <see cref="FlagSetting.Write"/>.</summary>
    protected override (bool ok, string? error) Write(string value)
    {
        bool ok;
        try { ok = Port.Set(value); } catch { ok = false; }
        return (ok, ok ? null : Port.LastError);
    }
}

/// <summary>Why a declared setting could not be applied. Carries INFORMATION — which setting, and the
/// transport's own words when it gave any — and deliberately not a sentence for the user: the domain knows no
/// setting names, so the message the user reads is composed by the UI from the row's own label (see
/// <c>OptionsAssembler.RunSet</c>). <see cref="Exception.Message"/> exists for a log or a stack trace and is not
/// what the UI shows.</summary>
public sealed class SettingNotAppliedException(string key, string? reason)
    : Exception($"{key} was not applied" + (reason != null ? $": {reason}" : ""))
{
    /// <summary>The declared setting's key — its backend's own name, opaque here.</summary>
    public string Key { get; } = key;

    /// <summary>The transport's own words, when it gave any.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>One light's persisted state. Colours are packed 0xRRGGBB. <see cref="Configured"/> is
/// false until the user changes something, so a fresh install doesn't clobber the firmware default
/// on first launch.</summary>
public sealed class LightSettings
{
    public bool Configured { get; set; }
    public int EffectIndex { get; set; }
    public int Brightness  { get; set; } = 100;
    public int Speed       { get; set; } = 5;
    public int Direction   { get; set; } = 1;   // directional effects (Wave): 1 or 2
    public int Color       { get; set; } = 0xFF0000;
    public int[] ZoneColors { get; set; } = [];
}

/// <summary>Remembered performance mode for one power source: the base (non-Turbo) profile id, plus
/// whether Turbo was layered on top. Empty <see cref="BaseId"/> = nothing remembered yet.</summary>
public sealed class ProfileMemory
{
    public string BaseId { get; set; } = "";
    public bool Turbo { get; set; }
}

/// <summary>A performance mode's remembered fan selection: mode (1=Auto/2=Max/3=Custom) + custom speeds.
/// When <see cref="Curve"/> is on, the app ignores the fixed speeds and instead drives Custom speeds from a
/// duty% curve at fixed temperature anchors (Acer has no native fan curves — this emulates them via the
/// sensor loop; the anchors and the default ramp live in <see cref="Fan"/>). <see cref="CpuCurve"/>/<see cref="GpuCurve"/> hold one duty%
/// per anchor; empty = use the built-in default ramp. The preset is the STORED shape and holds BOTH fans;
/// <see cref="Fan"/> is the model that reads one half of it.</summary>
public sealed class FanPreset
{
    public int Mode { get; set; } = 1;
    public int Cpu  { get; set; } = 70;
    public int Gpu  { get; set; } = 70;

    // Per-fan curve (only meaningful in Custom mode): when UseCurve is on for a fan, the app drives that
    // fan's Custom speed from its curve (duty% per temperature anchor) instead of the fixed speed above.
    public bool  CpuUseCurve { get; set; }
    public bool  GpuUseCurve { get; set; }
    public int[] CpuCurve    { get; set; } = [];
    public int[] GpuCurve    { get; set; } = [];

    /// <summary>A copy that shares NOTHING mutable with the stored instance — the two arrays are duplicated,
    /// not aliased. This is what a reader gets instead of the stored object, so a caller that keeps or edits
    /// what it was handed cannot rewrite the user's settings without going through a Set method (and thus
    /// without the lock that guards the graph). The depth is the point: copying the fields but keeping the
    /// arrays would leave the widest part of the preset still shared.</summary>
    public FanPreset Snapshot() => new()
    {
        Mode = Mode,
        Cpu = Cpu,
        Gpu = Gpu,
        CpuUseCurve = CpuUseCurve,
        GpuUseCurve = GpuUseCurve,
        CpuCurve = [.. CpuCurve],
        GpuCurve = [.. GpuCurve],
    };
}

/// <summary>A performance mode's remembered lighting: per-RGB-zone state, keyed by zone name.</summary>
public sealed class LightPreset
{
    public Dictionary<string, LightSettings> Zones { get; set; } = new();
}

/// <summary>A performance mode's remembered GPU clock offsets, in MHz (0 = stock). Applied on the mode switch
/// and re-applied at startup/resume (the driver zeroes offsets across a reboot/driver reload).</summary>
public sealed class GpuOcPreset
{
    public int Core { get; set; }
    public int Mem  { get; set; }

    /// <summary>A copy that shares nothing with the stored instance. Both members are values, so there is no
    /// depth to get wrong here — the method exists so every axis reads the same way at the call site, and so a
    /// member added later is copied by default rather than aliased by accident.</summary>
    public GpuOcPreset Snapshot() => new() { Core = Core, Mem = Mem };
}

/// <summary>A performance mode's remembered CPU Curve-Optimizer offset, in AVFS counts (0 = stock, negative =
/// undervolt). Applied on the mode switch and re-applied at startup/resume — the offset is SMU-resident and a
/// power cycle restores stock.</summary>
public sealed class CoPreset
{
    /// <summary>Offset applied to every core, used on a CPU that exposes no per-group control.</summary>
    public int AllCore { get; set; }

    /// <summary>Per-voltage-domain offsets keyed by the domain's hardware identity (see
    /// <see cref="VoltageDomain.Key"/>) — a domain name rather than a list position, so a preset survives a
    /// change in how domains are ordered or labelled. Preferred over <see cref="AllCore"/> wherever the CPU exposes
    /// separate domains, because a hybrid part's clusters are separate rails sitting at different voltages, and one
    /// number for both is pinned by whichever gives out first. A missing key means stock (0) for that domain.</summary>
    public Dictionary<string, int> Domains { get; set; } = new();

    /// <summary>A copy that shares nothing mutable with the stored instance: <see cref="Domains"/> is a new
    /// dictionary holding the same entries. A caller that keeps the offset for a rail and edits it must not be
    /// editing what the next mode switch will read back.</summary>
    public CoPreset Snapshot() => new() { AllCore = AllCore, Domains = new(Domains) };
}

/// <summary>Port for persisting <see cref="Settings"/>. Implemented in Infrastructure.</summary>
public interface ISettingsStore
{
    Settings Load();
    void Save(Settings settings);
}
