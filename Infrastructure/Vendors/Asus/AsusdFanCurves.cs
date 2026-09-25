using System.Globalization;
using System.Text.RegularExpressions;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The asusd FanCurves interface — READ ONLY in Phase 2.
//
// READ-ONLY ON PURPOSE, and the reasons are two rather than one conservative shrug:
//   1. A fan-curve WRITE touches the EC. rog-profiles writes pwmN_auto_point*_pwm/_temp and then pwmN_enable
//      directly, and asusd itself has to re-apply the PPT group after every curve write because the write resets
//      the EC fan mode (see asusd/src/ctrl_fancurves.rs, reapply_ppt). That is exactly the class of write Phase 3
//      gates behind explicit consent.
//   2. asusd's curve is TEMPERATURE-POINT based (eight temp/pwm pairs per fan per platform profile), while the
//      app's IFanControl is mode-and-speed based (Auto / Max / Custom CPU+GPU percentages) and its own fan curve
//      lives in Domain/FanCurveEngine.cs. Forcing the first into the second would be a bad mapping: there is no
//      "mode" to report and no place a sixteen-point curve belongs in a two-slider custom speed.
// So Phase 2 implements the CAPABILITY + READ plumbing — enumerate the curves, parse them into a pure model —
// and leaves both the IFanControl takeover and any write to Phase 3, where the safety review lives.
//
// THE WIRE SHAPE, from asusctl's rog-profiles crate:
//   fan_curve_data(profile: PlatformProfile) -> Vec<CurveData>
//   CurveData = struct { fan: FanCurvePU, pwm: [u8;8], temp: [u8;8], enabled: bool }
//   FanCurvePU is declared #[zvariant(signature = "s")], so its wire value is a WORD ("cpu"/"gpu"/"mid");
//   the two byte arrays are D-Bus `ay` (length + bytes), so CurveData's signature is (sayayb).
//
// THE PARSE IS TOLERANT OF BUSCTL'S PUNCTUATION by construction: it walks the ordered string/int/bool tokens
// rather than a specific layout of parentheses and brackets, so it does not depend on how busctl chooses to
// print the nested struct/array. Counts are read from the arrays' own length prefixes, never guessed.

/// <summary>The asusd FanCurves coordinates and the exact read call.</summary>
internal static class AsusdFanCurves
{
    internal const string Interface = "xyz.ljones.FanCurves";
    internal const string FanCurveDataMethod = "fan_curve_data";

    /// <summary>The one read this phase makes. <paramref name="profileSignature"/> is the platform profile's
    /// live wire form ("u" numeric on current asusd, "s" on older builds) — the same form the profile port
    /// observed — and <paramref name="profileValue"/> is the matching code or token.</summary>
    internal static string[] FanCurveDataArguments(string profileSignature, string profileValue)
        => Asusd.CallArgumentsAt(Asusd.BasePath, Interface, FanCurveDataMethod, profileSignature, profileValue);

    /// <summary>The wire value of a profile token under a signature: its numeric code on the numeric form, the
    /// token itself on the string form, or null for a token this table does not know. Shared by the reader and
    /// the writer so the two cannot disagree about what they send.</summary>
    internal static string? ProfileValue(string profileSignature, string profileToken)
        => Asusd.IsIntegerSignature(profileSignature)
            ? AsusProfiles.CodeFor(profileToken)?.ToString(CultureInfo.InvariantCulture)
            : AsusProfiles.TokenOf(AsusProfiles.ToProfile(profileToken));
}

/// <summary>Which fan a curve belongs to (rog-profiles FanCurvePU).</summary>
internal enum AsusFan { Cpu, Gpu, Mid, Unknown }

/// <summary>One point of a fan curve: a temperature in °C and the fan power as the 0..100 percentage rog-profiles
/// itself derives from the raw 0..255 pwm byte.</summary>
internal readonly record struct AsusFanCurvePoint(byte TempC, byte Percent);

