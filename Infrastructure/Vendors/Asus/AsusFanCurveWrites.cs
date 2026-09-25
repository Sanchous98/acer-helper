using System.Globalization;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The asusd FanCurves WRITE path — the EC-touching half Phase 2 deliberately left out.
//
// WHY A DEDICATED PORT AND NOT IFanControl. The app's IFanControl is mode/speed shaped (Auto / Max / Custom with
// one CPU and one GPU percentage) and its own curve is an EMULATION: Domain/FanCurveEngine.cs evaluates the
// user's anchors in software on every sensor tick and writes a single duty per fan. asusd's curve is a FIRMWARE
// artifact: eight temperature/pwm points per fan per platform profile, written once to the EC's
// pwmN_auto_point*_temp/_pwm and enabled with pwmN_enable, after which the firmware interpolates. There is no
// mode to report and no place for sixteen points in a two-slider custom speed, so binding either to the other
// would be a bad mapping. Phase 3 therefore exposes a dedicated writer and leaves IFanControl untouched.
//
// WRITING IS GENUINELY EC-TOUCHING, which is why every method here is consent-gated and single-flight. asusd's
// own write path (rog-profiles/src/fan_curve_set.rs write_to_device) writes every point and then pwmN_enable,
// and asusd re-applies the PPT group afterwards because the write resets the EC fan mode.
//
// THE WIRE SHAPE, from rog-dbus/src/zbus_fan_curves.rs + rog-profiles:
//   set_fan_curve(profile: PlatformProfile, curve: CurveData)      -> "<profile>(sayayb)"
//   set_fan_curves_enabled(profile, enabled: bool)                 -> "<profile>b"
//   set_profile_fan_curve_enabled(profile, fan: FanCurvePU, en:b)  -> "<profile>sb"
//   set_curves_to_defaults(profile)                                -> "<profile>"
//   CurveData = { fan: FanCurvePU(s), pwm: [u8;8](ay), temp: [u8;8](ay), enabled: bool } -> (sayayb)
//
// UNVERIFIED ON HARDWARE: no ASUS machine was available. The signatures, the value order and the validation are
// pinned by tests; the firmware's acceptance of a written curve is not measured.

/// <summary>The messages the fan-curve writes show, as localization keys.</summary>
internal static class AsusFanCurveMessages
{
    internal const string Warning =
        "This writes a fan curve to the firmware and replaces the current curve for this profile. It takes "
        + "effect immediately.";
    internal const string ResetWarning =
        "This resets this profile's fan curves to the machine's firmware defaults.";
    internal const string UnknownProfile = "That profile is not one of the machine's performance profiles.";
    internal const string UnknownFan = "This fan is not one of the machine's fans.";
    internal const string Unsupported = "This machine does not expose a fan curve for that fan.";
    internal const string EightPoints = "A fan curve must have exactly eight points.";
    internal const string Order = "A fan curve's temperatures must not decrease.";
    internal const string Range = "A fan curve's fan speeds must be between 0% and 100%.";
}

/// <summary>Pure curve validation and CurveData encoding — the one place the wire order is written down.</summary>
internal static class AsusFanCurveCodec
{
    internal const int PointCount = 8;

    /// <summary>The FanCurvePU wire word, or "" for a fan this table does not name (which validation refuses).</summary>
    internal static string WordFor(AsusFan fan) => fan switch
    {
        AsusFan.Cpu => "cpu",
        AsusFan.Gpu => "gpu",
        AsusFan.Mid => "mid",
        _           => "",
    };

    /// <summary>
    /// Refuse-over-guess validation. A curve reaches the EC only when it has asusd's exact eight points, its
    /// temperatures do not decrease (rog-profiles' own rule), its speeds are 0..100%, and its fan is one the
    /// wire names.
    /// </summary>
    internal static (bool ok, string? message) Validate(AsusFanCurve curve)
    {
        if (WordFor(curve.Fan).Length == 0) return (false, AsusFanCurveMessages.UnknownFan);
        if (curve.Points.Count != PointCount) return (false, AsusFanCurveMessages.EightPoints);

        byte lastTemp = 0;
        for (var i = 0; i < curve.Points.Count; i++)
        {
            if (curve.Points[i].Percent > 100) return (false, AsusFanCurveMessages.Range);
            if (i > 0 && curve.Points[i].TempC < lastTemp) return (false, AsusFanCurveMessages.Order);
            lastTemp = curve.Points[i].TempC;
        }
        return (true, null);
    }

    /// <summary>The FLATTENED CurveData fields: the fan word, the eight pwm bytes (their own length first, as
    /// busctl expects for an <c>ay</c>), the eight temperature bytes, and the enabled flag. The percentages are
    /// scaled to the raw 0..255 pwm the firmware takes — the same 0..100 ↔ 0..255 scale rog-profiles uses.</summary>
    internal static string[] ToValues(AsusFanCurve curve)
    {
        var count = PointCount.ToString(CultureInfo.InvariantCulture);
        var values = new List<string> { WordFor(curve.Fan), count };
        foreach (var point in curve.Points) values.Add(Pwm(point.Percent));
        values.Add(count);
        foreach (var point in curve.Points) values.Add(point.TempC.ToString(CultureInfo.InvariantCulture));
        values.Add(curve.Enabled ? "true" : "false");
        return [.. values];
    }

