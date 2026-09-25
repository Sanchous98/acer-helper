using System.Text.RegularExpressions;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The asusd AsusArmoury firmware-attribute surface — READ plus CONSENT-GATED, VALIDATED writes. This is the
// EC/BIOS-risky half of the ASUS support, so the design is deliberately paranoid and everything decidable lives
// here, un-suffixed, where the suite can reach it. The only I/O is the delegated busctl runner.
//
// THE INTERFACE, from asusctl (rog-dbus/src/asus_armoury.rs, asusd/src/asus_armoury.rs):
//   * one object PER ATTRIBUTE at /xyz/ljones/asus_armoury/<attr_name>, interface xyz.ljones.AsusArmoury
//   * properties: name:s, available_attrs:as, current_value:i, default_value:i, min_value:i, max_value:i,
//     scalar_increment:i, possible_values:ai, queued_gpu_value:i   (an unused numeric property reports -1)
//   * write: set-property current_value i <value>
//   * methods: restore_default(), apply_queued_gpu_value():b
//
// THREE KINDS OF ATTRIBUTE, AND THEY BEHAVE DIFFERENTLY (asusd's own table, rog-platform/src/asus_armoury.rs):
//   * Gpu  (gpu_mux_mode, dgpu_disable, egpu_enable, egpu_connected) — set_current_value QUEUES the value in
//     asusd's memory and does NOT touch the firmware; asusd applies the queue at SHUTDOWN. So a queued MUX
//     change lands on the next boot. THIS APP NEVER CALLS apply_queued_gpu_value: an immediate MUX apply is the
//     single highest-risk operation in the whole feature, and the deferred path is the vendor's own.
//   * Ppt  (ppt_pl1_spl/ppt_pl2_sppt/ppt_pl3_fppt/ppt_fppt, nv_dynamic_boost, nv_temp_target, nv_tgp, …) —
//     set_current_value stores the value and only writes the firmware while that profile/power-source's tuning
//     group is ENABLED in asusd. We mirror asusd's semantics and never enable tuning ourselves.
//   * ReadOnly (nv_base_tgp) — asusd refuses the write; we refuse it before the bus.
//   * Immediate/Bios — a direct firmware write (panel_od, charge_mode, boot_sound, …).
//   * an attribute whose name is not in asusd's table is UNKNOWN and is REFUSED: the app does not write a knob
//     whose type it cannot name.
//
// VALIDATION IS THE WHOLE POINT, and it REFUSES OVER GUESSING. Every write re-reads the live limits first and
// then requires ONE of:
//   * the value to be one of possible_values when the attribute reports a set; else
//   * the value to be within [min_value, max_value] and a whole number of scalar_increment steps from min_value.
// An attribute that reports NEITHER a value set NOR a range is refused: with no device-reported bound there is
// nothing to validate against, and inventing one is exactly the guess this phase forbids.
//
// THE RUNTIME IS UNVERIFIED on ASUS hardware: the interface, the semantics above and the wire forms are read
// from the asusd source and pinned by tests, but no write has been made on a real machine.

/// <summary>The AsusArmoury coordinates and the argument lists handed to <c>busctl</c>.</summary>
internal static partial class AsusdArmoury
{
    internal const string Interface = "xyz.ljones.AsusArmoury";
    internal const string Root = "/xyz/ljones/asus_armoury";

    internal const string NameProperty = "name";
    internal const string AvailableAttrsProperty = "available_attrs";
    internal const string CurrentValueProperty = "current_value";
    internal const string DefaultValueProperty = "default_value";
    internal const string MinValueProperty = "min_value";
    internal const string MaxValueProperty = "max_value";
    internal const string ScalarIncrementProperty = "scalar_increment";
    internal const string PossibleValuesProperty = "possible_values";
    internal const string QueuedGpuValueProperty = "queued_gpu_value";
    internal const string CurrentValueSignature = "i";

    internal static string AttributePath(string name) => $"{Root}/{name}";

    internal static string[] GetPropertyArguments(string path, string property)
        => Asusd.GetPropertyArgumentsAt(path, Interface, property);

