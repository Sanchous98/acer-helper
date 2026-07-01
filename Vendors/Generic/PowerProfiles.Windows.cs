using System.Runtime.InteropServices;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// Generic Windows performance profiles via the OS power-mode overlay (the taskbar battery-icon
/// slider): Best power efficiency / Balanced / Best performance. Vendor-agnostic — works on any
/// laptop — so it backs the generic device when no vendor backend is present.
/// </summary>
public sealed class OverlayPowerProfiles : IPowerProfiles
{
    // Documented power-overlay scheme GUIDs (empty = Balanced/Recommended).
    private static readonly Guid Efficiency  = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Balanced     = Guid.Empty;
    private static readonly Guid Performance  = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private static readonly (Guid g, string name, ProfileKind kind, AccentColor accent)[] Table =
    {
        (Efficiency,  "Best efficiency",  ProfileKind.Eco,         new(0x00, 0x89, 0x7B)),
        (Balanced,    "Balanced",         ProfileKind.Balanced,    new(0x2E, 0x7D, 0x32)),
        (Performance, "Best performance", ProfileKind.Performance, new(0xF5, 0x7C, 0x00)),
    };

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid scheme);

    public OverlayPowerProfiles()
    {
        try { Available = PowerGetEffectiveOverlayScheme(out _) == 0; }
        catch { Available = false; }
    }

    /// <summary>True if the power-overlay API responded (composition gate).</summary>
    public bool Available { get; }
    public string? LastError { get; private set; }

    public IReadOnlyList<PerformanceProfile> All { get; } =
        Table.Select(t => new PerformanceProfile(t.g.ToString(), t.name, t.kind, t.accent)).ToList();

    public IReadOnlyList<PerformanceProfile> Selectable() => All;

    public PerformanceProfile? Current()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out Guid g) != 0) return null;
            return All.FirstOrDefault(p => p.Id == g.ToString()) ?? All[1];   // unknown/empty -> Balanced
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public bool Set(PerformanceProfile profile)
    {
        try
        {
            uint r = PowerSetActiveOverlayScheme(Guid.Parse(profile.Id));
            if (r != 0) { LastError = $"PowerSetActiveOverlayScheme={r}"; return false; }
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }
}
