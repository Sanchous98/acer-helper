using AcerHelper.Features;
using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Dell;

// Linux: wire the Dell kernel drivers' sysfs to the generic holders (DelegatePorts.cs). Verified on a
// Latitude 5540 / kernel 7.0: battery charge modes via the power_supply extension (dell-laptop /
// dell-wmi-ddv, kernel 6.12+), thermal via the dell-pc platform_profile, Fn-lock + USB PowerShare via
// dell-wmi-sysman firmware-attributes (keyboard backlight BRIGHTNESS is generic — SysfsKbdBacklight). Reads
// work as the user (except firmware-attributes — root-only even to read, hence gated on readability); writes
// need root or a udev rule, and are additionally probed/gated because the firmware refuses some of them
// (BIOS-admin-password attrs, or the read-only dell-wmi-ddv charge path). Anything absent stays generic.
public sealed partial class DellDevice
{
    private const string Bat = "/sys/class/power_supply/BAT0";       // dell-laptop supports BAT0 only
    private const string KbdLed = "/sys/class/leds/dell::kbd_backlight";

    // Auto-off delays the LED stop_timeout accepts (verified on the 5540; ids == the strings the node
    // reads back, so the current value maps straight to a dropdown entry). "never"/"0" is rejected (-ENXIO)
    // on this hardware, so there's no disable option.
    private static readonly ChoiceOption[] KbdTimeouts =
    [
        new("5s", "5 s"), new("10s", "10 s"), new("30s", "30 s"),
        new("1m", "1 min"), new("5m", "5 min"), new("15m", "15 min"), new("1h", "1 h"),
    ];

    private SysfsInvoker _bat = null!;
    private SysfsInvoker _kbd = null!;
    private FirmwareAttributes? _fw;

    partial void InitVendor()
    {
        // Every Dell sysfs knob below is root-writable only, so each is offered ONLY when this process can
        // actually operate it — a control that fails "permission denied" on every use is worse than none
        // (same rule as the generic SysfsChargeLimit). Found-but-locked knobs set the status hint instead.
        var locked = false;

        // Thermal: dell-pc platform_profile ("cool quiet balanced performance") is the full Dell USTT set —
        // richer than the 3-profile PPD collapse, so prefer it, but only when switchable; otherwise KEEP the
        // generic polkit-authorised PPD port, which works unprivileged.
        var profiles = new SysfsPowerProfiles();
        if (profiles.Available)
        {
            if (profiles.Writable) PowerProfiles = profiles;
            else locked = true;
        }

        // Battery charge modes (power_supply charge_types = "Trickle Fast Standard [Adaptive] Custom": active
        // mode in brackets, only BIOS-supported modes listed). On some models this is the READ-ONLY
        // dell-wmi-ddv extension, which rejects writes with -EIO even with write permission — so PROBE
        // writability by idempotently re-applying the CURRENT mode (no behavioural change) and offer the
        // control only if that write actually takes. Either way drop the generic 80% end-threshold toggle:
        // Dell charge control is the named-mode selector (and the threshold is honoured only in Custom mode).
        _bat = new SysfsInvoker(Bat);
        var modes = ParseChargeTypes(_bat.Read("charge_types"));
        if (modes.Count > 0)
        {
            BatteryChargeLimit = null;
            var cur = GetChargeMode();
            if (_bat.CanWrite("charge_types") && cur != null && SetChargeMode(cur) is (true, _))
                BatteryChargeMode = new ChoicePort(modes, GetChargeMode, SetChargeMode);
            else
                locked = true;
        }

        // Keyboard-backlight AUTO-OFF DELAY: the LED's stop_timeout is a DURATION from a fixed firmware set
        // (not a bool — "never"/"0" is rejected -ENXIO here), so it's a duration DROPDOWN. Backlight
        // BRIGHTNESS itself is generic (kernel LED class — see SysfsKbdBacklight), wired by GenericDevice.
        _kbd = new SysfsInvoker(KbdLed);
        if (_kbd.Has("stop_timeout"))
        {
            if (_kbd.CanWrite("stop_timeout"))
                KeyboardBacklightTimeout = new ChoicePort(KbdTimeouts, GetKbdTimeout, SetKbdTimeout);
            else locked = true;
        }

        // BIOS settings via dell-wmi-sysman. When a BIOS ADMIN PASSWORD is set the firmware refuses attribute
        // writes unless the password is supplied first (we don't) — the write fails -EOPNOTSUPP — so offer
        // these ONLY when no password gates them, rather than a control that always fails. (current_value is
        // also root-only to read, hence the CanRead check; the udev/tmpfiles rules relax both.)
        _fw = FirmwareAttributes.TryCreate("dell-wmi-sysman");
        if (_fw is { RequiresPassword: true })
            locked = true;
        else if (_fw != null)
        {
            if (_fw.CanRead("FnLock")) FnLock = new FlagPort(GetFnLock, SetFnLock);
            if (_fw.CanRead("UsbPowerShare"))
                UsbCharging = new ChoicePort([new ChoiceOption("Disabled", "Off"), new ChoiceOption("Enabled", "On")],
                                             GetPowerShare, SetPowerShare);
        }

        if (locked)
            StatusMessage ??= "Some Dell controls are locked by the firmware (BIOS admin password, or the model rejects writes) and were hidden.";
    }

    // ---- battery charge modes (power_supply charge_types) ----
    private static List<ChoiceOption> ParseChargeTypes(string? raw)
        => (raw ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                      .Select(t => t.Trim('[', ']'))
                      .Select(id => new ChoiceOption(id, ChargeModeName(id)))
                      .ToList();

    private string? GetChargeMode()
        => (_bat.Read("charge_types") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                            .FirstOrDefault(t => t.StartsWith('['))?.Trim('[', ']');

    private (bool, string?) SetChargeMode(string id)
    {
        var ok = _bat.Write("charge_types", id, out var e);
        return (ok, e);
    }

    // ---- keyboard-backlight auto-off delay (LED stop_timeout; reads/writes "5s","1m","1h",…) ----
    private string? GetKbdTimeout() => _kbd.Read("stop_timeout");

    private (bool, string?) SetKbdTimeout(string id)
    {
        var ok = _kbd.Write("stop_timeout", id, out var e);
        return (ok, e);
    }

    // ---- BIOS attributes (dell-wmi-sysman) ----
    private bool GetFnLock() => _fw?.Read("FnLock") == "Enabled";

    private (bool, string?) SetFnLock(bool on) => WriteAttr("FnLock", on ? "Enabled" : "Disabled");

    private string? GetPowerShare() => _fw?.Read("UsbPowerShare");

    private (bool, string?) SetPowerShare(string id) => WriteAttr("UsbPowerShare", id);

    private (bool, string?) WriteAttr(string attribute, string value)
    {
        if (_fw == null) return (false, null);
        var ok = _fw.Write(attribute, value, out var e);
        return (ok, e);
    }
}
