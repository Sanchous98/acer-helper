using System.Text.RegularExpressions;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// WHY THIS FILE IS UN-SUFFIXED, in the words of the rule the exemplars state (CardwireGpuAccess.cs,
// AcerProfilePorts.cs): the test project targets net10.0-windows while AcerHelper.csproj excludes **/*.Linux.cs
// from that TFM, so anything left in a Linux file cannot be reached by the suite at all. Everything that DECIDES
// lives here — the profile vocabulary, the value parsers, the busctl argument lists, the ownership policy — and
// the two things that TOUCH the machine (the busctl call and the presence probe) arrive as delegates or from the
// Linux half beside this file.
//
// THE MECHANISM, from asusd (the asusctl daemon). The system-bus name is xyz.ljones.Asusd and its objects live
// under the base path /xyz/ljones. Phase 1 binds ONE interface, xyz.ljones.Platform:
//   * property platform_profile          — the active kernel `platform_profile` value
//   * property platform_profile_choices  — the set the kernel exposes
//   * property charge_control_end_threshold (20..=100) — the battery charge cap as a PERCENTAGE
//   * method   next_platform_profile()   — asusd's own cycle (no arguments)
//
// ONE API, TWO WIRE FORMS, and both are handled on purpose. asusd's PlatformProfile enum is declared with
// `#[zvariant(signature = "u")]` on current upstream, so the property arrives as a uint (0 Balanced, 1
// Performance, 2 Quiet, 3 LowPower, 4 Custom) and the choices as `au`; older builds exposed the same enum as
// strings ("quiet", "balanced", …). Rather than guessing which asusd a user runs, the parser reads the value AND
// its D-Bus signature off `busctl get-property` and writes the profile back in the FORM IT OBSERVED. The token
// vocabulary the app keys on is always the kernel's string form, so `settings.json` is identical whichever
// source (asusd or the generic sysfs fallback) is live on a given boot.
//
// WHY THE GENERIC SOURCE IS STILL THE FALLBACK. asusd is optional: a machine without it is served by
// power-profiles-daemon or the raw `platform_profile` sysfs node the base GenericDevice already wires. This
// backend therefore takes a port over ONLY when asusd is on the bus AND the port is actually usable — the
// ownership policy at the bottom of this file, tested.

/// <summary>The asusd system-bus coordinates and the exact argument lists handed to <c>busctl</c>.
///
/// The argument lists are built here rather than at the call site so a test can hold each one to its literal
/// shape — a renamed interface or a lowercased property is a call that silently does nothing (or writes to the
/// wrong place), which is the class of defect <c>CardwireGpuAccessTests</c> exists for. The <c>--system</c>
/// prefix is NOT in these lists: <c>Busctl.Call</c> adds it (its one call-site shape, PowerProfiles.Linux.cs).</summary>
internal static class Asusd
{
    internal const string Service = "xyz.ljones.Asusd";
    internal const string BasePath = "/xyz/ljones";
    internal const string PlatformInterface = "xyz.ljones.Platform";

    internal const string PlatformProfileProperty = "platform_profile";
    internal const string PlatformProfileChoicesProperty = "platform_profile_choices";
    internal const string ChargeControlEndThresholdProperty = "charge_control_end_threshold";
    internal const string NextPlatformProfileMethod = "next_platform_profile";

    /// <summary>Is the name owned on the system bus right now? The cheapest honest presence test — it asks the
    /// bus, not the filesystem, so a daemon that is installed but crashed reads as absent (the same test
    /// CardwireGpuAccess.Linux.cs uses).</summary>
    internal static string[] StatusArguments() => ["status", Service];

    internal static string[] GetPropertyArguments(string iface, string property)
        => ["get-property", Service, BasePath, iface, property];

    internal static string[] SetPropertyArguments(string iface, string property, string signature, string value)
        => ["set-property", Service, BasePath, iface, property, signature, value];

    /// <summary>A method call with no arguments: for <c>next_platform_profile</c> the signature is omitted
    /// entirely, which is how <c>busctl</c> spells a zero-argument call.</summary>
    internal static string[] CallArguments(string iface, string method)
        => ["call", Service, BasePath, iface, method];

    /// <summary>Paths of the objects asusd currently serves. <c>busctl tree</c> asks the daemon rather than the
    /// filesystem, and the object paths it prints are where the per-device interfaces (Aura) live.</summary>
    internal static string[] TreeArguments() => ["tree", Service];