/// <summary>One fan's stored curve for a profile, and whether asusd has it enabled (enabled curves are the ones
/// written to the device).</summary>
internal sealed record AsusFanCurve(AsusFan Fan, IReadOnlyList<AsusFanCurvePoint> Points, bool Enabled);

/// <summary>Parses a <c>fan_curve_data</c> reply into the pure model. Null when the payload is not a
/// well-formed curve array; an empty list is a valid "this profile stores no curves".</summary>
internal static partial class AsusFanCurveValues
{
    internal static AsusFan FanFor(string name) => name.ToLowerInvariant() switch
    {
        "cpu" => AsusFan.Cpu,
        "gpu" => AsusFan.Gpu,
        "mid" => AsusFan.Mid,
        _     => AsusFan.Unknown,
    };

    internal static IReadOnlyList<AsusFanCurve>? Parse(string output)
    {
        var items = Token().Matches(output).Cast<Match>().ToList();
        if (items.Count == 0 || !items[0].Groups[2].Success) return null;

        var i = 0;
        if (!ReadInt(items, ref i, out var count) || count < 0) return null;

        var curves = new List<AsusFanCurve>(count);
        for (var n = 0; n < count; n++)
        {
            if (i >= items.Count || !items[i].Groups[1].Success) return null;
            var fan = FanFor(items[i++].Groups[1].Value);

            if (!ReadByteArray(items, ref i, out var pwm)) return null;
            if (!ReadByteArray(items, ref i, out var temp)) return null;

            if (i >= items.Count || !items[i].Groups[3].Success) return null;
            var enabled = items[i++].Groups[3].Value == "true";

            if (pwm.Count != temp.Count) return null;
            var points = new List<AsusFanCurvePoint>(pwm.Count);
            for (var k = 0; k < pwm.Count; k++)
                points.Add(new AsusFanCurvePoint(
                    (byte)Math.Clamp(temp[k], 0, 255),
                    (byte)Math.Clamp(pwm[k] * 100 / 255, 0, 100)));

            curves.Add(new AsusFanCurve(fan, points, enabled));
        }
        return curves;
    }

    private static bool ReadInt(List<Match> items, ref int i, out int value)
    {
        value = 0;
        if (i >= items.Count || !items[i].Groups[2].Success) return false;
        value = int.Parse(items[i++].Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool ReadByteArray(List<Match> items, ref int i, out List<int> values)
    {
        values = [];
        if (!ReadInt(items, ref i, out var length) || length < 0 || i + length > items.Count) return false;
        for (var n = 0; n < length; n++)
        {
            if (!ReadInt(items, ref i, out var v)) return false;
            values.Add(v);
        }
        return true;
    }

    // Strings, integers and booleans, IN ORDER. The D-Bus signature token ("a(sayayb)") matches none of the
    // three, so it never enters the stream; a curve's array lengths are plain integers and are read explicitly.
    [GeneratedRegex("\"([^\"]*)\"|(-?\\d+)|(true|false)")]
    private static partial Regex Token();
}

/// <summary>
/// The read half of the FanCurves interface: ask asusd for a profile's curves and hand back the parsed model.
/// Built over a delegated busctl runner so it is testable. Phase 3 adds the write half beside it
/// (<see cref="AsusdFanCurvesWriter"/>); the reader stays read-only and is still not wired into the app, because
/// the app has no surface for raw firmware curves yet.
/// </summary>
internal sealed class AsusdFanCurvesReader(Func<string[], (int code, string output)> busctl, string profileSignature)
{
    /// <summary>The curves asusd holds for <paramref name="profileToken"/> (a kernel platform-profile token), or
    /// false when the call failed or the reply was malformed.</summary>
    public bool TryRead(string profileToken, out IReadOnlyList<AsusFanCurve> curves)
    {
        curves = [];
        if (AsusdFanCurves.ProfileValue(profileSignature, profileToken) is not { } value) return false;

        var (code, output) = busctl(AsusdFanCurves.FanCurveDataArguments(profileSignature, value));
        if (code != 0) return false;
        if (AsusFanCurveValues.Parse(output) is not { } parsed) return false;

        curves = parsed;
        return true;
    }
}
