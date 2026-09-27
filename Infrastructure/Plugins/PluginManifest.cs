namespace AcerHelper.Infrastructure.Plugins;

/// <summary>
/// The probe manifest a plugin returns from <c>ah_create</c> (docs/vendor-plugins.md §3.4) — the one place a
/// plugin declares WHAT THIS MACHINE HAS, so the host can build the <c>Device</c> slots
/// (<c>Infrastructure/Composition/Device.cs:42-53</c>) and the settings set before any further call.
///
/// THE SHAPE IS THE WIRE, so every type here is a plain DTO: no interfaces, no polymorphism, no <c>object</c>,
/// every list a concrete <see cref="List{T}"/>, and camelCase (via <c>PluginJsonContext</c>'s naming policy)
/// rather than the hand-written snake case the JSON in §3.4 is shown in. That is what makes it trivially
/// AOT-serializable through source generation (<c>PluginJsonContext</c>), which is the v1 payload decision
/// (§3.3). It is a faithful mirror of §3.4's sample: the host fills the slots it names and nothing else.
///
/// EVERY MEMBER IS OPTIONAL AND NULLABLE, and that is the partial-manifest rule rather than laziness: a plugin
/// under development returns only the capabilities it has finished, and §3.8.5's "additive only within a major"
/// means the other side must simply ignore what it does not know. A sub-object left null is "this machine does
/// not have that capability", which is exactly the <c>Device</c> slot's own <c>null</c>-means-absent rule.
/// </summary>
internal sealed class PluginManifest
{
    /// <summary>The plugin API major.minor this build speaks, e.g. "1.0", echoed from <c>ah_abi_version()</c>.
    /// The EXPORT is authoritative; this field is a cross-check, and a disagreement is a plugin bug (§3.4).</summary>
    public string? Abi { get; set; }

    /// <summary>The MODEL LINE id (<c>ah_plugin_id</c>), not the vendor — e.g. "acer-nitro" (§1.4).</summary>
    public string? PluginId { get; set; }

    /// <summary>The vendor token, e.g. "acer".</summary>
    public string? Vendor { get; set; }

    /// <summary>What the UI shows as the product name (<c>Device.VendorName</c>).</summary>
    public string? VendorName { get; set; }

    /// <summary>A localization KEY (never a sentence), or null — <c>Device.StatusMessage</c>. The UI resolves it
    /// with <c>Loc.T</c>, which is why <c>Localization/</c> stays host-only (§3.4).</summary>
    public string? StatusMessage { get; set; }

    /// <summary>The capabilities this machine has. Null means the plugin declares none.</summary>
    public CapabilitiesManifest? Capabilities { get; set; }
}

/// <summary>The <c>capabilities</c> object of the probe manifest (§3.4). Each member is the vendor-overridable
/// slot it fills; null means "this machine does not have it" and the host leaves the corresponding
/// <c>Device</c> slot as the generic backend built it (§1.1).</summary>
internal sealed class CapabilitiesManifest
{
    public PowerProfilesManifest? PowerProfiles { get; set; }
    public FanManifest? Fan { get; set; }

    /// <summary>PRESENCE IS THE OVERRIDE (§3.4): when this is non-null the vendor replaces the generic
    /// <c>ISensors.Read()</c>; when null the host keeps the generic read.</summary>
    public SensorsManifest? Sensors { get; set; }

    public KeyboardBrightnessManifest? KeyboardBrightness { get; set; }
    public RgbManifest? Rgb { get; set; }

    /// <summary>PRESENCE IS THE OVERRIDE: non-null means the plugin raises hotkeys through
    /// <c>ah_set_event_sink</c> (§3.5).</summary>
    public HotkeysManifest? Hotkeys { get; set; }

    public GpuMuxManifest? GpuMux { get; set; }
    public BatteryManifest? Battery { get; set; }

    /// <summary>A non-null value means the plugin OVERRIDES the generic curve optimizer; null (the Acer and
    /// Dell case, §3.4) means the host keeps the generic AMD-SMU implementation. A marker rather than a port:
    /// the generic axis is host-side (§1.1), so there is nothing for a vendor to declare behind it yet.</summary>
    public CurveOptimizerOverrideManifest? CurveOptimizerOverride { get; set; }

    /// <summary>The settings this machine declares, as declarations (key + shape + options), not as ports —
    /// the values are read/written through <c>ah_invoke(Capability.Settings, ...)</c>, which preserves the
    /// host settings model (§3.4). Absent/empty means the machine declares none.</summary>
    public List<DeclaredSettingManifest>? DeclaredSettings { get; set; }
}

