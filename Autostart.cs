using System.Diagnostics;

namespace AcerHelper;

/// <summary>
/// Start Acer Helper automatically at logon. Because the app runs elevated
/// (requireAdministrator), a plain HKCU\...\Run entry would trigger a UAC prompt
/// on every logon. Instead we register a Scheduled Task with "highest privileges",
/// which launches it elevated and silently. Creating/removing the task itself
/// requires admin — which the app already has.
/// </summary>
public static class Autostart
{
    private const string TaskName = "AcerHelperAutostart";

    /// <summary>True if the autostart scheduled task exists.</summary>
    public static bool IsEnabled() => Run($"/query /tn \"{TaskName}\"").exit == 0;

    /// <summary>Create or remove the autostart task. Returns true on success.</summary>
    public static bool SetEnabled(bool enable)
    {
        if (enable)
        {
            string exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
            return Run($"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\"\" /sc onlogon /rl highest /f").exit == 0;
        }
        return Run($"/delete /tn \"{TaskName}\" /f").exit == 0;
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
