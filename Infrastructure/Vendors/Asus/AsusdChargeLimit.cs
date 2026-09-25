using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// The ASUS battery charge cap over asusd's `xyz.ljones.Platform.charge_control_end_threshold` — a PERCENTAGE
// (asusd refuses anything outside 20..=100), whereas the app's charge-limit port (Domain/Battery.cs,
// BatteryToggle) is an on/off property. The mapping is therefore the same one the generic sysfs limiter uses,
// stated once here so the two sources agree:
//
//   ON  = cap at 80%   (the battery-health level the generic SysfsChargeLimit writes)
//   OFF = 100%
//
// WHY THE PORT AND NOT A PERCENTAGE CHOICE. The task is parity of the EXISTING battery surface: `BatteryToggle`
// is what `Battery.ChargeLimit` is and what the Battery view already renders, on every vendor. Modelling asusd's
// 20..100 range as the app's on/off limit keeps `settings.json` (a bool) and the UI identical whether the cap
// is reached through asusd or through sysfs — which is the property that matters, because the two can serve the
// same machine on different boots.
//
// THE I/O IS INJECTED, like every other vendor port here: the busctl runner is a delegate, so the parse, the
// threshold→bool mapping and the argument lists are all reachable by a test without asusd. The real port is
// built by AsusdPlatform.Linux.cs.

/// <summary>
/// The pure rules of the asusd charge cap: what a payload means and which percentage a toggle writes.
/// </summary>
internal static class AsusdChargeLimitRules
{
    /// <summary>The percentage the app's "on" writes — the same 80 the generic sysfs limiter caps at.</summary>
    internal const int LimitPercent = 80;

    /// <summary>The percentage "off" writes, i.e. no cap.</summary>
    internal const int FullPercent = 100;

    /// <summary>The threshold in a <c>charge_control_end_threshold</c> payload (<c>y</c> or <c>u</c>), or null
    /// when there is no number to read. Deliberately NOT clamped here: a value outside asusd's 20..100 is the
    /// machine's own fact, and it is <see cref="IsOn"/> that decides what it means.</summary>
    internal static int? Parse(string output) => AsusdValues.ParseThreshold(output);

    /// <summary>Whether a threshold reads as "the cap is on". Anything below 100 is a cap; 100 (or an unreadable
    /// 0) is off. The 0 case is defensive — asusd never writes it — and treats it as off rather than as "cap at
    /// 0%", which would be a battery the machine refuses to charge at all.</summary>
    internal static bool IsOn(int threshold) => threshold is > 0 and < 100;

    /// <summary>The percentage a toggle writes: the health cap when on, 100 when off.</summary>
    internal static int PercentFor(bool on) => on ? LimitPercent : FullPercent;
}

/// <summary>
/// The asusd-backed charge limiter. <see cref="TryCreate"/> returns null — leaving the generic sysfs limiter the
/// base device wired in place — unless the property is readable, so a daemon without the capability is invisible
/// here rather than a switch whose every write fails.
/// </summary>
internal static class AsusdChargeLimit
{
    internal static BatteryToggle? TryCreate(Func<string[], (int code, string output)> busctl)
    {
        var (code, output) = busctl(Asusd.GetPropertyArguments(
            Asusd.PlatformInterface, Asusd.ChargeControlEndThresholdProperty));
        if (code != 0 || AsusdChargeLimitRules.Parse(output) is not { }) return null;

        // The signature the live daemon reports ("y" for u8 on current asusd, "u" on some builds); the write is
        // spelled with the same one so the value is accepted.
        var signature = Asusd.SignatureOf(output);
        if (signature.Length == 0) signature = "y";

        return new BatteryToggle(
            Read: () =>
            {
                var (c, o) = busctl(Asusd.GetPropertyArguments(
                    Asusd.PlatformInterface, Asusd.ChargeControlEndThresholdProperty));
                return c == 0 && AsusdChargeLimitRules.Parse(o) is { } v && AsusdChargeLimitRules.IsOn(v);
            },
            Write: on =>
            {
                var (c, o) = busctl(Asusd.SetPropertyArguments(
                    Asusd.PlatformInterface, Asusd.ChargeControlEndThresholdProperty, signature,
                    AsusdChargeLimitRules.PercentFor(on).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return c == 0 ? (true, null) : (false, Asusd.Describe(c, o));
            });
    }
}

/// <summary>
/// asusd's <c>one_shot_full_charge()</c> — charge to 100 % for ONE cycle, then restore the stored cap (asusd
/// swaps in 100, remembers the prior threshold in <c>base_charge_control_end_threshold</c>, and restores it on
/// the next unplug). It is a COMMAND, not a state.
///
/// DELIBERATELY NOT MAPPED TO <c>Battery.Calibration</c>, and this class carries the reason rather than leaving
/// it to a comment: calibration is a full discharge-then-recharge CYCLE with a latched on/off state the firmware
/// holds (<c>BatteryToggle</c>, as Acer's WMI calibration mode is), whereas this is a one-shot action with no
/// state to read back and nothing to turn off. Binding a momentary command to a toggle would show a switch that
/// snaps back and can never be "on", which is the bad mapping this avoids. It is also not the charge limit: the
/// limit is a persistent cap, this temporarily lifts it.
///
/// So Phase 2 implements the call and its exact argument list, and leaves the owner decision about WHERE it
/// belongs (a one-shot action row, a battery menu item) for when there is somewhere to put it. See
/// docs/asus-support.md.
/// </summary>
internal static class AsusdOneShotCharge
{
    internal const string Method = "one_shot_full_charge";

    internal static string[] Arguments() => Asusd.CallArguments(Asusd.PlatformInterface, Method);

    /// <summary>Ask asusd for one full charge. Returns the daemon's own refusal reason on failure.</summary>
    internal static (bool ok, string? error) Run(Func<string[], (int code, string output)> busctl)
    {
        var (code, output) = busctl(Arguments());
        return code == 0 ? (true, null) : (false, Asusd.Describe(code, output));
    }
}