    internal static string[] SetCurrentValueArguments(string path, int value)
        => Asusd.SetPropertyArgumentsAt(path, Interface, CurrentValueProperty, CurrentValueSignature,
                                        value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Every attribute object in a <c>busctl tree</c> reply, deduplicated. The bare root is not an
    /// attribute.</summary>
    internal static IReadOnlyList<string> AttributePaths(string treeOutput)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (Match m in AttributePathRegex().Matches(treeOutput))
            if (seen.Add(m.Value)) paths.Add(m.Value);
        return paths;
    }

    [GeneratedRegex(@"/xyz/ljones/asus_armoury/[^\s]+")]
    private static partial Regex AttributePathRegex();
}

/// <summary>How asusd treats one firmware attribute (rog-platform's FirmwareAttributeType). The distinction is
/// load-bearing rather than descriptive: <see cref="Gpu"/> writes are QUEUED, <see cref="ReadOnly"/> writes are
/// never made, and <see cref="Unknown"/> names are refused.</summary>
internal enum AsusAttributeKind { Ppt, Gpu, ReadOnly, Immediate, Bios, Unknown }

/// <summary>asusd's own attribute-type table, transcribed. Anything not named here is
/// <see cref="AsusAttributeKind.Unknown"/> and is refused rather than written.</summary>
internal static class AsusArmouryKinds
{
    private static readonly HashSet<string> Ppt =
    [
        "ppt_pl1_spl", "ppt_pl2_sppt", "ppt_pl3_fppt", "ppt_fppt",
        "ppt_apu_sppt", "ppt_platform_sppt", "nv_dynamic_boost", "nv_temp_target", "nv_tgp",
    ];

    private static readonly HashSet<string> Gpu =
    [
        "gpu_mux_mode", "dgpu_disable", "egpu_enable", "egpu_connected",
    ];

    private static readonly HashSet<string> Immediate =
    [
        "apu_mem", "cores_performance", "cores_efficiency", "charge_mode", "mcu_powersave",
        "panel_od", "panel_hd_mode", "mini_led_mode", "screen_auto_brightness",
        "kbd_leds_awake", "kbd_leds_sleep", "kbd_leds_boot", "kbd_leds_shutdown",
    ];

    internal static AsusAttributeKind KindOf(string name)
        => name switch
        {
            "nv_base_tgp" => AsusAttributeKind.ReadOnly,
            "boot_sound"  => AsusAttributeKind.Bios,
            _ when Ppt.Contains(name)       => AsusAttributeKind.Ppt,
            _ when Gpu.Contains(name)       => AsusAttributeKind.Gpu,
            _ when Immediate.Contains(name) => AsusAttributeKind.Immediate,
            _ => AsusAttributeKind.Unknown,
        };

    /// <summary>Whether a write to this kind is QUEUED by asusd (applied on shutdown) rather than immediate.
    /// Only GPU attributes are deferred.</summary>
    internal static bool IsQueued(AsusAttributeKind kind) => kind == AsusAttributeKind.Gpu;
}

/// <summary>What one attribute object reported — everything validation needs, read fresh from the device and
/// never guessed. <see cref="MinValue"/>/<see cref="MaxValue"/>/<see cref="ScalarIncrement"/> are -1 when the
/// attribute does not report them, exactly as asusd spells "unused".</summary>
internal sealed record AsusAttributeDescriptor(
    string Name,
    string Path,
    AsusAttributeKind Kind,
    bool Available,
    int CurrentValue,
    int MinValue,
    int MaxValue,
    int ScalarIncrement,
    int DefaultValue,
    IReadOnlyList<int> PossibleValues);

/// <summary>The user's answer to a request to write firmware/EC. The port asks before EVERY write; a false answer
/// refuses it. The key handed in is the LOCALIZATION KEY of the warning to show (the UI looks it up), and the
/// production wiring supplies a real prompt while tests supply a fake. Nothing in the app calls a write from the
/// periodic refresh loop — that is enforced structurally, because the write methods are only reachable by an
/// explicit caller.</summary>
internal delegate bool AsusWriteConsent(string warningKey);

