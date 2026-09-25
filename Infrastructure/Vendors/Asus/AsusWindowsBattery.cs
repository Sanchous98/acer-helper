using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// ASUS Windows battery charge limit and panel overdrive, over the ATK device (AsusAtk.cs carries the sources).
//
// CHARGE LIMIT. `ASUS_WMI_DEVID_RSOC` (0x00120057) takes a percentage; the app's battery port is the on/off
// toggle the rest of the app already uses, so the SAME mapping the Linux/asusd path uses applies here (ON = the
// 80 % health cap, OFF = 100 %) — one rule, `AsusdChargeLimitRules`, shared by both OSes. Note the firmware has
// no reliable read-back (the Linux driver keeps the value in memory too), so the read is best-effort and an
// unreadable value is reported as "off" rather than as a fake cap.
//
// PANEL OVERDRIVE. `ASUS_WMI_DEVID_PANEL_OD` (0x00050019), offered only when its support device
// (`ASUS_WMI_DEVID_PANEL_OD` sibling 0x00050020) reports 1 (G-Helper's `IsOverdriveSupported`). It is a plain
// declared on/off setting, so it reuses the generic FlagPort.
//
// UNVERIFIED ON HARDWARE (see AsusAtk.cs).

/// <summary>The ATK-backed 80 % charge cap as the app's battery toggle.</summary>
internal static class AsusWindowsChargeLimit
{
    internal static BatteryToggle? TryCreate(AsusAtkDevice atk)
    {
        if (!atk.Supported(AsusAtk.BatteryRsoc)) return null;

        return new BatteryToggle(
            Read: () =>
            {
                var value = atk.Get(AsusAtk.BatteryRsoc);
                return value >= 0 && AsusdChargeLimitRules.IsOn(value);
            },
            Write: on =>
            {
                var result = atk.Set(AsusAtk.BatteryRsoc, AsusdChargeLimitRules.PercentFor(on));
                return result == 1
                    ? (true, null)
                    : (false, result < 0 ? "the machine did not answer the charge-limit write"
                                         : $"the firmware refused the charge-limit write (status {result})");
            });
    }
}

/// <summary>The ATK-backed panel-overdrive flag, or null when the machine does not report support.</summary>
internal static class AsusWindowsPanelOverdrive
{
    internal const string SettingKey = "panel_od";

    internal static IFlagPort? TryCreate(AsusAtkDevice atk)
    {
        if (atk.Get(AsusAtk.PanelOverdriveSupported) != 1) return null;

        return new FlagPort(
            read: () => atk.Get(AsusAtk.PanelOverdrive) == 1,
            write: on =>
            {
                var result = atk.Set(AsusAtk.PanelOverdrive, on ? 1 : 0);
                return result == 1
                    ? (true, null)
                    : (false, result < 0 ? "the machine did not answer the panel-overdrive write"
                                         : $"the firmware refused the panel-overdrive write (status {result})");
            });
    }
}
