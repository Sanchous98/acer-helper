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