/// <summary>The outcome of one attempted write. <see cref="Ok"/> is false for a refusal (validation, consent or
/// the daemon) and the <see cref="Message"/> is then the reason — a LOCALIZATION KEY for the reasons this app
/// owns, or the daemon's own words for a bus refusal (the UI runs it through <c>Loc.T</c>, which passes an
/// unknown key through unchanged). <see cref="Queued"/> means asusd accepted the value but will apply it at
/// shutdown, i.e. the machine must restart.</summary>
internal readonly record struct AsusWriteOutcome(bool Ok, bool Queued, string? Message)
{
    internal static AsusWriteOutcome Applied() => new(true, false, null);
    internal static AsusWriteOutcome QueuedForRestart(string message) => new(true, true, message);
    internal static AsusWriteOutcome Refused(string message) => new(false, false, message);
}

/// <summary>The messages this feature shows, as localization keys (Localization/Strings.Ru.cs holds the Russian
/// entries, and AcerHelper.Tests pins that each key exists there). Kept in one place so a test can hold the
/// refusal at a boundary to the exact sentence the user would see.</summary>
internal static class AsusArmouryMessages
{
    internal const string ReadOnly = "This setting is read-only on this machine.";
    internal const string Unavailable = "This machine does not expose that setting.";
    internal const string NoRange = "This machine does not report a range for that setting, so the app will not write it.";
    internal const string OutOfRange = "That value is outside the range this machine reports.";
    internal const string NotAStep = "That value is not a multiple of the machine's step size.";
    internal const string NotAnOption = "That value is not one of the values this machine accepts.";
    internal const string UnknownAttribute = "The app does not know how this machine treats that setting, so it will not write it.";
    internal const string Cancelled = "Change cancelled.";
    internal const string GpuQueued = "The change is queued and will be applied on the next restart.";
    internal const string GpuRebootWarning =
        "Changing the GPU mode is queued and only takes effect on the next restart. A wrong value can leave the "
        + "screen black — if that happens, force a shutdown by holding the power button, then start the machine "
        + "and change the mode back from a console or another display. See docs/asus-support.md.";
    internal const string PptWarning =
        "This writes a power limit to the firmware. It takes effect only while this profile's custom tuning is "
        + "enabled in asusd.";
    internal const string ImmediateWarning = "This writes a value to the firmware.";
}

/// <summary>
/// The validation rules, as ONE pure function so a boundary test can hold every refusal. It never invents a
/// bound: a value is accepted only against what the device reported.
/// </summary>
internal static class AsusArmouryRules
{
    internal static (bool ok, string? message) Validate(AsusAttributeDescriptor attr, int value)
    {
        if (attr.Kind == AsusAttributeKind.ReadOnly) return (false, AsusArmouryMessages.ReadOnly);
        if (attr.Kind == AsusAttributeKind.Unknown) return (false, AsusArmouryMessages.UnknownAttribute);
        if (!attr.Available) return (false, AsusArmouryMessages.Unavailable);

        if (attr.PossibleValues.Count > 0)
            return attr.PossibleValues.Contains(value)
                ? (true, null)
                : (false, AsusArmouryMessages.NotAnOption);

        if (attr.MinValue >= 0 && attr.MaxValue >= 0)
        {
            if (value < attr.MinValue || value > attr.MaxValue) return (false, AsusArmouryMessages.OutOfRange);
            if (attr.ScalarIncrement > 1 && (value - attr.MinValue) % attr.ScalarIncrement != 0)
                return (false, AsusArmouryMessages.NotAStep);
            return (true, null);
        }

        // Neither a value set nor a range: nothing to validate against, so refuse rather than guess.
        return (false, AsusArmouryMessages.NoRange);
    }
}

/// <summary>
/// The AsusArmoury port: enumerate the attribute objects, read their descriptors, and write — CONSENT-GATED,
/// VALIDATED and SINGLE-FLIGHT. Built over a delegated busctl runner so every branch is reachable without asusd
/// or ASUS hardware.
///
/// IT NEVER CALLS apply_queued_gpu_value. A GPU attribute write is queued into asusd (which is what the reboot
/// warning names) and asusd applies it at shutdown itself; an immediate apply is the highest-risk operation here
/// and the vendor's deferred path is the one we take.
/// </summary>
internal sealed class AsusdArmouryPort
{
    private readonly Func<string[], (int code, string output)> _busctl;
    private readonly AsusWriteConsent _consent;
    private readonly List<AsusAttributeDescriptor> _attributes;
    private readonly object _gate = new();