    /// <summary>Introspect one object — the presence probe for an interface that has no
    /// <c>supported_properties()</c> of its own (Backlight, Aura): the interface either appears on the object or
    /// it does not.</summary>
    internal static string[] IntrospectArguments(string path) => ["introspect", Service, path];

    internal static string[] GetPropertyArgumentsAt(string path, string iface, string property)
        => ["get-property", Service, path, iface, property];

    /// <summary>Set a property on a NON-default path, with a possibly-complex value. <paramref name="values"/> is
    /// the FLATTENED value list busctl expects for <paramref name="signature"/> (a struct's fields, and each
    /// nested array's own length, are separate arguments).</summary>
    internal static string[] SetPropertyArgumentsAt(string path, string iface, string property,
                                                    string signature, params string[] values)
        => ["set-property", Service, path, iface, property, signature, .. values];

    /// <summary>A method call on a non-default path: <paramref name="values"/> is the FLATTENED argument list
    /// busctl expects for <paramref name="signature"/> (a struct's fields, and each nested array's own length,
    /// are separate arguments).</summary>
    internal static string[] CallArgumentsAt(string path, string iface, string method, string signature,
                                             params string[] values)
        => ["call", Service, path, iface, method, signature, .. values];

    /// <summary>Whether <c>busctl introspect</c> output carries the interface. The check is line-anchored and
    /// exact-token: the interface name appears alone in the first column of its own row, so a substring test
    /// would match a member or a doc string and answer "present" for an interface that is not there.</summary>
    internal static bool HasInterface(string introspectOutput, string iface)
    {
        foreach (var line in introspectOutput.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(iface, StringComparison.Ordinal)) continue;
            if (trimmed.Length == iface.Length || char.IsWhiteSpace(trimmed[iface.Length])) return true;
        }
        return false;
    }

    /// <summary>The D-Bus signature <c>busctl get-property</c> prints first — "s", "u", "as", "au", "y", … —
    /// or "" when there is nothing to read. The parsers below branch on it so one reader serves both asusd
    /// generations.</summary>
    internal static string SignatureOf(string output)
    {
        var trimmed = output.TrimStart();
        var end = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        return end < 0 ? trimmed : trimmed[..end];
    }

    /// <summary>Whether a signature names a D-Bus integer type — the branch that decides the profile is written
    /// as a NUMBER rather than a word.</summary>
    internal static bool IsIntegerSignature(string signature)
        => signature.Length > 0 && "ybnqiuxt".Contains(signature[^1]);

    /// <summary>busctl's refusal, on one line for a status row — the shape CardwireGpuAccess.Describe uses. The
    /// daemon's own words are kept: a refusal is its sentence, not ours to reword.</summary>
    internal static string Describe(int code, string output)
    {
        var text = string.Join("; ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length == 0 ? $"busctl exited with {code}" : text;
    }
}

/// <summary>The ASUS performance-profile vocabulary: the kernel's <c>platform_profile</c> tokens that asusd
/// proxies, and what each one IS to the app.
///
/// IT MIRRORS THE GENERIC SYSFS TABLE ON PURPOSE. The ids are the kernel tokens ("low-power", "quiet",
/// "balanced", "performance", "custom") and the kinds and colours are exactly what
/// <c>SysfsPowerProfiles.Describe</c> answers for the same words — because the two sources can serve the SAME
/// machine on different boots (asusd installed or not) and the app persists the id in `settings.json`, the tray,
/// the per-mode presets and the power-source memory. A divergence here would make a preset written with asusd
/// present fail to apply when the fallback is live, for no reason the hardware gives.</summary>
internal static class AsusProfiles
{
    /// <param name="Token">The kernel token, the app's stable id.</param>
    /// <param name="Code">The asusd PlatformProfile numeric value (the wire form on current asusd).</param>
    private sealed record Entry(string Token, int Code, string Name, ProfileKind Kind, AccentColor Accent);

    // Numeric codes are asusd's own enum (rog-platform/src/platform.rs): Balanced = 0, Performance = 1,
    // Quiet = 2, LowPower = 3, Custom = 4. Colours are byte-for-byte the generic sysfs table's.
    private static readonly Entry[] Table =
    [
        new("low-power",   3, "Low power",   ProfileKind.Eco,         new AccentColor(0x00, 0x89, 0x7B)),
        new("quiet",       2, "Quiet",       ProfileKind.Quiet,       new AccentColor(0x42, 0x85, 0xF4)),
        new("balanced",    0, "Balanced",    ProfileKind.Balanced,    new AccentColor(0x2E, 0x7D, 0x32)),
        new("performance", 1, "Performance", ProfileKind.Performance, new AccentColor(0xD3, 0x2F, 0x2F)),
        new("custom",      4, "Custom",      ProfileKind.Other,       new AccentColor(0x80, 0x80, 0x80)),
    ];