/// <summary>The <c>powerProfiles</c> capability (§3.4), mirroring <c>IPowerProfiles</c> +
/// <c>IProfileAvailability</c> + <c>IProfileTraits</c> (Domain/Ports.cs:17-44, Plugins/Sdk/ProfileKind.cs:74-79).</summary>
internal sealed class PowerProfilesManifest
{
    /// <summary>The full profile set in display order (<c>IPowerProfiles.All</c>), immutable — no call needed.</summary>
    public List<ProfileManifest>? All { get; set; }

    /// <summary>The active profile's id, or null when unreadable.</summary>
    public string? CurrentId { get; set; }

    /// <summary>Ids selectable on AC (<c>IProfileAvailability.AvailableOn(true)</c>).</summary>
    public List<string>? AvailableOnAc { get; set; }

    /// <summary>Ids selectable on battery (<c>IProfileAvailability.AvailableOn(false)</c>).</summary>
    public List<string>? AvailableOnBattery { get; set; }

    /// <summary>The per-profile classification + colours (<c>IProfileTraits</c>). The <c>kind</c> is a string
    /// the HOST parses into its own <c>ProfileKind</c>, so the ABI carries no host enum (§6).</summary>
    public List<ProfileTraitsManifest>? Traits { get; set; }
}

/// <summary>One <c>PerformanceProfile(Id, DisplayName)</c> (Domain/Models.cs:22). The id is the vendor's opaque
/// stable key; the display name is a localization key or a literal the backend chose.</summary>
internal sealed class ProfileManifest
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>One profile's traits (§3.4): the coarse <c>kind</c> string and the two optional RGB colours the UI
/// paints with (accent colour + firmware flash colour), each <c>[r,g,b]</c>.</summary>
internal sealed class ProfileTraitsManifest
{
    public string? Id { get; set; }

    /// <summary>One of the <c>ProfileKind</c> names ("Quiet", "Eco", "Balanced", "Performance", "Turbo",
    /// "Other"), parsed by the host.</summary>
    public string? Kind { get; set; }

    /// <summary>RGB triple <c>[r,g,b]</c>, or null when the table classifies no colour.</summary>
    public int[]? Accent { get; set; }

    /// <summary>RGB triple <c>[r,g,b]</c>, or null.</summary>
    public int[]? Flash { get; set; }
}

/// <summary>The <c>fan</c> capability (§3.4), mirroring <c>IFanControl.Capability</c>
/// (<c>FanCapability(HasMax, HasCustom, HasGpuFan)</c>, Domain/Models.cs:64).</summary>
internal sealed class FanManifest
{
    public FanCapabilityManifest? Capability { get; set; }
}

/// <summary>Which fan controls the machine offers; Auto is always implied (§3.4).</summary>
internal sealed class FanCapabilityManifest
{
    public bool HasMax { get; set; }
    public bool HasCustom { get; set; }
    public bool HasGpuFan { get; set; }
}

/// <summary>The <c>sensors</c> marker (§3.4). It has no fields: the presence of the object is the whole
/// declaration — "this machine overrides the generic sensor read".</summary>
internal sealed class SensorsManifest
{
}

/// <summary>The <c>keyboardBrightness</c> capability (§3.4), mirroring <c>IKeyboardBrightness.MaxLevel</c>
/// (Domain/Ports.cs:106).</summary>
internal sealed class KeyboardBrightnessManifest
{
    /// <summary>Highest level the hardware accepts; 0 = off. Levels are 0..MaxLevel.</summary>
    public int MaxLevel { get; set; }
}

/// <summary>The <c>rgb</c> capability (§3.4), mirroring <c>IRgbDevice</c> (Domain/Rgb.cs:90).</summary>
internal sealed class RgbManifest
{
    /// <summary>The settings key under which a profile-indicator zone's "follows performance profile"
    /// preference is stored, or null (<c>IRgbDevice.ProfileFollowKey</c>).</summary>
    public string? ProfileFollowKey { get; set; }

    /// <summary>The independently-controllable zones, in display order.</summary>
    public List<RgbZoneManifest>? Zones { get; set; }
}

/// <summary>One RGB zone declaration (§3.4), mirroring <c>RgbZone</c> (Domain/Rgb.cs:32).</summary>
internal sealed class RgbZoneManifest
{
    public string? Name { get; set; }