    private AsusdArmouryPort(Func<string[], (int code, string output)> busctl, AsusWriteConsent consent,
                             List<AsusAttributeDescriptor> attributes)
    {
        _busctl = busctl;
        _consent = consent;
        _attributes = attributes;
    }

    /// <summary>The attributes this machine exposes, as first read (limits are re-read on every write).</summary>
    internal IReadOnlyList<AsusAttributeDescriptor> Attributes => _attributes;

    internal AsusAttributeDescriptor? Find(string name)
        => _attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    /// <summary>Enumerate the attribute objects and build the port, or null when asusd exposes none. Each object
    /// is confirmed by introspection (the interface either appears on it or it does not).</summary>
    internal static AsusdArmouryPort? TryCreate(Func<string[], (int code, string output)> busctl, AsusWriteConsent consent)
    {
        var (treeCode, tree) = busctl(Asusd.TreeArguments());
        if (treeCode != 0) return null;

        var attributes = new List<AsusAttributeDescriptor>();
        foreach (var path in AsusdArmoury.AttributePaths(tree))
        {
            var (introspectCode, introspect) = busctl(Asusd.IntrospectArguments(path));
            if (introspectCode != 0 || !Asusd.HasInterface(introspect, AsusdArmoury.Interface)) continue;
            if (ReadDescriptor(busctl, path) is { } descriptor) attributes.Add(descriptor);
        }
        return attributes.Count > 0 ? new AsusdArmouryPort(busctl, consent, attributes) : null;
    }

    /// <summary>The queued-but-not-applied GPU value, or null when none is queued / the attribute is not a GPU
    /// one. Read-only; the UI uses it to say "queued: N (applies on restart)".</summary>
    internal int? QueuedGpuValue(AsusAttributeDescriptor attr)
    {
        if (attr.Kind != AsusAttributeKind.Gpu) return null;
        var (code, output) = _busctl(AsusdArmoury.GetPropertyArguments(attr.Path, AsusdArmoury.QueuedGpuValueProperty));
        if (code != 0) return null;
        var value = AsusdValues.ParseScalarUInt(output);
        return value is null or < 0 ? null : value;
    }

    /// <summary>The attribute's live <c>current_value</c>, or null when it cannot be read. Used by the MUX port
    /// so its read reflects the firmware NOW rather than the snapshot taken when the port was built.</summary>
    internal int? ReadCurrentValue(AsusAttributeDescriptor attr)
    {
        var (code, output) = _busctl(AsusdArmoury.GetPropertyArguments(attr.Path, AsusdArmoury.CurrentValueProperty));
        return code == 0 ? AsusdValues.ParseScalarUInt(output) : null;
    }

    /// <summary>
    /// Write one attribute value. THE ORDER IS THE SAFETY: refresh the live limits → validate → ask consent →
    /// write, all under one lock so two writes cannot run concurrently.
    ///
    /// A GPU attribute comes back <see cref="AsusWriteOutcome.Queued"/> with the reboot warning: the value is in
    /// asusd's queue, not on the firmware. Every other kind applies immediately.
    /// </summary>
    internal AsusWriteOutcome Write(AsusAttributeDescriptor attr, int value)
    {
        // Refresh first: min/max/step/possible_values are re-read from the device on every write (PPT limits can
        // move with the profile), so validation is against the LIVE limits and never a stale or invented one.
        if (Refresh(attr) is not { } live) return AsusWriteOutcome.Refused(AsusArmouryMessages.Unavailable);

        if (AsusArmouryRules.Validate(live, value) is (false, { } refusal))
            return AsusWriteOutcome.Refused(refusal);

        var warning = live.Kind switch
        {
            AsusAttributeKind.Gpu => AsusArmouryMessages.GpuRebootWarning,
            AsusAttributeKind.Ppt => AsusArmouryMessages.PptWarning,
            _ => AsusArmouryMessages.ImmediateWarning,
        };

        lock (_gate)
        {
            if (!_consent(warning)) return AsusWriteOutcome.Refused(AsusArmouryMessages.Cancelled);

            var (code, output) = _busctl(AsusdArmoury.SetCurrentValueArguments(live.Path, value));
            if (code != 0) return AsusWriteOutcome.Refused(Asusd.Describe(code, output));

            return AsusArmouryKinds.IsQueued(live.Kind)
                ? AsusWriteOutcome.QueuedForRestart(AsusArmouryMessages.GpuQueued)
                : AsusWriteOutcome.Applied();
        }
    }

