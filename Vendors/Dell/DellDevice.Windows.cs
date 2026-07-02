using AcerHelper.Features;
using AcerHelper.Vendors.Generic;

namespace AcerHelper.Vendors.Dell;

// Windows: wire Dell's agentless BIOS-attribute WMI (DellBiosWmi) to the generic holders — the same EC
// knobs the Linux partial reaches through sysfs, one binding per OS. Thermal Management IS Dell's
// performance-profile set (the USTT knob dell-pc/platform_profile exposes on Linux), so it overrides the
// generic Windows power-overlay profiles; battery charge modes, Fn-lock and USB PowerShare are attributes
// too. Every feature is gated on its attribute actually existing on this model. Written from the
// documented interface (Dell agentless-manageability whitepaper) but NOT yet verified on Dell-Windows
// hardware — if the namespace is absent (pre-2018/consumer) everything stays generic.
public sealed partial class DellDevice
{
    // BIOS ThermalManagement values; Kind/accent align with the Linux platform_profile mapping
    // (quiet/cool/balanced/performance) so the UI looks the same on both OSes.
    private static readonly PerformanceProfile[] Thermal =
    [
        new("Quiet",            "Quiet",       ProfileKind.Quiet,       new AccentColor(0x42, 0x85, 0xF4)),
        new("Cool",             "Cool",        ProfileKind.Quiet,       new AccentColor(0x00, 0x89, 0x7B)),
        new("Optimized",        "Optimized",   ProfileKind.Balanced,    new AccentColor(0x2E, 0x7D, 0x32)),
        new("UltraPerformance", "Performance", ProfileKind.Performance, new AccentColor(0xD3, 0x2F, 0x2F)),
    ];

    // PrimaryBattChargeCfg values (the BIOS-attribute naming of the same five EC modes as Linux's
    // charge_types). The standard Dell set; a value this model lacks fails its write and reads back.
    private static readonly ChoiceOption[] ChargeModes =
        [.. new[] { "Adaptive", "Standard", "Express", "PrimAcUse", "Custom" }
              .Select(id => new ChoiceOption(id, ChargeModeName(id)))];

    // KeyboardIllumination enumeration values, indexed as brightness levels 0..2 (Off/Dim/Bright).
    private static readonly string[] Illum = ["Disabled", "Dim", "Bright"];

    private DellBiosWmi _bios = null!;

    partial void InitVendor()
    {
        var bios = new DellBiosWmi();
        if (!bios.Available) return;   // no Dell firmware WMI on this model -> keep the generic ports
        _bios = bios;

        // A BIOS admin password gates ALL attribute writes (firmware refuses them without it) -> hide the
        // Dell BIOS controls and keep the generic ports (e.g. the Windows power-mode overlay for profiles),
        // rather than offer controls that always fail Access Denied. Mirrors the Linux RequiresPassword gate.
        if (bios.AdminPasswordSet())
        {
            StatusMessage = "Dell BIOS controls are locked by a BIOS admin password and were hidden.";
            return;
        }

        if (bios.Get("ThermalManagement") != null)
            PowerProfiles = new ProfilesPort(Thermal, () => Thermal, CurrentThermal, SetThermal);
        if (bios.Get("PrimaryBattChargeCfg") != null)
            BatteryChargeMode = new ChoicePort(ChargeModes, GetChargeMode, SetChargeMode);
        if (bios.Get("FnLock") != null)
            FnLock = new FlagPort(GetFnLock, SetFnLock);
        if (bios.Get("UsbPowerShare") != null)
            UsbCharging = new ChoicePort([new("Disabled", "Off"), new("Enabled", "On")],
                                         GetPowerShare, SetPowerShare);
        // Plain keyboard backlight (Disabled/Dim/Bright) as discrete brightness levels — shown in the
        // Lighting window, not Options. (On Linux the same knob is the kernel LED class, wired generically.)
        if (bios.Get("KeyboardIllumination") != null)
            KeyboardBrightness = new LevelPort(Illum.Length - 1, GetKbdBright, SetKbdBright);
    }

    // ---- thermal (ThermalManagement attribute = the USTT knob) ----
    private PerformanceProfile? CurrentThermal()
    {
        var v = _bios.Get("ThermalManagement");
        return Thermal.FirstOrDefault(p => p.Id == v);
    }

    private (bool, string?) SetThermal(PerformanceProfile p) => _bios.Set("ThermalManagement", p.Id);

    // ---- battery charge mode / Fn-lock / USB PowerShare ----
    private string? GetChargeMode() => _bios.Get("PrimaryBattChargeCfg");

    private (bool, string?) SetChargeMode(string id) => _bios.Set("PrimaryBattChargeCfg", id);

    private bool GetFnLock() => _bios.Get("FnLock") == "Enabled";

    private (bool, string?) SetFnLock(bool on) => _bios.Set("FnLock", on ? "Enabled" : "Disabled");

    private string? GetPowerShare() => _bios.Get("UsbPowerShare");

    private (bool, string?) SetPowerShare(string id) => _bios.Set("UsbPowerShare", id);

    private int GetKbdBright() { var i = Array.IndexOf(Illum, _bios.Get("KeyboardIllumination")); return i < 0 ? 0 : i; }

    private (bool, string?) SetKbdBright(int level) => _bios.Set("KeyboardIllumination", Illum[Math.Clamp(level, 0, Illum.Length - 1)]);
}