    private static Entry? Find(string token)
        => Table.FirstOrDefault(e => string.Equals(e.Token, token, StringComparison.OrdinalIgnoreCase));

    private static Entry? Find(int code) => Table.FirstOrDefault(e => e.Code == code);

    /// <summary>The app profile for a kernel token. A token the table does not know is carried through with the
    /// token itself as its display name and <see cref="ProfileTraits.Unknown"/>'s class — NOT mapped onto a
    /// nearby mode.</summary>
    internal static PerformanceProfile ToProfile(string token)
        => Find(token) is { } e ? new PerformanceProfile(e.Token, e.Name) : new PerformanceProfile(token, token);

    /// <summary>The wire token a profile id stands for, or null for an id that is not one of ours. A query, not
    /// a write, so "not ours" is an answer rather than a throw.</summary>
    internal static string? TokenOf(PerformanceProfile profile) => Find(profile.Id)?.Token;

    /// <summary>The kernel token for an asusd numeric value, or null for a number the enum does not name.</summary>
    internal static string? TokenForCode(int code) => Find(code)?.Token;

    /// <summary>The asusd numeric value for a token, or null for a token the table does not know (which is why
    /// a numeric-wire machine can only ever write a profile it offered).</summary>
    internal static int? CodeFor(string token) => Find(token)?.Code;

    /// <summary>The class and colours of a profile, off the same table — so the list offered and the set
    /// classified cannot disagree.</summary>
    internal static ProfileTraits TraitsOf(PerformanceProfile profile)
        => Find(profile.Id) is { } e ? new ProfileTraits(e.Kind, e.Accent) : ProfileTraits.Unknown;
}

/// <summary>Reads asusd's property payloads. ONE reader per SHAPE, because the same API arrives as strings on
/// older asusd and as numbers on current asusd (see the file header), and the parser must not assume which.</summary>
internal static partial class AsusdValues
{
    /// <summary>The profile tokens in a <c>platform_profile_choices</c> payload — <c>as</c> (quoted words) or
    /// <c>au</c> (a count and the numeric codes). An array's leading count is dropped, never read as a value.</summary>
    internal static IReadOnlyList<string> ParseProfileList(string output)
    {
        var quoted = Quoted().Matches(output).Select(m => m.Groups[1].Value).ToList();
        if (quoted.Count > 0) return quoted;

        var codes = Integers(output);
        if (Asusd.SignatureOf(output).StartsWith("a", StringComparison.Ordinal) && codes.Count > 0)
            codes.RemoveAt(0);   // the array length
        return codes.Select(AsusProfiles.TokenForCode).Where(t => t != null).Select(t => t!).ToList();
    }

    /// <summary>The active profile token in a <c>platform_profile</c> payload — <c>s</c> (one quoted word) or
    /// <c>u</c> (one numeric code). Null when the payload names nothing the table can read.</summary>
    internal static string? ParseProfile(string output)
    {
        var quoted = Quoted().Match(output);
        if (quoted.Success) return quoted.Groups[1].Value;

        var codes = Integers(output);
        if (codes.Count == 0) return null;
        if (Asusd.SignatureOf(output).StartsWith("a", StringComparison.Ordinal)) codes.RemoveAt(0);
        return codes.Count > 0 ? AsusProfiles.TokenForCode(codes[^1]) : null;
    }

    /// <summary>The last integer in a scalar payload (the charge threshold, a brightness), whatever its integer
    /// signature (<c>y</c> for u8, <c>u</c> for u32). Null when the signature is not an integer or there is no
    /// number to read — a string payload must not have a number scraped out of its quotes.</summary>
    internal static int? ParseScalarUInt(string output)
    {
        if (!Asusd.IsIntegerSignature(Asusd.SignatureOf(output))) return null;
        var codes = Integers(output);
        return codes.Count > 0 ? codes[^1] : null;
    }

    /// <summary>The charge-threshold payload reader — the scalar-uint rule under the name that port uses.</summary>
    internal static int? ParseThreshold(string output) => ParseScalarUInt(output);

