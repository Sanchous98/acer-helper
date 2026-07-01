using AcerHelper.Features;
using AcerHelper.Os;
using AcerHelper.Vendors.Acer;

namespace AcerHelper.Composition;

// Windows vendor detection: the Acer gaming WMI class is present only on Acer gaming models (and
// only readable when elevated). Within Acer, only the un-probeable bits (display name + RGB layout)
// come from the model quirks config; every other port below is probed. Adding a vendor = another
// branch here plus its codecs.
public static partial class DeviceFactory
{
    private static partial (IDevice? Device, string? Reason) CreateVendorDevice()
    {
        var gaming = new WmiInvoker("AcerGamingFunction");
        if (!gaming.Available)
        {
            string? reason = gaming.LastError;
            gaming.Dispose();
            return (null, reason);   // not an Acer gaming model (or WMI not accessible) -> generic
        }

        var (_, product) = MachineInfo.Read();
        var model = AcerModels.Detect(product);

        var battery   = new WmiInvoker("BatteryControl");
        // Probe which battery modes the firmware actually advertises (uFunctionList bits), so the toggles
        // appear only when they'd really do something — not merely because the BatteryControl class exists.
        var batModes  = battery.Available ? BatteryWmi.ReadStatus(battery, out _) : null;
        var apge      = new WmiInvoker("APGeAction");
        var hotkeys   = new AcerHotkeys();
        var clamshell = new Clamshell();
        var usb       = new AcerUsbCharging(apge);
        var backlight = new AcerKeyboardBacklight(apge);
        var lighting  = new AcerLighting(model.Zones, model.Lightbar, gaming);   // gaming WMI = brightness read-back

        var device = new CompositeDevice
        {
            VendorName    = model.Name,
            PowerProfiles = new AcerPowerProfiles(gaming),
            FanControl    = new AcerFanControl(gaming),
            Sensors       = new AcerSensors(gaming),
            LcdOverdrive  = new AcerLcdOverdrive(gaming),
            BatteryInfo        = BatteryInfo.TryCreate(),
            BatteryChargeLimit = batModes?.HealthAvail == true ? new AcerBatteryChargeLimit(battery) : null,
            BatteryCalibration = batModes?.CalibAvail == true ? new AcerBatteryCalibration(battery) : null,
            UsbCharging       = usb.Supported ? usb : null,
            KeyboardBacklight = backlight.Supported ? backlight : null,
            Lighting          = lighting.Available ? lighting : null,   // RGB presence is probed
            Hotkeys           = hotkeys,
            DisplayTint       = new DisplayTint(),
            Autostart         = new Autostart(),
            Clamshell         = clamshell.Supported ? clamshell : null,
            Owned = [gaming, battery, apge, lighting, hotkeys, clamshell],
        };
        return (device, null);
    }
}

