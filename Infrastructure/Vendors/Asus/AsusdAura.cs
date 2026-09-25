using System.Text.RegularExpressions;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The asusd Aura RGB interface, mapped onto the app's existing RGB framework (Domain/Rgb.cs +
// Infrastructure/Lighting/RgbController.cs) — no new domain model: one RgbZone per Aura device, its effects
// taken from asusd's supported_basic_modes, its brightness from asusd's LedBrightness levels, and its apply path
// writing the AuraEffect struct.
//
// PER-DEVICE PATHS, and that is why the device list is discovered rather than assumed. asusd's aura manager
// registers one object per physical Aura device, all under the Aura root BUT with device-specific final
// segments (the keyboard is /xyz/ljones/Aura/<idProduct>_<devnum>_<devpath>, or /xyz/ljones/Aura/tuf for the
// sysfs-backlight TUF path). So the host enumerates `busctl tree`, keeps every path under the root, and attaches
// only the ones whose introspection actually carries xyz.ljones.Aura.
//
// WIRE SHAPES, all from asusctl's rog-aura crate:
//   * brightness         — LedBrightness as a u32 (0 Off, 1 Low, 2 Med, 3 High)
//   * led_mode           — AuraModeNum as a u32
//   * led_mode_data      — the AuraEffect struct, signature (uu(yyy)(yyy)ss):
//                          mode:u, zone:u, colour1:(y,y,y), colour2:(y,y,y), speed:s, direction:s
//                          (Speed/Direction are declared #[zvariant(signature = "s")], so their wire values are
//                          the variant words "Low"/"Med"/"High" and "Right"/"Left"/"Up"/"Down")
//   * supported_brightness / supported_basic_modes / supported_basic_zones — au
//
// WRITING led_mode_data is the single apply call asusd offers: its handler validates the mode/zone against the
// supported sets, applies the effect and then re-applies the current brightness. The brightness property is set
// separately.
//
// RUNTIME MARKED UNVERIFIED: no ASUS Aura hardware was available to this change. The mapping and the exact
// argument lists are unit-tested against recorded busctl output; the on-device effect of a write has not been
// measured. The mode capability flags (has-colour/has-speed/has-direction) are a faithful reading of rog-aura's
// packet builder, not a measurement.

/// <summary>The Aura coordinates, its per-device path discovery, and the effect-struct wire form.</summary>
internal static partial class AsusdAura
{
    internal const string Interface = "xyz.ljones.Aura";
    internal const string Root = "/xyz/ljones/Aura";

    internal const string BrightnessProperty = "brightness";
    internal const string LedModeProperty = "led_mode";
    internal const string LedModeDataProperty = "led_mode_data";
    internal const string SupportedBrightnessProperty = "supported_brightness";
    internal const string SupportedBasicModesProperty = "supported_basic_modes";
    internal const string SupportedBasicZonesProperty = "supported_basic_zones";
    internal const string DeviceTypeProperty = "device_type";

    /// <summary>The AuraEffect struct signature, exactly as the Rust type derives it.</summary>
    internal const string EffectSignature = "(uu(yyy)(yyy)ss)";

    /// <summary>The AuraZone value meaning "the whole keyboard / no zone".</summary>
    internal const int WholeZone = 0;

    /// <summary>Every Aura object path in a <c>busctl tree</c> reply, in tree order, deduplicated. Only the
    /// per-device children are taken: the bare root is not a device.</summary>
    internal static IReadOnlyList<string> DevicePaths(string treeOutput)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (Match m in AuraPath().Matches(treeOutput))
            if (seen.Add(m.Value)) paths.Add(m.Value);
        return paths;
    }

    [GeneratedRegex(@"/xyz/ljones/Aura/[^\s]+")]
    private static partial Regex AuraPath();
}

/// <summary>The Aura effect vocabulary: asusctl's AuraModeNum, its display word, and which of the three optional
/// effect controls it honours. The flags come from rog-aura's packet builder (colour1 is used by every mode
/// except the two Rainbow ones; Speed is meaningful for anything animated; Direction only for the wave).</summary>
internal static class AsusAuraModes
{
    internal sealed record Entry(int Code, string Name, bool HasColor, bool HasSpeed, bool HasDirection);

