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
}
