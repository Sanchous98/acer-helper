using System.Diagnostics;

namespace AcerHelper.Vendors.Generic;

// Start at logon via a Scheduled Task with "highest privileges" — so the elevated watcher launches
// silently without a UAC prompt each logon. Needs admin (the app has it).
// Launches the lightweight "--watch" mode (not the full UI): it sits in the background listening for the
// Nitro key and opens the full app on demand, so the button works even when AcerHelper is closed.
public sealed partial class Autostart
{
    private const string TaskName = "AcerHelperAutostart";

    public partial string Label => "Start with Windows";

    public partial bool IsEnabled() => Run($"/query /tn \"{TaskName}\"").exit == 0;

    public partial bool SetEnabled(bool enable)
        => enable
            ? Run($"/create /tn \"{TaskName}\" /tr \"\\\"{ExePath}\\\" --watch\" /sc onlogon /rl highest /f").exit == 0
            : Run($"/delete /tn \"{TaskName}\" /f").exit == 0;

    // Heal a stale logon task in place: older builds (or the installer) registered the task WITHOUT --watch, so
    // at logon it launched the full UI instead of the lightweight watcher — meaning the Nitro key did nothing
    // when the app was closed. IsEnabled() only checks the task exists, so such a task is never refreshed. Here
    // we re-register it, but ONLY when it already points at THIS exe and is just missing --watch — so running a
    // different build (e.g. a dev build) can't hijack autostart to its own path.
    public void EnsureCurrent()
    {
        var (exit, output) = Run($"/query /tn \"{TaskName}\" /fo LIST /v");
        if (exit != 0) return;   // not registered -> user hasn't enabled autostart; don't create it unasked
        if (output.Contains(ExePath, StringComparison.OrdinalIgnoreCase)
            && !output.Contains("--watch", StringComparison.OrdinalIgnoreCase))
            SetEnabled(true);   // same exe, missing --watch -> re-create with the current command (/f overwrites)
    }

    private static (int exit, string output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