    private static readonly Entry[] Table =
    [
        new(0,  "Static",       HasColor: true,  HasSpeed: false, HasDirection: false),
        new(1,  "Breathe",      HasColor: true,  HasSpeed: true,  HasDirection: false),
        new(2,  "RainbowCycle", HasColor: false, HasSpeed: true,  HasDirection: false),
        new(3,  "RainbowWave",  HasColor: false, HasSpeed: true,  HasDirection: true),
        new(4,  "Stars",        HasColor: true,  HasSpeed: true,  HasDirection: false),
        new(5,  "Rain",         HasColor: true,  HasSpeed: true,  HasDirection: false),
        new(6,  "Highlight",    HasColor: true,  HasSpeed: true,  HasDirection: false),
        new(7,  "Laser",        HasColor: true,  HasSpeed: true,  HasDirection: false),
        new(8,  "Ripple",       HasColor: true,  HasSpeed: true,  HasDirection: false),
        new(10, "Pulse",        HasColor: true,  HasSpeed: true,  HasDirection: false),
        new(11, "Comet",        HasColor: true,  HasSpeed: true,  HasDirection: false),
        new(12, "Flash",        HasColor: true,  HasSpeed: true,  HasDirection: false),
    ];

    internal static Entry? Find(int code) => Table.FirstOrDefault(e => e.Code == code);

    internal static bool IsKnown(int code) => Find(code) != null;

    /// <summary>The modes the ATK TUF-keyboard RGB path accepts: everything the table names below 12 except the
    /// reserved 9 — the set asus-wmi's `kbd_rgb_mode_store` itself filters to ("These are the known usable modes
    /// across all TUF/ROG").</summary>
    internal static IReadOnlyList<RgbModeInfo> TufBasicModes
        => Table.Where(e => e.Code < 12 && e.Code != 9).Select(e => ToModeInfo(e.Code)).ToList();

    /// <summary>Project one Aura mode to the vendor-neutral descriptor the UI binds to, carrying the mode code
    /// as the opaque handle the apply path reads back.</summary>
    internal static RgbModeInfo ToModeInfo(int code)
    {
        var e = Find(code) ?? new Entry(code, $"0x{code:X2}", false, false, false);
        return new RgbModeInfo(e.Name, e.HasColor, e.HasSpeed, e.Code, e.HasDirection);
    }
}

/// <summary>The Aura wire encoders: percentage → LedBrightness, percentage → Speed word, app direction → Aura
/// Direction word, and the flattened value list of the AuraEffect struct.</summary>
internal static class AsusAuraValues
{
    internal const int StaticMode = 0;   // AuraModeNum::Static

    /// <summary>A 0..100 brightness as an Aura LedBrightness (0 Off, 1 Low, 2 Med, 3 High).</summary>
    internal static int LedFor(byte percent)
        => percent == 0 ? 0 : percent < 34 ? 1 : percent < 67 ? 2 : 3;

    /// <summary>A LedBrightness value back to the 0..100 scale the app shows.</summary>
    internal static int PercentFor(int led) => led switch { 0 => 0, 1 => 33, 2 => 66, 3 => 100, _ => 0 };

    /// <summary>A 0..100 speed as the Speed variant word.</summary>
    internal static string SpeedFor(byte percent)
        => percent < 34 ? "Low" : percent < 67 ? "Med" : "High";

    /// <summary>The app's direction byte (1 or 2, Acer's vocabulary) as the Aura Direction word. The pairing is
    /// a reading of rog-aura's enum (Right = 0, Left = 1) and is marked unverified in the file header.</summary>
    internal static string DirectionFor(byte direction) => direction == 2 ? "Left" : "Right";

    /// <summary>The FLATTENED <c>led_mode_data</c> values for one effect — the struct's fields in order, with
    /// each nested struct's three bytes spelled out. This is what busctl consumes beside
    /// <see cref="AsusdAura.EffectSignature"/>, and it is the one place the wire order is written down.</summary>
    internal static string[] EffectValues(int mode, int zone, AccentColor colour1, AccentColor colour2,
                                          string speed, string direction)
        =>
        [
            mode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            zone.ToString(System.Globalization.CultureInfo.InvariantCulture),
            colour1.R.ToString(System.Globalization.CultureInfo.InvariantCulture),
            colour1.G.ToString(System.Globalization.CultureInfo.InvariantCulture),
            colour1.B.ToString(System.Globalization.CultureInfo.InvariantCulture),
            colour2.R.ToString(System.Globalization.CultureInfo.InvariantCulture),
            colour2.G.ToString(System.Globalization.CultureInfo.InvariantCulture),
            colour2.B.ToString(System.Globalization.CultureInfo.InvariantCulture),
            speed,
            direction,
        ];
}

/// <summary>
/// One asusd Aura device as an <see cref="IRgbController"/>: a single <see cref="RgbZone"/> whose effects are the
/// device's own <c>supported_basic_modes</c>, whose sub-zones are its supported key zones, and whose applies
/// write the AuraEffect struct (and then the brightness) through the delegated busctl runner.
///
/// The keyboard is the ZONE and its key regions are the sub-zones, mirroring how the Acer ENE controller models a
/// multi-zone keyboard, so the existing lighting UI needs no new shape.
/// </summary>
internal sealed class AsusdAuraController : IRgbController
{
    private readonly Func<string[], (int code, string output)> _busctl;
    private readonly string _path;
    private readonly List<RgbZone> _zones = [];

