using AcerHelper.Os;
using AcerHelper.Vendors.Acer;

namespace AcerHelper.Composition;

/// <summary>
/// Detects the laptop on Windows and assembles an <see cref="IDevice"/> with the ports it
/// supports (null = unsupported → the UI hides that section). Adding a vendor = another branch
/// here plus its codecs. The Linux counterpart is <c>DeviceFactory.Linux.cs</c>.
/// </summary>
public static class DeviceFactory
{
    public static IDevice Create()
    {
        // Vendor detection: the Acer gaming WMI class is present only on Acer gaming models
        // (and only readable when elevated).
        var gaming = new WmiInvoker("AcerGamingFunction");
        if (!gaming.Available)
        {
            gaming.Dispose();
            return new CompositeDevice
            {
                StatusMessage = gaming.LastError ?? "Acer WMI unavailable — run as Administrator.",
                // Blue-light (gamma) and autostart work on any Windows box, so still offer them.
                DisplayTint = new DisplayTint(),
                Autostart = new Autostart(),
            };
        }

        var battery   = new WmiInvoker("BatteryControl");
        var apge      = new WmiInvoker("APGeAction");
        var lighting  = new AcerLighting();
        var hotkeys   = new AcerHotkeys();
        var clamshell = new Clamshell();
        var usb       = new AcerUsbCharging(apge);
        var backlight = new AcerKeyboardBacklight(apge);

        return new CompositeDevice
        {
            VendorName = "Acer",
            PowerProfiles = new AcerPowerProfiles(gaming),
            FanControl    = new AcerFanControl(gaming),
            Sensors       = new AcerSensors(gaming),
            LcdOverdrive  = new AcerLcdOverdrive(gaming),
            BatteryChargeLimit = battery.Available ? new AcerBatteryChargeLimit(battery) : null,
            BatteryCalibration = battery.Available ? new AcerBatteryCalibration(battery) : null,
            UsbCharging       = usb.Supported ? usb : null,
            KeyboardBacklight = backlight.Supported ? backlight : null,
            Lighting          = lighting.Available ? lighting : null,
            Hotkeys           = hotkeys,
            DisplayTint       = new DisplayTint(),
            Autostart         = new Autostart(),
            Clamshell         = clamshell.Supported ? clamshell : null,
            Owned = [gaming, battery, apge, lighting, hotkeys, clamshell],
        };
    }
}
