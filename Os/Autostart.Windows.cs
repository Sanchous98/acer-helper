using System.Diagnostics;

namespace AcerHelper.Os;

/// <summary>
/// Start at logon via a Scheduled Task with "highest privileges" — so the elevated app launches
/// silently without a UAC prompt each logon. Vendor-agnostic. Needs admin (the app has it).
/// </summary>
public sealed class Autostart : IAutostart
{
    private const string TaskName = "AcerHelperAutostart";

    public string Label => "Start with Windows";

    public bool IsEnabled() => Run($"/query /tn \"{TaskName}\"").exit == 0;

    public bool SetEnabled(bool enable)
    {
        if (enable)
        {
            string exe = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
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