    /// <summary>How many individually-addressable regions the zone is split into. The host's <c>RgbZone</c>
    /// floors this at 1, so a value of 0 on the wire reads as "cannot be split".</summary>
    public int SubZones { get; set; }

    /// <summary>True for a zone the firmware already paints per performance profile (the Acer lightbar).</summary>
    public bool CanFollowProfile { get; set; }

    public List<RgbEffectManifest>? Effects { get; set; }

    /// <summary>True when the zone exposes a brightness read (<c>RgbZone.ReadBrightness</c>).</summary>
    public bool ReadBrightness { get; set; }
}

/// <summary>One RGB effect/mode declaration (§3.4), mirroring <c>RgbModeInfo(Name, HasColor, HasSpeed,
/// Handle)</c> (Domain/Models.cs:91). <c>Handle</c> is the vendor's opaque encoding, carried as an integer —
/// the domain's <c>object</c> handle does not cross the ABI (§3.2, no managed type crosses).</summary>
internal sealed class RgbEffectManifest
{
    public string? Name { get; set; }
    public bool HasColor { get; set; }
    public bool HasSpeed { get; set; }

    /// <summary>The vendor's opaque effect handle (e.g. a mode byte).</summary>
    public int Handle { get; set; }
}

/// <summary>The <c>hotkeys</c> marker (§3.4). It has no fields: the presence of the object is the declaration,
/// and the events arrive through <c>ah_set_event_sink</c> (§3.5).</summary>
internal sealed class HotkeysManifest
{
}

/// <summary>The <c>gpuMux</c> capability (§3.4), mirroring <c>IGpuMux</c> (Domain/GpuMux.cs:51).</summary>
internal sealed class GpuMuxManifest
{
    /// <summary>False when the firmware exposes no switchable MUX (<c>IGpuMux.Supported</c>).</summary>
    public bool Supported { get; set; }

    /// <summary>The modes the machine offers, in display order (<c>IGpuMux.Modes</c>).</summary>
    public List<ChoiceOptionManifest>? Modes { get; set; }

    /// <summary>The mode id the firmware is in now, or null.</summary>
    public string? Current { get; set; }

    /// <summary>The mode id queued for the next restart, or null when nothing is queued.</summary>
    public string? Pending { get; set; }
}

/// <summary>A single labelled choice with a stable id (§3.4), mirroring <c>ChoiceOption(Id, DisplayName)</c>
/// (Domain/Models.cs:94). Used by both the MUX modes and a declared choice setting's options.</summary>
internal sealed class ChoiceOptionManifest
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>The <c>battery</c> capability (§3.4): four presence flags, one per vendor-overridable property of
/// <c>Battery</c> (Domain/Battery.cs:28-47). A true flag means the property exists and the host wires the
/// corresponding <c>BatteryToggle</c>/<c>BatteryChoice</c> over the Battery capability ops. The generic
/// telemetry is host-side and is therefore NOT listed here.</summary>
internal sealed class BatteryManifest
{
    public bool ChargeLimit { get; set; }
    public bool Calibration { get; set; }
    public bool ChargeMode { get; set; }
    public bool PowerSource { get; set; }
}

/// <summary>The <c>curveOptimizerOverride</c> marker (§3.4). It has no fields today: a non-null value is the
/// whole declaration ("this plugin overrides the generic curve optimizer"), and null — the Acer/Dell case — is
/// "keep the generic AMD-SMU implementation".</summary>
internal sealed class CurveOptimizerOverrideManifest
{
}

/// <summary>One declared setting (§3.4), mirroring the two shapes of <c>SettingDeclaration</c>
/// (Domain/DeclaredSetting.cs:26): <c>shape</c> is "flag" or "choice", and a choice carries its
/// <c>options</c>. <c>readbackVerifiesWrite</c> mirrors <c>SettingDeclaration.ReadbackVerifiesWrite</c>
/// (default true) — the flag sample sets it false; a partial manifest that omits it gets the same default the
/// declaration has.</summary>
internal sealed class DeclaredSettingManifest
{
    /// <summary>The backend's opaque key, matched with a UI row label and used as the settings-value key.</summary>
    public string? Key { get; set; }

    /// <summary>"flag" or "choice".</summary>
    public string? Shape { get; set; }

    /// <summary>The legal option set for a "choice"; null for a "flag".</summary>
    public List<ChoiceOptionManifest>? Options { get; set; }

    /// <summary>Whether a row verifies a write by reading the setting back afterwards.</summary>
    public bool ReadbackVerifiesWrite { get; set; } = true;
}
