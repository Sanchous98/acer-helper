using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Dell;

/// <summary>
/// Dell laptop backend. Extends <see cref="GenericDevice"/> with what Dell firmware exposes beyond the
/// generic OS surface — battery charge modes (Adaptive / Express charge / Primarily AC / Standard / Custom),
/// USB PowerShare, Fn-lock, keyboard-backlight timeout and the full 4-mode thermal set (Optimized / Cool /
/// Quiet / UltraPerformance). Both OSes drive the SAME firmware knobs through different bindings:
/// Linux = the Dell kernel drivers' sysfs (dell-laptop/dell-wmi-ddv power_supply extension, dell-pc
/// platform_profile, dell-wmi-sysman firmware-attributes); Windows = Dell's agentless BIOS-attribute
/// ACPI-WMI (root\dcim\sysman\biosattributes, stock firmware on 2018+ business models — no Dell software
/// needed). Per-OS wiring lives in DellDevice.{Linux,Windows}.cs; unsupported surfaces simply stay generic.
/// </summary>
public sealed partial class DellDevice : GenericDevice
{
    public DellDevice(string? product) : base(status: null)
    {
        if (!string.IsNullOrWhiteSpace(product)) VendorName = product!;
        InitVendor();
    }

    partial void InitVendor();

    /// <summary>Display name for a charge-mode id. Handles BOTH bindings' ids: the kernel power_supply
    /// strings (Trickle/Fast/Standard/Adaptive/Custom) and the BIOS-attribute values
    /// (PrimAcUse/Express/Standard/Adaptive/Custom) — same five EC modes, two namings.</summary>
    internal static string ChargeModeName(string id) => id switch
    {
        "Trickle" or "PrimAcUse" => "Primarily AC use",
        "Fast" or "Express"      => "Express charge",
        "Standard"               => "Standard",
        "Adaptive"               => "Adaptive",
        "Custom"                 => "Custom",
        _                        => id,
    };
}
