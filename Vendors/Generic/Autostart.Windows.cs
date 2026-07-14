using System.Diagnostics;
using System.IO;
using System.Text;

namespace AcerHelper.Vendors.Generic;

// Start at logon via a Scheduled Task with highest privileges (so the elevated app launches without a UAC
// prompt each logon). Task Scheduler (a system service, not user-killable) is the watchdog — no separate,
// equally-killable helper process: the task launches the app with --startup (resident in the tray, no flyout)
// and, via a 1-minute repeating TimeTrigger + MultipleInstancesPolicy=IgnoreNew, keeps it alive — while the app
// runs the repeats are no-ops, and if it dies (killed OR crash, regardless of exit code) the next repeat (≤1
// min) relaunches it. (RestartOnFailure was tried and dropped: it only restarts the task's OWN launched instance
// and keys off a non-zero exit code — unreliable.) The app itself listens for the Nitro key, so keeping it alive
// is enough. Registered from XML because schtasks' /tr form can't express repetition / IgnoreNew / an unlimited
// execution-time. Needs admin (the app has it).
public sealed partial class Autostart
{
    private const string TaskName = "AcerHelperAutostart";

    public partial string Label => "Start with Windows";

    public partial bool IsEnabled() => Run($"/query /tn \"{TaskName}\"").exit == 0;

    public partial bool SetEnabled(bool enable)
    {
        if (!enable) return Run($"/delete /tn \"{TaskName}\" /f").exit == 0;

        // Over-the-shoulder elevation guard: when a standard user elevates with a DIFFERENT admin account,
        // our token (which BuildTaskXml binds the logon trigger + principal to) is that admin — who never
        // logs on interactively, so the task would register fine and never fire. No task shape can fix
        // this: Task Scheduler can't raise a UAC prompt, so it cannot start a requireAdministrator exe for
        // a non-admin user at all. Refuse instead of registering a convincing no-op; the toggle then
        // honestly reads back OFF (IsEnabled checks task existence).
        if (!TokenUserIsInteractiveUser()) return false;

        var xmlPath = Path.Combine(StagingDir(), $"acerhelper-autostart-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);   // UTF-16 + BOM, as schtasks /xml expects
            return Run($"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f").exit == 0;
        }
        catch { return false; }
        finally { try { File.Delete(xmlPath); } catch { /* best-effort */ } }
    }

    // Where to stage the task XML that ELEVATED schtasks will parse. A fixed name in the user %TEMP% was a
    // TOCTOU gift: any same-user medium-IL process could swap the file between our write and schtasks'
    // read and register an arbitrary elevated task. For the installed build the exe sits under Program
    // Files (admin-writable only), so its own folder is the protected choice. A portable run falls back to
    // %TEMP% — there the exe itself is user-writable, so hardening the XML alone would protect nothing —
    // with the random name at least breaking the trivial watch-and-swap of a known path.
    private static string StagingDir()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var pf = Environment.GetFolderPath(f);
            // Compare with a trailing separator so "C:\Program Files" doesn't also match a sibling like
            // "C:\Program Filesque" (which would misclassify a user-writable folder as protected).
            if (!string.IsNullOrEmpty(pf) && IsUnder(dir, pf)) return dir;
        }
        return Path.GetTempPath();
    }

    // Re-register the logon task if it exists but is out of date (an older build's command — e.g. the removed
    // --watch watcher), so an in-place upgrade heals itself. Only touches our OWN entry (same exe) so running a
    // different build can't hijack autostart to its path. "Launches this exe with --startup" is treated as current.
    public void EnsureCurrent()
    {
        var (exit, output) = Run($"/query /tn \"{TaskName}\" /fo LIST /v");
        if (exit != 0) return;
        if (output.Contains(ExePath, StringComparison.OrdinalIgnoreCase)
            && !output.Contains(AppArgs.Startup, StringComparison.OrdinalIgnoreCase))
            SetEnabled(true);
    }

    // Task definition. Two triggers: a LogonTrigger (instant launch of --startup at logon, resident in the tray)
    // and a TimeTrigger with a past StartBoundary + 1-minute indefinite repetition (the keep-alive — also active
    // mid-session, unlike a logon trigger's repetition, so it's verifiable without a re-logon). IgnoreNew makes a
    // repeat a no-op while the app runs, and relaunch it within a minute if it died. HighestAvailable +
    // InteractiveToken = elevated, in the user's desktop session. ExecutionTimeLimit PT0S = unlimited (else the
    // 3-day default would stop a resident app); runs on battery. Settings are in schema (XSD) order.
    private static string BuildTaskXml()
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var u    = System.Security.SecurityElement.Escape(user);
        var exe  = System.Security.SecurityElement.Escape(ExePath);
        return
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo><Description>Start Acer Helper at logon and keep it running.</Description></RegistrationInfo>\n" +
            "  <Triggers>\n" +
            "    <LogonTrigger>\n" +
            "      <Enabled>true</Enabled>\n" +
            $"      <UserId>{u}</UserId>\n" +
            "    </LogonTrigger>\n" +
            "    <TimeTrigger>\n" +
            "      <StartBoundary>2020-01-01T00:00:00</StartBoundary>\n" +
            "      <Enabled>true</Enabled>\n" +
            "      <Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>\n" +
            "    </TimeTrigger>\n" +
            "  </Triggers>\n" +
            "  <Principals>\n" +
            $"    <Principal id=\"Author\"><UserId>{u}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>\n" +
            "  </Principals>\n" +
            "  <Settings>\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            "    <AllowHardTerminate>false</AllowHardTerminate>\n" +
            "    <StartWhenAvailable>true</StartWhenAvailable>\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
            "    <Enabled>true</Enabled>\n" +
            "    <Priority>7</Priority>\n" +
            "  </Settings>\n" +
            "  <Actions Context=\"Author\">\n" +
            $"    <Exec><Command>{exe}</Command><Arguments>{AppArgs.Startup}</Arguments></Exec>\n" +
            "  </Actions>\n" +
            "</Task>";
    }

    private static bool IsUnder(string path, string root)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    // Does our process token belong to the user actually logged on to this session? (See the guard in
    // SetEnabled.) The token gives DOMAIN\user; WTS reports the session's bare user name — compare the user
    // parts. Any uncertainty answers TRUE (register the task): WTS returning nothing (SYSTEM/session-0) or
    // a rare same-short-name collision across two accounts both fail open, because blocking a legitimate
    // self-elevated admin is worse than the cosmetic no-op this guard exists to avoid. (Domain comparison
    // was considered and dropped: WTS vs token domain formatting differs for AzureAD/MSA accounts, risking
    // a false block of legitimate users — not worth it for this low-severity guard.)
    private static bool TokenUserIsInteractiveUser()
    {
        try
        {
            var token = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var sessionUser = InteractiveUser();
            if (string.IsNullOrEmpty(sessionUser)) return true;   // WTS gave us nothing usable -> fail open
            var slash = token.LastIndexOf('\\');
            var tokenUser = slash >= 0 ? token[(slash + 1)..] : token;
            return string.Equals(tokenUser, sessionUser, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private const int WTSUserName = 5;

    [System.Runtime.InteropServices.LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);

    [System.Runtime.InteropServices.LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint buffer);

    private static string? InteractiveUser()
    {
        if (!WTSQuerySessionInformation(0, Process.GetCurrentProcess().SessionId, WTSUserName, out var buf, out _)
            || buf == 0) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
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
