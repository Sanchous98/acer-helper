using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Infrastructure.Vendors.Dell;

/// <summary>
/// Dell laptop backend. Extends <see cref="GenericDevice"/> with what Dell firmware exposes beyond the
/// generic OS surface — battery charge modes (Adaptive / Express charge / Primarily AC / Standard / Custom),
/// USB PowerShare, Fn-lock, keyboard-backlight timeout and the full 4-mode thermal set (Optimized / Cool /
/// Quiet / UltraPerformance). Both OSes drive the SAME firmware knobs through different bindings:
/// Linux = the Dell kernel drivers' sysfs (dell-laptop/dell-wmi-ddv power_supply extension, dell-pc
/// platform_profile, dell-wmi-sysman firmware-attributes); Windows = Dell's agentless BIOS-attribute
/// ACPI-WMI (root\dcim\sysman\biosattributes, stock firmware on 2018+ business models — no Dell software
/// needed). Per-OS wiring lives in DellDevice.{Linux,Windows}.cs; unsupported surfaces simply stay generic.
/// See docs/dell-firmware.md.
/// </summary>
public sealed partial class DellDevice : GenericDevice
{
    public DellDevice(string? product) : base(status: null)
    {
        if (!string.IsNullOrWhiteSpace(product)) VendorName = product!;
        InitVendor();
    }

    partial void InitVendor();

    /// <summary>Display key for a charge-mode id. Handles BOTH bindings' ids: the kernel power_supply
    /// strings (Trickle/Fast/Standard/Adaptive/Custom) and the BIOS-attribute values
    /// (PrimAcUse/Express/Standard/Adaptive/Custom) — same five EC modes, two namings. The return value is a
    /// neutral localization key; the id itself stays the wire token.</summary>
    internal static string ChargeModeName(string id) => id switch
    {
        "Trickle" or "PrimAcUse" => "power.dell_primarily_ac",
        "Fast" or "Express"      => "bat.charge_express",
        "Standard"               => "bat.charge_standard",
        "Adaptive"               => "bat.charge_adaptive",
        "Custom"                 => "fan.mode_custom",
        _                        => id,
    };
}
