using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

/// <summary>User preferences, and the set of settings THIS machine declares plus the logic of switching them.
///
/// The two halves are different in kind, and holding both in one type is a recorded state rather than a
/// claim that they belong together: the persisted half is the shape of settings.json — written by an
/// <see cref="ISettingsStore"/> beside this file, vendor-neutral so it survives a hardware/vendor change —
/// while <see cref="DeclaredSettings"/> is runtime state the backend supplies and that must never reach the
/// file.
///
/// THE PERSISTED HALF IS INFRASTRUCTURE BY THE OWNER'S RULING — it is the form the file has, and it declares
/// the options «конкретно этого ноутбука» — so this type sits beside the store and the device factory, and the
/// CONTRACT half of the declared-settings model (<see cref="SettingDeclaration"/> and its two shapes) stayed in
/// <c>Domain/DeclaredSetting.cs</c>, where the owner left it. The two are still one object because the model is
/// what switches an option and what remembers it: the apply goes through the declaration, the remember writes
/// into the bag below, and both need the same instance. Nothing in Domain names this type any more, which is
/// what the move bought — and, with it, Domain importing nothing above itself.</summary>
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
    // LampArray-aware app) can paint them — see Infrastructure/Lighting/LampArrayBridge.cs and docs/lamparray.md.
    // Off by default: it needs the separately-installed driver, and while a host holds the surface the app's own
    // per-mode lighting is not what's on the keyboard.
    public bool DynamicLighting { get; set; }

    // Applied lighting remembered PER performance mode (same key scheme as FanPresets), each holding the
    // per-RGB-zone state. Switching mode re-applies that mode's lighting. The app is the source of truth —
    // the controllers are largely write-only and can't report their state — so this is restored on startup
    // and on mode switch.
    public Dictionary<string, LightPreset> LightPresets { get; set; } = new();

    // The user's consent to let THIS app use the machine's discrete GPU, which a third-party daemon (cardwire)
    // is hiding from programs that have not been allowed to use it — see docs/cardwire-gpu-access.md. Off by
    // default, and asked for at runtime only: consenting to it writes no file and changes nothing about the
    // machine, so unlike every other member here this one is not a piece of device state being restored — it is
    // a permission the app re-asks for on each start (a grant belongs to one process and dies with it, and
    // cardwire has no way to revoke it early). The row that sets it exists only where there is something to ask
    // for; the value itself is a plain preference and is kept everywhere.
    public bool CardwireGpuAccess { get; set; }

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
    // and carries the logic of switching it — applying one goes through the option's own contract, a refusal comes
    // out of that call as an exception, and an option this machine does not declare is refused by THIS type before
    // any transport is touched. The FORM of a declaration and what applying one is are stated once, with the
    // contract types live in Domain/DeclaredSetting.cs; nothing here interprets a key or invents a declaration.
    //
    // THE SET ARRIVES THROUGH THE CONSTRUCTOR AND IS FIXED THERE. What a machine has is what its probe found, so
    // this type builds nothing itself: the composition path constructs the model with the set the backend filled
    // while probing, and COPIES it, so nothing that happens after construction can add an option to a machine
    // that did not have it. The order that makes the copy work is the order production already had — a vendor
    // backend declares inside InitVendor, before the service (and so before the model) exists — which is why the
    // tests' fakes had to be re-pointed to declare before the fixture builds the service.
    //
    // NOT PERSISTED, and the accessor below is `internal` rather than public because of it: the guard
    // EveryDeclaredPropertyStillReachesTheDisk (tests) holds every PUBLIC property of this type to reaching
    // settings.json, and a declaration must not reach it — it says what the MACHINE has, not what the user chose.
    // A public property would either put a list of live transports in the user's file or force that guard to grow
    // an exemption, and noticing exactly that is what the guard is for.

    private readonly IReadOnlyList<SettingDeclaration> _declaredSettings;

    /// <summary>The settings this machine's backend declared, in the order their rows should read. Empty on a
    /// machine that declares none.</summary>
    internal IReadOnlyList<SettingDeclaration> DeclaredSettings => _declaredSettings;

    /// <summary>The shape the FILE has: a Settings with no declarations at all. It exists because settings.json
    /// carries only what the user CHOSE and never what the machine HAS, so the store's reader has to be able to
    /// build one before the backend's set is in hand. It is not a session's model — that is the constructor
    /// below, which is given both halves — and a model built here declares nothing, so it switches nothing.</summary>
    public Settings() : this([]) { }

    /// <summary>The session's model: the options this machine declares, plus, when a previous run left a file,
    /// the values it persisted.
    ///
    /// The set is COPIED rather than held by reference, and the difference is the rule this constructor exists
    /// for: a declaration made after it returns must not show up on a machine that was probed without it. Holding
    /// the live list would let a backend keep adding options to a model that had already stated what this machine
    /// has — which is exactly what the set means.
    ///
    /// <paramref name="persisted"/> is taken over BY REFERENCE, member by member: it is the reader's throwaway
    /// product, so the collections it holds are this model's from here on and nothing else reads or writes
    /// them.</summary>
    internal Settings(IReadOnlyList<SettingDeclaration> declaredSettings, Settings? persisted = null)
    {
        _declaredSettings = [.. declaredSettings];
        if (persisted is null) return;

        // One line per persisted member. A member added to this type without a line here would come back as its
        // default after a save/load, which is a FEATURE quietly forgetting — EverySettingSurvivesTheRoundTrip
        // compares the whole instance for exactly that, so the omission reddens rather than ships.
        Language        = persisted.Language;
        TurboToggles    = persisted.TurboToggles;
        Clamshell       = persisted.Clamshell;
        OnAc            = persisted.OnAc;
        OnBattery       = persisted.OnBattery;
        FanPresets      = persisted.FanPresets;
        Bluelight       = persisted.Bluelight;
        DynamicLighting = persisted.DynamicLighting;
        CardwireGpuAccess = persisted.CardwireGpuAccess;
        LightPresets    = persisted.LightPresets;
        GpuOcPresets    = persisted.GpuOcPresets;
        CoPresets       = persisted.CoPresets;
        CpuPowerModes   = persisted.CpuPowerModes;
        DeviceSettings  = persisted.DeviceSettings;
    }

    /// <summary>Switch one declared option: hand the value to the option's own contract, which is where the write
    /// happens and where a refusal comes from — a <see cref="SettingNotAppliedException"/> carrying the key and
    /// the transport's own reason.
    ///
    /// AN OPTION THIS MACHINE DOES NOT DECLARE IS AN ERROR, not a silent no-op. The set this model was
    /// constructed with is the whole of what it can switch, so an option outside it is refused the same way a
    /// refused write is — the same exception, carrying this layer's reason instead of a transport's — and no
    /// transport is touched at all. It is unreachable through the UI by construction (every row is built from the
    /// declared set, OptionsAssembler), which is why it is pinned at the model, where it CAN fire.
    ///
    /// THE CALLER MUST NOT HOLD THE GRAPH LOCK ACROSS THIS CALL. It is an EC/WMI write and the lock must never
    /// span one (docs/domain-refactoring-plan.md §4), so the recording that follows a write that took is a
    /// separate call — <see cref="Remember"/> — which the caller makes under the lock instead. This type keeps no
    /// lock of its own: a lock in Domain/ is the same thing §4 forbids.</summary>
    internal void Apply(SettingDeclaration option, string value)
    {
        if (!Declares(option.Key)) throw new SettingNotAppliedException(option.Key, NotDeclaredReason);
        option.Apply(value);
    }

    /// <summary>Whether this machine declared <paramref name="key"/>. Matched by KEY rather than by the
    /// declaration INSTANCE, because the key is the only name either layer has for a setting and it is the key
    /// <see cref="Remember"/> records the value under — so a declaration rebuilt over the same key is still a
    /// setting this machine has, and only a key outside the set is refused.</summary>
    private bool Declares(string key)
    {
        foreach (var declared in _declaredSettings)
            if (declared.Key == key)
                return true;
        return false;
    }

    /// <summary>Why an option outside the declared set was refused — this layer's own words, sitting where a
    /// transport's words sit on a refused write. The UI composes the sentence from the row's label and this
    /// reason (<c>OptionsAssembler.RunSet</c>), so it has to read as the clause after "&lt;label&gt; failed: ".</summary>
    private const string NotDeclaredReason = "this machine does not declare it";

    /// <summary>Remember a value the hardware took, under the option's OWN key — the bag is keyed by the
    /// backend's name for the setting, which is the only name either layer has for it, and a choice's value is an
    /// option id rather than a bool, which the bag carries as a string.
    ///
    /// That a REFUSED switch records nothing is the ORDER, not a check: a throw out of <see cref="Apply"/> leaves
    /// this call unreached (pinned by <c>DeclaredSettingTests.ARefusedSetting_RecordsNothing_AndSavesNothing</c>).
    /// The caller holds the graph lock: <see cref="DeviceSettings"/> is a shared collection.</summary>
    internal void Remember(SettingDeclaration option, string value) => DeviceSettings[option.Key] = value;
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
/// per anchor. The preset is the STORED shape and holds BOTH fans; <see cref="Fan"/> is the model that reads one
/// half of it.
///
/// AN EMPTY CURVE USED TO MEAN "the built-in default ramp" AND NO LONGER DOES. That spelling was a second way to
/// say what the five default duties say, and the model cannot hold it: <c>FanSettings</c> requires a curve of
/// exactly one duty% per anchor, so the default below is the ramp ITSELF rather than an empty array, and the
/// load-time sanitiser rewrites an empty or otherwise malformed curve in an existing file the same way
/// (Infrastructure/Composition/JsonSettingsStore.cs). The user's curve is then the ramp, spelled out —
/// indistinguishable in behaviour and readable in the file.
///
/// <see cref="Mode"/> STAYS AN <c>int</c> and is read through <c>FanModes.FromStored</c> rather than cast, so a
/// value naming no <see cref="FanMode"/> cannot become one (Domain/Models.cs).</summary>
public sealed class FanPreset
{
    public int Mode { get; set; } = 1;
    public int Cpu  { get; set; } = 70;
    public int Gpu  { get; set; } = 70;