    /// <summary>Re-read the validation-relevant properties. Null when the attribute itself cannot be reached —
    /// which is a refusal, not a fallback to the cached descriptor.</summary>
    private AsusAttributeDescriptor? Refresh(AsusAttributeDescriptor attr)
    {
        var available = ReadAvailableAttrs(attr.Path);
        if (available == null) return null;

        var min = ReadInt(attr.Path, AsusdArmoury.MinValueProperty);
        var max = ReadInt(attr.Path, AsusdArmoury.MaxValueProperty);
        if (min is null || max is null) return null;

        return attr with
        {
            Available = available.Contains(AsusdArmoury.CurrentValueProperty),
            MinValue = min.Value,
            MaxValue = max.Value,
            ScalarIncrement = ReadInt(attr.Path, AsusdArmoury.ScalarIncrementProperty) ?? -1,
            PossibleValues = ReadPossibleValues(attr.Path) ?? [],
        };
    }

    private static AsusAttributeDescriptor? ReadDescriptor(Func<string[], (int code, string output)> busctl, string path)
    {
        var (nameCode, nameOutput) = busctl(AsusdArmoury.GetPropertyArguments(path, AsusdArmoury.NameProperty));
        if (nameCode != 0) return null;
        var name = AsusdValues.ParseStringArray(nameOutput).FirstOrDefault();
        if (string.IsNullOrEmpty(name)) return null;

        var available = ReadAvailableAttrs(busctl, path);
        var possible = ReadPossibleValues(busctl, path);

        return new AsusAttributeDescriptor(
            Name: name,
            Path: path,
            Kind: AsusArmouryKinds.KindOf(name),
            Available: available?.Contains(AsusdArmoury.CurrentValueProperty) ?? false,
            CurrentValue: ReadInt(busctl, path, AsusdArmoury.CurrentValueProperty) ?? -1,
            MinValue: ReadInt(busctl, path, AsusdArmoury.MinValueProperty) ?? -1,
            MaxValue: ReadInt(busctl, path, AsusdArmoury.MaxValueProperty) ?? -1,
            ScalarIncrement: ReadInt(busctl, path, AsusdArmoury.ScalarIncrementProperty) ?? -1,
            DefaultValue: ReadInt(busctl, path, AsusdArmoury.DefaultValueProperty) ?? -1,
            PossibleValues: possible ?? []);
    }

    private IReadOnlyList<string>? ReadAvailableAttrs(string path)
        => ReadAvailableAttrs(_busctl, path);

    private static IReadOnlyList<string>? ReadAvailableAttrs(Func<string[], (int, string)> busctl, string path)
    {
        var (code, output) = busctl(AsusdArmoury.GetPropertyArguments(path, AsusdArmoury.AvailableAttrsProperty));
        return code == 0 ? AsusdValues.ParseStringArray(output) : null;
    }

    private int? ReadInt(string path, string property)
        => ReadInt(_busctl, path, property);

    private static int? ReadInt(Func<string[], (int, string)> busctl, string path, string property)
    {
        var (code, output) = busctl(AsusdArmoury.GetPropertyArguments(path, property));
        return code == 0 ? AsusdValues.ParseScalarUInt(output) : null;
    }

    private IReadOnlyList<int>? ReadPossibleValues(string path)
        => ReadPossibleValues(_busctl, path);

    private static IReadOnlyList<int>? ReadPossibleValues(Func<string[], (int, string)> busctl, string path)
    {
        var (code, output) = busctl(AsusdArmoury.GetPropertyArguments(path, AsusdArmoury.PossibleValuesProperty));
        return code == 0 ? AsusdValues.ParseUIntArray(output) : null;
    }
}