    private static string Pwm(byte percent)
        => Math.Round(percent * 255.0 / 100.0, MidpointRounding.AwayFromZero)
               .ToString(CultureInfo.InvariantCulture);
}

/// <summary>The exact method calls for the four FanCurves writes, with the profile's live wire signature
/// ("u" numeric on current asusd, "s" older).</summary>
internal static class AsusFanCurveCalls
{
    private const string Iface = AsusdFanCurves.Interface;

    internal static string[] SetFanCurve(string profileSignature, string profileValue, AsusFanCurve curve)
        => Asusd.CallArgumentsAt(Asusd.BasePath, Iface, "set_fan_curve",
                                 profileSignature + "(sayayb)",
                                 [profileValue, .. AsusFanCurveCodec.ToValues(curve)]);

    internal static string[] SetFanCurvesEnabled(string profileSignature, string profileValue, bool enabled)
        => Asusd.CallArgumentsAt(Asusd.BasePath, Iface, "set_fan_curves_enabled",
                                 profileSignature + "b",
                                 profileValue, Bool(enabled));

    internal static string[] SetProfileFanCurveEnabled(string profileSignature, string profileValue,
                                                       AsusFan fan, bool enabled)
        => Asusd.CallArgumentsAt(Asusd.BasePath, Iface, "set_profile_fan_curve_enabled",
                                 profileSignature + "sb",
                                 profileValue, AsusFanCurveCodec.WordFor(fan), Bool(enabled));

    internal static string[] SetCurvesToDefaults(string profileSignature, string profileValue)
        => Asusd.CallArgumentsAt(Asusd.BasePath, Iface, "set_curves_to_defaults",
                                 profileSignature, profileValue);

    private static string Bool(bool value) => value ? "true" : "false";
}

/// <summary>
/// The consent-gated, single-flight fan-curve writer. Every method validates first, resolves the profile's wire
/// value, asks for consent (with the warning key), and only then makes the one busctl call.
///
/// NOTHING HERE IS CALLED BY THE REFRESH LOOP: the writer has no periodic caller, and a curve write happens only
/// when a caller explicitly runs one of these methods.
/// </summary>
internal sealed class AsusdFanCurvesWriter(Func<string[], (int code, string output)> busctl,
                                           string profileSignature,
                                           AsusWriteConsent consent)
{
    private readonly object _gate = new();

    /// <summary>Write (and activate) one fan's curve for a profile.</summary>
    internal AsusWriteOutcome SetCurve(string profileToken, AsusFanCurve curve)
    {
        if (AsusFanCurveCodec.Validate(curve) is (false, { } refusal)) return AsusWriteOutcome.Refused(refusal);
        return Guarded(profileToken, AsusFanCurveMessages.Warning,
                       value => AsusFanCurveCalls.SetFanCurve(profileSignature, value, curve));
    }

    /// <summary>Enable or disable every stored curve for a profile.</summary>
    internal AsusWriteOutcome SetCurvesEnabled(string profileToken, bool enabled)
        => Guarded(profileToken, AsusFanCurveMessages.Warning,
                   value => AsusFanCurveCalls.SetFanCurvesEnabled(profileSignature, value, enabled));

    /// <summary>Enable or disable one fan's curve for a profile (FanCurvePU is a wire WORD, not a number).</summary>
    internal AsusWriteOutcome SetProfileFanCurveEnabled(string profileToken, AsusFan fan, bool enabled)
    {
        if (AsusFanCurveCodec.WordFor(fan).Length == 0) return AsusWriteOutcome.Refused(AsusFanCurveMessages.UnknownFan);
        return Guarded(profileToken, AsusFanCurveMessages.Warning,
                       value => AsusFanCurveCalls.SetProfileFanCurveEnabled(profileSignature, value, fan, enabled));
    }

    /// <summary>Reset a profile's stored and device curves to the firmware defaults.</summary>
    internal AsusWriteOutcome ResetToDefaults(string profileToken)
        => Guarded(profileToken, AsusFanCurveMessages.ResetWarning,
                   value => AsusFanCurveCalls.SetCurvesToDefaults(profileSignature, value));

    /// <summary>The shared tail: resolve the profile's wire value, then consent + one call under the single-flight
    /// lock. The lock spans the consent so two writes cannot interleave even at the prompt.</summary>
    private AsusWriteOutcome Guarded(string profileToken, string warning, Func<string, string[]> call)
    {
        if (AsusdFanCurves.ProfileValue(profileSignature, profileToken) is not { } value)
            return AsusWriteOutcome.Refused(AsusFanCurveMessages.UnknownProfile);

        lock (_gate)
        {
            if (!consent(warning)) return AsusWriteOutcome.Refused(AsusArmouryMessages.Cancelled);
            var (code, output) = busctl(call(value));
            return code == 0
                ? AsusWriteOutcome.Applied()
                : AsusWriteOutcome.Refused(Asusd.Describe(code, output));
        }
    }
}
