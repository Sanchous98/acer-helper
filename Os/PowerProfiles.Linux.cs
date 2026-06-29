using System.IO;

namespace AcerHelper.Os;

/// <summary>
/// Generic Linux performance profiles via the ACPI <c>platform_profile</c> sysfs interface
/// (/sys/firmware/acpi/platform_profile[_choices]). Vendor-agnostic — works on any laptop whose
/// firmware/driver exposes it (Dell, ThinkPad, Acer via acer-wmi, AMD/Intel, …). Backs the
/// generic device. Writing usually needs root or a udev rule granting the user write access.
/// </summary>
public sealed class SysfsPowerProfiles : IPowerProfiles
{
    private const string ProfilePath = "/sys/firmware/acpi/platform_profile";
    private const string ChoicesPath = "/sys/firmware/acpi/platform_profile_choices";

    public SysfsPowerProfiles()
    {
        string? choices = Read(ChoicesPath);
        All = choices == null
            ? []
            : choices.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(ToProfile).ToList();
        Available = All.Count > 0;
    }

    /// <summary>True if the kernel exposes platform_profile choices (composition gate).</summary>
    public bool Available { get; }
    public string? LastError { get; private set; }
    public IReadOnlyList<PerformanceProfile> All { get; }
    public IReadOnlyList<PerformanceProfile> Selectable() => All;

    public PerformanceProfile? Current()
    {
        string? cur = Read(ProfilePath);
        if (cur == null) return null;
        return All.FirstOrDefault(p => p.Id == cur) ?? ToProfile(cur);
    }

    public bool Set(PerformanceProfile profile)
    {
        try { File.WriteAllText(ProfilePath, profile.Id); return true; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    private static PerformanceProfile ToProfile(string choice)
    {
        (string name, ProfileKind kind, AccentColor accent) = choice switch
        {
            "low-power"            => ("Low power",            ProfileKind.Eco,         new AccentColor(0x00, 0x89, 0x7B)),
            "quiet"                => ("Quiet",                ProfileKind.Quiet,       new AccentColor(0x42, 0x85, 0xF4)),
            "cool"                 => ("Cool",                 ProfileKind.Quiet,       new AccentColor(0x42, 0x85, 0xF4)),
            "balanced"             => ("Balanced",             ProfileKind.Balanced,    new AccentColor(0x2E, 0x7D, 0x32)),
            "balanced-performance" => ("Balanced performance", ProfileKind.Performance, new AccentColor(0xF5, 0x7C, 0x00)),
            "performance"          => ("Performance",          ProfileKind.Performance, new AccentColor(0xD3, 0x2F, 0x2F)),
            _                      => (choice,                 ProfileKind.Other,       new AccentColor(0x80, 0x80, 0x80)),
        };
        return new PerformanceProfile(choice, name, kind, accent);
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }
}