    // Per-fan curve (only meaningful in Custom mode): when UseCurve is on for a fan, the app drives that
    // fan's Custom speed from its curve (duty% per temperature anchor) instead of the fixed speed above.
    // The default is the ramp ITSELF rather than an empty array, so a preset that has never been configured is
    // already a curve the model admits — a fresh preset is written into the dictionary by the first edit of a
    // mode and read straight back by it, with no store round-trip to sanitise it in between.
    public bool  CpuUseCurve { get; set; }
    public bool  GpuUseCurve { get; set; }
    public int[] CpuCurve    { get; set; } = Fan.DefaultDuties();
    public int[] GpuCurve    { get; set; } = Fan.DefaultDuties();

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

/// <summary>Port for persisting <see cref="Settings"/>. Implemented in Infrastructure.
///
/// <see cref="Load"/> builds the SESSION'S MODEL, so the set of options this machine declares is a parameter of
/// it: the model takes that set at construction (<see cref="Settings"/>'s constructor) and fixes it there, and
/// the store is the only thing that constructs one. The store is not where the set lives — it is handed over and
/// passes it straight through on its way to the constructor.</summary>
public interface ISettingsStore
{
    Settings Load(IReadOnlyList<SettingDeclaration> declaredSettings);
    void Save(Settings settings);
}
