using System.Diagnostics;
using System.IO;

namespace AcerHelper;

/// <summary>Linux-only: installs the udev + tmpfiles permission rules bundled next to the app (AppImage
/// case — a sandbox-free binary can call pkexec directly) into /etc via a single polkit prompt, so the
/// root-only control nodes become writable by the user's group. A native RPM install already ships the
/// rules (under /usr/lib/udev/rules.d), so the action is offered ONLY when neither location has them. No-op
/// on Windows (no udev). Cross-platform file so the all-platforms callers don't trip CA1416.</summary>
public static class HardwareAccess
{
    private static readonly string[] RuleLocations =
    [
        "/etc/udev/rules.d/60-acer-helper.rules",
        "/usr/lib/udev/rules.d/60-acer-helper.rules",   // where the RPM puts it
    ];

    /// <summary>True when we should offer "Grant hardware access": Linux, the bundled rule files are present
    /// (i.e. running from the AppImage/publish, not a rules-less build), and no installed copy matches them —
    /// covering both "never installed" and "installed by an older version" (rules evolve with the app; the
    /// pkexec install is idempotent).</summary>
    public static bool RulesNeeded()
    {
        if (!OperatingSystem.IsLinux()) return false;
        var rule = Bundled("60-acer-helper.rules");
        var conf = Bundled("acer-helper.conf");
        if (rule == null || conf == null) return false;
        return !RuleLocations.Any(loc => SameContent(loc, rule))
               || !SameContent("/etc/tmpfiles.d/acer-helper.conf", conf);
    }

    private static bool SameContent(string installed, string bundled)
    {
        try
        {
            return File.Exists(installed) &&
                   File.ReadAllBytes(installed).AsSpan().SequenceEqual(File.ReadAllBytes(bundled));
        }
        catch { return false; }
    }

    /// <summary>Install the bundled rules into /etc via one pkexec prompt and apply them live. Runs the
    /// blocking pkexec call — call it off the UI thread.</summary>
    public static bool Install(out string? error)
    {
        error = null;
        var rule = Bundled("60-acer-helper.rules");
        var conf = Bundled("acer-helper.conf");
        if (rule == null || conf == null) { error = "bundled rules not found"; return false; }

        // Copy the bundled files to a plain temp dir first: root (via pkexec) can't traverse into the user's
        // AppImage FUSE mount, but can read /tmp. Then pkexec installs from there.
        DirectoryInfo? stage = null;
        try
        {
            stage = Directory.CreateTempSubdirectory("acer-helper-rules");
            var stagedRule = Path.Combine(stage.FullName, "60-acer-helper.rules");
            var stagedConf = Path.Combine(stage.FullName, "acer-helper.conf");
            File.Copy(rule, stagedRule, overwrite: true);
            File.Copy(conf, stagedConf, overwrite: true);

            var script =
                $"install -m0644 '{stagedRule}' /etc/udev/rules.d/60-acer-helper.rules && " +
                $"install -m0644 '{stagedConf}' /etc/tmpfiles.d/acer-helper.conf && " +
                "udevadm control --reload-rules && " +
                "udevadm trigger --subsystem-match=power_supply --subsystem-match=leds --subsystem-match=platform-profile " +
                "--subsystem-match=platform --subsystem-match=input && " +
                "systemd-tmpfiles --create /etc/tmpfiles.d/acer-helper.conf";

            var psi = new ProcessStartInfo("pkexec") { UseShellExecute = false };
            psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);

            using var p = Process.Start(psi);
            if (p == null) { error = "could not start pkexec"; return false; }
            p.WaitForExit(120_000);
            if (p.ExitCode == 0) return true;
            error = $"pkexec exited with code {p.ExitCode}";   // 126/127 = auth dismissed / not authorized
            return false;
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally { try { stage?.Delete(recursive: true); } catch { /* ignore */ } }
    }

    private static string? Bundled(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }
}
