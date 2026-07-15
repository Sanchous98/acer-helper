using AcerHelper.Features;
using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Acer;

/// <summary>
/// The Acer device: extends <see cref="GenericDevice"/> and relies on it for the common OS-level ports
/// (battery telemetry, autostart, and on Windows blue-light/clamshell). DeviceFactory constructs it by DMI
/// manufacturer; from there this class owns ALL Acer creation — transports + per-feature probing + wiring —
/// in <see cref="InitVendor"/> (per-OS). It only overrides the generic profiles/sensors where Acer's are
/// richer, and adds the proprietary ports; anything whose transport/node isn't available stays null so the
/// generic behaviour shows through (rely on Generic where possible).
/// </summary>
public sealed partial class AcerDevice : GenericDevice
{
    private readonly AcerModel _model;

    public AcerDevice(string? product) : base(status: null)
    {
        _model = AcerModels.Detect(product);
        VendorName = _model.Name;
        InitVendor();
    }

    /// <summary>Per-OS: create the Acer transports and wire the proprietary ports (WMI on Windows, Linuwu
    /// sysfs on Linux). See AcerDevice.Windows.cs / AcerDevice.Linux.cs.</summary>
    partial void InitVendor();

    // USB-charging levels (ids = battery-threshold percentages, "0" = off). OS-agnostic — the same choices
    // on both backends — so it lives here rather than duplicated in each InitVendor.
    private static readonly ChoiceOption[] UsbLevels =
        [new("0", "Off"), new("10", "10%"), new("20", "20%"), new("30", "30%")];

    /// <summary>Assemble the RGB device from the ENE HID controller and adopt it if it has any zones. The
    /// controller is the same brick on both OSes (same device + packets); only the keyboard-brightness
    /// read-back differs (gaming WMI on Windows, none on Linux — it's a write-only HID device), so the
    /// per-OS InitVendor passes that delegate in. Kept here so a change to the adopt/dispose handling is
    /// made once.</summary>
    private void WireRgb(Func<int?>? readKeyboardBrightness)
    {
        var rgb = new EneHidController(_model.Zones, _model.Lightbar, readKeyboardBrightness);
        if (rgb.Zones.Count > 0) { var dev = new RgbDevice(rgb); Lighting = dev; Own(dev); }
        else rgb.Dispose();
    }
}