    /// <summary>The values of an <c>au</c> payload (a choice/brightness/mode list), with the array's leading
    /// count dropped. An empty payload yields nothing rather than a spurious 0.</summary>
    internal static IReadOnlyList<int> ParseUIntArray(string output)
    {
        var codes = Integers(output);
        if (Asusd.SignatureOf(output).StartsWith("a", StringComparison.Ordinal) && codes.Count > 0)
            codes.RemoveAt(0);   // the array length
        return codes;
    }

    /// <summary>The quoted words of an <c>as</c> payload (a name, an available-property list). The signature
    /// and the length are not words, so they are simply not matched.</summary>
    internal static IReadOnlyList<string> ParseStringArray(string output)
        => Quoted().Matches(output).Select(m => m.Groups[1].Value).ToList();

    private static List<int> Integers(string output)
    {
        var list = new List<int>();
        foreach (Match m in Integer().Matches(output))
            if (int.TryParse(m.Value, out var v)) list.Add(v);
        return list;
    }

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex Quoted();

    // A bare run of digits. The signature token ("u", "au", "y") is alphabetic and never matches; the array
    // count is the only integer the caller strips per shape.
    [GeneratedRegex(@"-?\d+")]
    private static partial Regex Integer();
}

/// <summary>
/// The asusd-backed performance-profiles port. It follows <c>PpdPowerProfiles</c> in what it exposes and how it
/// maps (<see cref="IPowerProfiles"/> + <see cref="IProfileTraits"/>), and <c>CardwireGpuAccessPort</c> in how it
/// is built: the busctl runner is a delegate, so the whole port — its probing, its parsing, its writes — is
/// reachable by a test without asusd, a bus, or a Linux box.
///
/// WHERE IT SITS. The composition root's Linux half (<c>AsusDevice.Linux.cs</c>) builds it only when asusd is on
/// the bus, and hands the generic PPD/sysfs port it replaces to nobody — the base <c>GenericDevice</c> already
/// wired the fallback, and the ownership policy below is what decides whether this takes over.
/// </summary>
internal sealed class AsusdPlatformPort : IPowerProfiles, IProfileTraits
{
    private readonly Func<string[], (int code, string output)> _busctl;

    // The D-Bus signature the profile property reported when this port was built ("s" or "u"). The write uses
    // it so the value goes out in the form the live asusd accepts.
    private readonly string _profileSignature;

    public AsusdPlatformPort(Func<string[], (int code, string output)> busctl)
    {
        _busctl = busctl;

        var (choicesCode, choicesOutput) =
            _busctl(Asusd.GetPropertyArguments(Asusd.PlatformInterface, Asusd.PlatformProfileChoicesProperty));
        All = choicesCode == 0
            ? AsusdValues.ParseProfileList(choicesOutput).Select(AsusProfiles.ToProfile).ToList()
            : [];
        Available = All.Count > 0;

        var (profileCode, profileOutput) =
            _busctl(Asusd.GetPropertyArguments(Asusd.PlatformInterface, Asusd.PlatformProfileProperty));
        _profileSignature = profileCode == 0 ? Asusd.SignatureOf(profileOutput) : "";
    }

    /// <summary>Whether asusd offered a profile set. False when the daemon is gone or the property is
    /// unsupported, which is what keeps the generic port in place.</summary>
    public bool Available { get; }

    public string? LastError { get; private set; }

    /// <summary>The set the kernel exposes, in asusd's order, with the kernel tokens as ids.</summary>
    public IReadOnlyList<PerformanceProfile> All { get; }

    /// <summary>All of <see cref="All"/>. There is no power-source gate here, deliberately: parity with the
    /// generic Linux and Acer sources, whose set is a hardware fact (the kernel's choice list) rather than a
    /// policy the app invents.</summary>
    public IReadOnlyList<PerformanceProfile> Selectable() => All;

    public PerformanceProfile? Current()
    {
        var (code, output) =
            _busctl(Asusd.GetPropertyArguments(Asusd.PlatformInterface, Asusd.PlatformProfileProperty));
        if (code != 0) return null;
        var token = AsusdValues.ParseProfile(output);
        return token == null ? null : All.FirstOrDefault(p => p.Id == token) ?? AsusProfiles.ToProfile(token);
    }

