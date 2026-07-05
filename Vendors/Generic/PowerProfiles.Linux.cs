using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using AcerHelper.Features;

namespace AcerHelper.Vendors.Generic;

/// <summary>
/// Generic Linux performance profiles via <b>power-profiles-daemon</b> (or its tuned-ppd shim) over
/// the standard <c>net.hadess.PowerProfiles</c> D-Bus API: power-saver / balanced / performance.
/// This is what GNOME Settings uses — switching is polkit-authorised for the active session, so it
/// works without root. Preferred over raw sysfs; the composition root falls back to
/// <see cref="SysfsPowerProfiles"/> when this isn't present.
/// </summary>
public sealed partial class PpdPowerProfiles : IPowerProfiles
{
    private const string Bus   = "net.hadess.PowerProfiles";
    private const string Obj   = "/net/hadess/PowerProfiles";
    private const string Iface = "net.hadess.PowerProfiles";

    public PpdPowerProfiles()
    {
        var (code, outp) = Busctl.Call("get-property", Bus, Obj, Iface, "Profiles");
        if (code != 0) { All = []; Available = false; return; }
        // payload: ... "Profile" s "power-saver" "Driver" s "tuned" "Profile" s "balanced" ...
        All = ProfileRegex().Matches(outp)
                   .Select(m => ToProfile(m.Groups[1].Value)).ToList();
        Available = All.Count > 0;
    }

    public bool Available { get; }
    public string? LastError { get; private set; }
    public IReadOnlyList<PerformanceProfile> All { get; }
    public IReadOnlyList<PerformanceProfile> Selectable() => All;

    public PerformanceProfile? Current()
    {
        var (code, outp) = Busctl.Call("get-property", Bus, Obj, Iface, "ActiveProfile");
        if (code != 0) return null;
        var m = CurrentProfileRegex().Match(outp);
        if (!m.Success) return null;
        var id = m.Groups[1].Value;
        return All.FirstOrDefault(p => p.Id == id) ?? ToProfile(id);
    }

    public bool Set(PerformanceProfile profile)
    {
        var (code, outp) = Busctl.Call("set-property", Bus, Obj, Iface, "ActiveProfile", "s", profile.Id);
        if (code != 0) LastError = outp;
        return code == 0;
    }

    private static PerformanceProfile ToProfile(string id)
    {
        var (name, kind, accent) = id switch
        {
            "power-saver" => ("Power saver", ProfileKind.Eco,         new AccentColor(0x00, 0x89, 0x7B)),
            "balanced"    => ("Balanced",    ProfileKind.Balanced,    new AccentColor(0x2E, 0x7D, 0x32)),
            "performance" => ("Performance", ProfileKind.Performance, new AccentColor(0xF5, 0x7C, 0x00)),
            _             => (id,            ProfileKind.Other,       new AccentColor(0x80, 0x80, 0x80)),
        };
        return new PerformanceProfile(id, name, kind, accent);
    }

    [GeneratedRegex("\"Profile\"\\s+s\\s+\"([^\"]+)\"")]
    private static partial Regex ProfileRegex();
    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex CurrentProfileRegex();
}

/// <summary>Thin system-bus <c>busctl</c> runner shared by the D-Bus-backed Linux services
/// (power-profiles-daemon, UPower keyboard backlight). D-Bus services are polkit/at-console
/// authorised, so these work without root — preferred over root-only sysfs writes.</summary>
internal static class Busctl
{
    public static (int code, string output) Call(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("busctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("--system");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}

/// <summary>
/// Fallback generic Linux profiles via the ACPI <c>platform_profile</c> sysfs interface. Reading
/// works as the user; writing usually needs root/udev (and conflicts with a running power daemon),
/// so this is only used when no power-profiles-daemon D-Bus interface is available.
/// </summary>
public sealed class SysfsPowerProfiles : IPowerProfiles
{
    // Two kernel interfaces expose platform profiles: the ACPI alias (all kernels; a write fans out to
    // every registered handler) and per-handler class nodes (6.14+). The alias is frequently left
    // root-only — it appears only when a handler registers, which for EC modules like Linuwu-Sense is
    // after the boot-time tmpfiles pass — while the class nodes trigger a udev event on registration, so
    // a udev rule can grant group access reliably; the vendor handler also carries the firmware's full
    // profile list. Hence: prefer whichever source this process can actually write.
    private const string LegacyProfile = "/sys/firmware/acpi/platform_profile";
    private const string LegacyChoices = "/sys/firmware/acpi/platform_profile_choices";
    private const string ClassRoot     = "/sys/class/platform-profile";

    private readonly string _profilePath;

    public SysfsPowerProfiles()
    {
        (_profilePath, var choicesPath) = PickSource();
        var choices = Read(choicesPath);
        All = choices == null
            ? []
            : choices.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(ToProfile).ToList();
        Available = All.Count > 0;
        Writable = Available && CanWrite(_profilePath);
    }

    private static (string Profile, string Choices) PickSource()
    {
        if (CanWrite(LegacyProfile)) return (LegacyProfile, LegacyChoices);
        (string, string)? best = null;
        var bestCount = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ClassRoot))   // one dir per handler
            {
                var profile = Path.Combine(dir, "profile");
                var choices = Path.Combine(dir, "choices");
                if (!CanWrite(profile)) continue;
                // Several handlers may coexist (e.g. amd-pmf + the vendor EC): the richest choice list is
                // the vendor's own, which is the set worth showing.
                var count = Read(choices)?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
                if (count > bestCount) (best, bestCount) = ((profile, choices), count);
            }
        }
        catch { /* class absent (pre-6.14 kernel) */ }
        return best ?? (LegacyProfile, LegacyChoices);
    }

    public bool Available { get; }

    /// <summary>Whether this process can actually SWITCH profiles — the node is normally root-only, so a
    /// vendor should only prefer this over the polkit-authorised PPD when this is true (a richer profile
    /// list whose writes all fail is worse than a shorter working one).</summary>
    public bool Writable { get; }

    // Probe write permission without writing (sysfs attrs don't act until an actual write happens).
    private static bool CanWrite(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Write); return true; }
        catch { return false; }
    }
    public string? LastError { get; private set; }
    public IReadOnlyList<PerformanceProfile> All { get; }
    public IReadOnlyList<PerformanceProfile> Selectable() => All;

    public PerformanceProfile? Current()
    {
        var cur = Read(_profilePath);
        if (cur == null) return null;
        return All.FirstOrDefault(p => p.Id == cur) ?? ToProfile(cur);
    }

    public bool Set(PerformanceProfile profile)
    {
        try { File.WriteAllText(_profilePath, profile.Id); return true; }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    private static PerformanceProfile ToProfile(string choice)
    {
        var (name, kind, accent) = choice switch
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
