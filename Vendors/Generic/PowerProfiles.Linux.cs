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
public sealed class PpdPowerProfiles : IPowerProfiles
{
    private const string Bus   = "net.hadess.PowerProfiles";
    private const string Obj   = "/net/hadess/PowerProfiles";
    private const string Iface = "net.hadess.PowerProfiles";

    public PpdPowerProfiles()
    {
        var (code, outp) = Busctl("get-property", Bus, Obj, Iface, "Profiles");
        if (code != 0) { All = []; Available = false; return; }
        // payload: ... "Profile" s "power-saver" "Driver" s "tuned" "Profile" s "balanced" ...
        All = Regex.Matches(outp, "\"Profile\"\\s+s\\s+\"([^\"]+)\"")
                   .Select(m => ToProfile(m.Groups[1].Value)).ToList();
        Available = All.Count > 0;
    }

    public bool Available { get; }
    public string? LastError { get; private set; }
    public IReadOnlyList<PerformanceProfile> All { get; }
    public IReadOnlyList<PerformanceProfile> Selectable() => All;

    public PerformanceProfile? Current()
    {
        var (code, outp) = Busctl("get-property", Bus, Obj, Iface, "ActiveProfile");
        if (code != 0) return null;
        var m = Regex.Match(outp, "\"([^\"]+)\"");
        if (!m.Success) return null;
        string id = m.Groups[1].Value;
        return All.FirstOrDefault(p => p.Id == id) ?? ToProfile(id);
    }

    public bool Set(PerformanceProfile profile)
    {
        var (code, outp) = Busctl("set-property", Bus, Obj, Iface, "ActiveProfile", "s", profile.Id);
        if (code != 0) LastError = outp;
        return code == 0;
    }

    private static PerformanceProfile ToProfile(string id)
    {
        (string name, ProfileKind kind, AccentColor accent) = id switch
        {
            "power-saver" => ("Power saver", ProfileKind.Eco,         new AccentColor(0x00, 0x89, 0x7B)),
            "balanced"    => ("Balanced",    ProfileKind.Balanced,    new AccentColor(0x2E, 0x7D, 0x32)),
            "performance" => ("Performance", ProfileKind.Performance, new AccentColor(0xF5, 0x7C, 0x00)),
            _             => (id,            ProfileKind.Other,       new AccentColor(0x80, 0x80, 0x80)),
        };
        return new PerformanceProfile(id, name, kind, accent);
    }

    private static (int code, string output) Busctl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("busctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("--system");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
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