    /// <summary>Write the active profile. A profile this port did not offer is REFUSED rather than mapped onto a
    /// nearby one — the table and the kernel's choice list are the authority, and a wrong write here is a power
    /// envelope the user did not ask for.</summary>
    public bool Set(PerformanceProfile profile)
    {
        var token = All.FirstOrDefault(p => p.Id == AsusProfiles.TokenOf(profile))?.Id;
        if (token == null)
        {
            LastError = $"\"{profile.DisplayName}\" is not one of the machine's performance profiles";
            return false;
        }

        var signature = _profileSignature.Length > 0 ? _profileSignature : "s";
        var value = Asusd.IsIntegerSignature(signature)
            ? AsusProfiles.CodeFor(token)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? token
            : token;

        var (code, output) =
            _busctl(Asusd.SetPropertyArguments(Asusd.PlatformInterface, Asusd.PlatformProfileProperty, signature, value));
        if (code != 0) { LastError = Asusd.Describe(code, output); return false; }
        LastError = null;
        return true;
    }

    /// <summary>asusd's own <c>next_platform_profile()</c> — the cycle the vendor UI uses. Exposed for the ASUS
    /// performance hotkey path (a later phase); the app's own hotkey currently cycles the port's set itself.</summary>
    internal bool Next()
    {
        var (code, output) = _busctl(Asusd.CallArguments(Asusd.PlatformInterface, Asusd.NextPlatformProfileMethod));
        if (code != 0) { LastError = Asusd.Describe(code, output); return false; }
        LastError = null;
        return true;
    }

    public ProfileTraits Traits(PerformanceProfile profile) => AsusProfiles.TraitsOf(profile);
}

/// <summary>What the ASUS Linux half found when it probed. The machine's state, as four booleans a test can
/// write down — the input the ownership policy below is a pure function of.</summary>
/// <param name="AsusdPresent">The name is owned on the system bus right now.</param>
/// <param name="ProfilesAvailable">The asusd profile port returned a usable choice set.</param>
/// <param name="ChargeLimitUsable">The asusd charge-threshold property was readable.</param>
internal readonly record struct AsusdPlatformFacts(bool AsusdPresent, bool ProfilesAvailable, bool ChargeLimitUsable);

/// <summary>
/// When asusd MAY take a port over. asusd is optional and its absence must be invisible: a machine without it is
/// served by the generic power-profiles-daemon / <c>platform_profile</c> sysfs port the base device already
/// wired, and this policy is what states — once, testably — that the vendor port replaces it only where asusd
/// is present AND its own property is usable. A second expression at the call site is how a half-built port ends
/// up standing in front of a working generic one.
/// </summary>
internal static class AsusdOwnership
{
    internal static bool TakeOverProfiles(in AsusdPlatformFacts facts)
        => facts.AsusdPresent && facts.ProfilesAvailable;

    internal static bool TakeOverChargeLimit(in AsusdPlatformFacts facts)
        => facts.AsusdPresent && facts.ChargeLimitUsable;

    /// <summary>The Phase-2 peripherals, each gated the same way: asusd present AND the property/interface it
    /// needs actually usable. Kept as one-liners over explicit booleans rather than folded into
    /// <see cref="AsusdPlatformFacts"/> so a port that arrives later adds a branch, not a wider record.</summary>
    internal static bool TakeOverAura(bool present, bool auraAvailable) => present && auraAvailable;

    internal static bool TakeOverBacklight(bool present, bool usable) => present && usable;

    internal static bool TakeOverFanCurves(bool present, bool readable) => present && readable;
}

/// <summary>
/// Applies the asusd ports to the machine — the decision half of <c>AsusDevice.Linux.cs</c>. It lives here,
/// un-suffixed, so a test can drive the fallback with a fake machine and written-down facts: the all-absent case
/// (asusd not installed) must leave every generic port exactly where the base device put it, which is a claim the
/// Linux file alone cannot be held to.
/// </summary>
internal static class AsusWiring
{
    internal static void Apply(Device machine, in AsusdPlatformFacts facts,
                               IPowerProfiles? profiles, BatteryToggle? chargeLimit)
    {
        if (AsusdOwnership.TakeOverProfiles(facts) && profiles is { } p) machine.PowerProfiles = p;
        if (AsusdOwnership.TakeOverChargeLimit(facts) && chargeLimit is { } c) machine.Battery.ChargeLimit = c;
    }

    /// <summary>The RGB side: an enumerated Aura device replaces the machine's (usually absent) Lighting slot
    /// only when it was actually built from a live interface.</summary>
    internal static void ApplyAura(Device machine, bool present, bool auraAvailable, IRgbDevice? aura)
    {
        if (AsusdOwnership.TakeOverAura(present, auraAvailable) && aura is { } a) machine.Lighting = a;
    }
}