    private AsusdAuraController(Func<string[], (int code, string output)> busctl, string path,
                                string label, IReadOnlyList<int> modes, IReadOnlyList<int> zones)
    {
        _busctl = busctl;
        _path = path;

        var effects = modes.Where(AsusAuraModes.IsKnown).Select(AsusAuraModes.ToModeInfo).ToList();
        var keyZones = zones.Count(z => z is >= 1 and <= 4);   // AuraZone Key1..Key4
        _zones.Add(new RgbZone(label, Math.Max(1, keyZones), effects,
                               ApplyEffect, ApplySubZone, ReadBrightness));
    }

    public IReadOnlyList<RgbZone> Zones => _zones;

    /// <summary>Build a controller for one Aura object, or null when asusd offers this device no usable effect
    /// set (the same "unusable reads as absent" rule the other ports here keep).</summary>
    internal static AsusdAuraController? TryCreate(Func<string[], (int code, string output)> busctl, string path)
    {
        var (modeCode, modeOutput) = busctl(Asusd.GetPropertyArgumentsAt(
            path, AsusdAura.Interface, AsusdAura.SupportedBasicModesProperty));
        var modes = modeCode == 0 ? AsusdValues.ParseUIntArray(modeOutput) : [];
        if (!modes.Any(AsusAuraModes.IsKnown)) return null;

        var (zoneCode, zoneOutput) = busctl(Asusd.GetPropertyArgumentsAt(
            path, AsusdAura.Interface, AsusdAura.SupportedBasicZonesProperty));
        var zones = zoneCode == 0 ? AsusdValues.ParseUIntArray(zoneOutput) : [];

        var (typeCode, typeOutput) = busctl(Asusd.GetPropertyArgumentsAt(
            path, AsusdAura.Interface, AsusdAura.DeviceTypeProperty));
        var type = typeCode == 0 ? AsusdValues.ParseScalarUInt(typeOutput) ?? 0 : 0;

        return new AsusdAuraController(busctl, path, DeviceLabel(type), modes, zones);
    }

    /// <summary>What to call this device in the UI. Only laptop keyboards register <c>xyz.ljones.Aura</c>
    /// (Slash/AniMe/SCSI have their own interfaces), so the laptop types all read "Keyboard".</summary>
    private static string DeviceLabel(int deviceType) => deviceType switch
    {
        0 or 1 or 2 => "Keyboard",
        3 => "External drive",
        4 => "Ally",
        5 => "AniMe / Slash",
        _ => "ASUS Aura",
    };

    private bool ApplyEffect(RgbModeInfo effect, byte brightness, byte speed, byte direction, AccentColor colour)
    {
        var mode = effect.Handle is int code ? code : AsusAuraValues.StaticMode;
        var values = AsusAuraValues.EffectValues(mode, AsusdAura.WholeZone, colour, default,
                                                AsusAuraValues.SpeedFor(speed), AsusAuraValues.DirectionFor(direction));
        if (!WriteModeData(values)) return false;
        return WriteBrightness(AsusAuraValues.LedFor(brightness));
    }

    private bool ApplySubZone(int index, byte brightness, AccentColor colour)
    {
        var zone = Math.Clamp(index, 0, 3) + 1;   // AuraZone Key1..Key4
        var values = AsusAuraValues.EffectValues(AsusAuraValues.StaticMode, zone, colour, default,
                                                 AsusAuraValues.SpeedFor(0), AsusAuraValues.DirectionFor(1));
        if (!WriteModeData(values)) return false;
        return WriteBrightness(AsusAuraValues.LedFor(brightness));
    }

    private int? ReadBrightness()
    {
        var (code, output) = _busctl(Asusd.GetPropertyArgumentsAt(
            _path, AsusdAura.Interface, AsusdAura.BrightnessProperty));
        return code == 0 && AsusdValues.ParseScalarUInt(output) is { } led ? AsusAuraValues.PercentFor(led) : null;
    }

    private bool WriteModeData(string[] values)
    {
        var (code, _) = _busctl(Asusd.SetPropertyArgumentsAt(
            _path, AsusdAura.Interface, AsusdAura.LedModeDataProperty, AsusdAura.EffectSignature, values));
        return code == 0;
    }

    private bool WriteBrightness(int led)
    {
        var (code, _) = _busctl(Asusd.SetPropertyArgumentsAt(
            _path, AsusdAura.Interface, AsusdAura.BrightnessProperty, "u",
            led.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return code == 0;
    }

    /// <summary>Darken the device by asking for Off — the one blanking write Aura has.</summary>
    public bool Blank() => WriteBrightness(0);

    /// <summary>No handle of ours is held (asusd owns the device), so nothing to release.</summary>
    public void Dispose() { }
}
