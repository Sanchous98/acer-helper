using System.Runtime.InteropServices;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// CPU power management via the Windows Power-Mode overlay (the taskbar battery-slider modes: Best power
/// efficiency / Balanced / Best performance), driven by <c>powrprof.dll</c>'s <c>PowerSetActiveOverlayScheme</c>.
/// This is the one CPU-power knob available driverless on this class of machine: real PPT/STAPM/TDP and
/// Curve-Optimizer undervolt are all ring-0 (RyzenAdj/SMU) and this app ships no kernel driver, and Acer — unlike
/// ASUS — exposes NO WMI/ACPI CPU-power setter (it bakes the whole envelope into its fixed EC profiles). So,
/// mirroring G-Helper's driverless CPU axis, the app maps an OS power mode to each performance profile.
///
/// The overlay is an axis ORTHOGONAL to the Acer performance profile (the Acer WMI profile write carries no
/// overlay GUID and touches no Windows power scheme), so setting it per profile does not fight the EC.
/// Cross-vendor + Windows-only; <see cref="TryCreate"/> returns null on an OS without the overlay API so the UI
/// hides the CPU section. No elevation needed for the overlay (the app runs elevated anyway for the EC/WMI
/// controls). Named to avoid colliding with the <c>IDevice.CpuPower</c> port property.
/// </summary>
internal sealed partial class OverlayCpuPower : ICpuPower
{
    // Documented power-overlay scheme GUIDs (Balanced = the all-zero GUID). Same set OverlayPowerProfiles uses.
    private static readonly Guid Efficiency  = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Balanced    = Guid.Empty;
    private static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    // Guid is blittable (16 bytes), so these marshal with zero generated stubs -> AOT-safe. Overlay by value,
    // the effective-scheme read by out-pointer.
    [LibraryImport("powrprof.dll")] private static partial uint PowerSetActiveOverlayScheme(Guid overlay);
    [LibraryImport("powrprof.dll")] private static partial uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    // Display order: efficiency -> balanced -> performance (the OS slider's low-to-high spectrum).
    private static readonly ChoiceOption[] _modes =
    [
        new(Efficiency.ToString(),  "Best power efficiency"),
        new(Balanced.ToString(),    "Balanced"),
        new(Performance.ToString(), "Best performance"),
    ];

    public IReadOnlyList<ChoiceOption> Modes => _modes;
    public string? LastError { get; private set; }

    /// <summary>Probe for the overlay API. Returns null (feature hidden) on an OS/build where it isn't present.
    /// Never throws.</summary>
    public static OverlayCpuPower? TryCreate()
    {
        try { return PowerGetEffectiveOverlayScheme(out _) == 0 ? new OverlayCpuPower() : null; }
        catch { return null; }
    }

    public string? Current()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) != 0) return null;
            var id = g.ToString();
            // Some builds report Balanced as a non-empty "recommended" GUID — fold any unrecognized value to
            // Balanced so the UI always maps it to a known mode.
            return Array.Exists(_modes, m => m.Id == id) ? id : Balanced.ToString();
        }
        catch (Exception e) { LastError = e.Message; return null; }
    }

    public bool Set(string id)
    {
        LastError = null;
        if (!Guid.TryParse(id, out var g)) { LastError = "invalid overlay id"; return false; }
        try
        {
            uint r = PowerSetActiveOverlayScheme(g);
            if (r != 0) { LastError = $"PowerSetActiveOverlayScheme={r}"; return false; }
            return true;
        }
        catch (Exception e) { LastError = e.Message; return false; }
    }
}
