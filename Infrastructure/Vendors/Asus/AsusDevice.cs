using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Asus;

/// <summary>
/// The ASUS laptop backend. It extends <see cref="GenericDevice"/> and relies on it for every common OS-level
/// port (battery telemetry, autostart, and — where the OS exposes them — profiles, sensors, clamshell and
/// blue-light); this class owns ONLY what ASUS firmware exposes beyond that surface, and even then it defers to
/// the generic ports wherever a vendor interface is absent.
///
/// PHASE 1 IS LINUX-AND-ASUSD ONLY. On Linux the vendor integration target is the asusd/asusctl stack
/// (<c>xyz.ljones.Asusd</c>); when asusd is not on the bus the machine reads exactly as the generic backend
/// leaves it — power-profiles-daemon or the raw <c>platform_profile</c> sysfs node for profiles, and the standard
/// <c>charge_control_end_threshold</c> node for the battery cap. On Windows this class currently adds nothing
/// (the WMI/ASUS-Armoury surface is a later phase), so an ASUS laptop there behaves like the generic backend.
/// Per-OS wiring lives in AsusDevice.{Linux,Windows}.cs; the plan and the Acer↔ASUS parity map are in
/// docs/asus-support.md.
/// </summary>
public sealed partial class AsusDevice : GenericDevice
{
    /// <summary>The DMI product name this machine reported, for the per-model quirks lookup (AsusModels).</summary>
    internal string? Product { get; }

    public AsusDevice(string? product) : base(status: null)
    {
        Product = product;
        if (!string.IsNullOrWhiteSpace(product)) VendorName = product!;
        InitVendor();
    }

    /// <summary>Per-OS: create the ASUS transports and wire the vendor ports. See AsusDevice.Windows.cs /
    /// AsusDevice.Linux.cs.</summary>
    partial void InitVendor();
}
