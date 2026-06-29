using AcerHelper.Os;
using AcerHelper.Vendors.Acer;

namespace AcerHelper.Composition;

/// <summary>
/// Detects the laptop on Windows and assembles an <see cref="IDevice"/> with the ports it
/// supports (null = unsupported → the UI hides that section). Vendor detection is the Acer gaming
/// WMI class; within Acer, the specific model (profiles, fan count, RGB layout) is selected from
/// the DMI product name. Adding a vendor = another branch here plus its codecs.
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
            string? status = gaming.LastError;
            gaming.Dispose();
            return BuildGeneric(status);
        }

        // Within Acer, only the un-probeable bits (display name + RGB layout) come from the model
        // quirks config; everything else below is probed.
        var (_, product) = MachineInfo.Read();
        var model = AcerModels.Detect(product);

        var battery   = new WmiInvoker("BatteryControl");
        var apge      = new WmiInvoker("APGeAction");
        var hotkeys   = new AcerHotkeys();
        var clamshell = new Clamshell();
        var usb       = new AcerUsbCharging(apge);
        var backlight = new AcerKeyboardBacklight(apge);
        var lighting  = new AcerLighting(model.Zones, model.Lightbar);

        return new CompositeDevice
        {
            VendorName    = model.Name,
            PowerProfiles = new AcerPowerProfiles(gaming),
            FanControl    = new AcerFanControl(gaming),
            Sensors       = new AcerSensors(gaming),
            LcdOverdrive  = new AcerLcdOverdrive(gaming),
            BatteryChargeLimit = battery.Available ? new AcerBatteryChargeLimit(battery) : null,
            BatteryCalibration = battery.Available ? new AcerBatteryCalibration(battery) : null,
            UsbCharging       = usb.Supported ? usb : null,
            KeyboardBacklight = backlight.Supported ? backlight : null,
            Lighting          = lighting.Available ? lighting : null,   // RGB presence is probed
            Hotkeys           = hotkeys,
            DisplayTint       = new DisplayTint(),
            Autostart         = new Autostart(),
            Clamshell         = clamshell.Supported ? clamshell : null,
            Owned = [gaming, battery, apge, lighting, hotkeys, clamshell],
        };
    }

    /// <summary>Generic Windows device (not an Acer gaming model, or WMI not accessible): the basics
    /// any laptop has — OS power-mode profiles, blue-light, autostart, clamshell.</summary>
    private static IDevice BuildGeneric(string? status)
    {
        var profiles = new OverlayPowerProfiles();
        var clamshell = new Clamshell();
        return new CompositeDevice
        {
            VendorName    = "Generic",
            StatusMessage = status,
            PowerProfiles = profiles.Available ? profiles : null,
            DisplayTint   = new DisplayTint(),
            Autostart     = new Autostart(),
            Clamshell     = clamshell.Supported ? clamshell : null,
            Owned = [clamshell],
        };
    }
}

