using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace AcerHelper.Infrastructure;

/// <summary>Self-update for the Windows MSI install: download the release MSI and hand off to msiexec for an
/// in-place major upgrade (the .wxs carries a fixed UpgradeCode + &lt;MajorUpgrade&gt;), then relaunch. The
/// running exe lives in Program Files and is LOCKED while we're alive, so we can't overwrite it ourselves;
/// instead a tiny detached .cmd waits for THIS process to exit, runs msiexec, restarts the app, and deletes
/// itself + the MSI. Our process is already elevated (app.manifest requireAdministrator), so the child cmd
/// and msiexec inherit elevation — no second UAC prompt.
///
/// A PORTABLE/dev run lives outside Program Files and is not elevated, so it cannot self-replace; it installs
/// the same MSI through the Windows Installer instead. That path used to leave the user with a fresh install and
/// NOTHING running (it "had nothing to relaunch"): the installer completed and the app was simply gone, which
/// reads exactly as "the update broke — it reinstalls but never restarts". The installer is now WAITED on and
/// then the freshly installed copy is launched before the portable process quits (see
/// <see cref="InstallPortableAsync"/> + <see cref="RelaunchInstalled"/>).</summary>
public static class WindowsUpdater
{
    /// <summary>True only for the MSI-installed build (AcerHelper.exe under Program Files). A portable or
    /// dev-tree run returns false; the caller then installs the MSI instead of opening the release page.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows()
        && Environment.ProcessPath is { } p
        && p.EndsWith("AcerHelper.exe", StringComparison.OrdinalIgnoreCase)
        && IsUnderProgramFiles(p);

    private static bool IsUnderProgramFiles(string exe)
    {
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var dir = Environment.GetFolderPath(f);
            // Trailing separator so "C:\Program Files" doesn't also match a sibling "C:\Program Filesque".
            if (string.IsNullOrEmpty(dir)) continue;
            var root = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The release's .msi asset, if present.</summary>
    public static ReleaseAsset? PickAsset(IReadOnlyList<ReleaseAsset> assets)
        => assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

    // Staging directory for the downloaded MSI and the helper .cmd. For the INSTALLED build this is the install
    // folder: everything staged here is later executed ELEVATED, and %TEMP% is writable by any same-user
    // medium-IL process, which could swap the MSI (or rewrite the .cmd — cmd.exe reads batch files incrementally,
    // so even a running script isn't safe) during the seconds between download and msiexec — a silent user→admin
    // escalation. IsSupported guarantees the exe lives under Program Files, which only administrators can write,
    // so the install folder itself is the protected staging area; we're elevated, so writing there is fine, and
    // the helper deletes both files when done.
    //
    // A PORTABLE run stages under %LOCALAPPDATA%\AcerHelper\update instead: its own folder may be read-only
    // (e.g. a mounted image) and offers no such protection anyway, because the process is not elevated — the MSI
    // is launched through UAC, which is the elevation the user sees and approves.
    private static string StagingDir =>
        IsSupported
            ? AppContext.BaseDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "AcerHelper", "update");

    /// <summary>Download the MSI into the staging directory (see <see cref="StagingDir"/>). (ok,
    /// msiPath-or-error). Blocking I/O — call off the UI thread.</summary>
    public static async Task<(bool ok, string? result)> DownloadAsync(string assetUrl, CancellationToken ct = default)
    {
        SweepStale();   // clear any MSI/.cmd stranded by a previous failed/interrupted update (see below)
        var tmp = Path.Combine(StagingDir, $"AcerHelper-Setup-{Guid.NewGuid():N}.msi");
        try
        {
            Directory.CreateDirectory(StagingDir);   // the portable staging dir may not exist yet
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AcerHelper-update");   // some GitHub endpoints 403 UA-less
            await using (var src = await http.GetStreamAsync(assetUrl, ct))       // asset URL 302s to a CDN; HttpClient follows
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);
            return (true, tmp);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            return (false, ex.Message);
        }
    }

    /// <summary>The installed MSI's exe, launched after a portable-run install so the user ends up in the NEW
    /// build rather than with a fresh install and nothing on screen. A fixed path on purpose: the MSI's
    /// INSTALLFOLDER is pinned in packaging/AcerHelper.wxs, and a per-machine install ignores wherever the
    /// portable run happens to live — so there is nothing to probe and nothing to guess. Null off Windows.</summary>
    public static string? InstalledExePath =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                           "AcerHelper", "AcerHelper.exe")
            : null;

    /// <summary>PORTABLE/DEV install path: run the MSI through the Windows Installer, WAIT for it to finish, and
    /// report whether it succeeded so the caller can relaunch into the new build and quit (see
    /// <see cref="RelaunchInstalled"/>). Blocking I/O — call off the UI thread.
    ///
    /// WHY IT WAITS. This used to shell-execute the MSI and return at once, which left the user with a completed
    /// install and NO running app — the "update reinstalls but does not restart" defect. The elevated msiexec is
    /// the only thing that can write Program Files, and the caller cannot know it is done unless it waits; "done"
    /// is exactly when the relaunch is safe. <c>Verb=runas</c> (not a bare shell-execute) is what makes the wait
    /// honest: it hands back the ELEVATED msiexec that does the work, instead of the non-elevated stub msiexec
    /// would otherwise leave us holding while it elevates itself and the wait returns before any file is written.
    /// An already-elevated call (the usual case: app.manifest requireAdministrator) elevates to the same level, so
    /// no prompt is shown; a medium-IL caller gets the one UAC consent the install needs. /passive shows progress
    /// with no prompts and /norestart keeps a reboot from being scheduled behind the user's back. A non-zero exit
    /// is a failed install: we do NOT pretend to relaunch, the caller reports it, and the old copy stays put.
    /// A declined UAC prompt throws out of Start and is the same failure.</summary>
    public static async Task<bool> InstallPortableAsync(string msiPath, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{msiPath}\" /passive /norestart",
                UseShellExecute = true,   // required for Verb=runas; the handle is the elevated msiexec
                Verb = "runas",
            });
            if (p == null) return false;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);   // blocks on the ELEVATED process (see above)
            try { return p.ExitCode == 0; }
            catch
            {
                // Some shell-executed launches refuse ExitCode. The wait already told us it finished, so the
                // honest fallback is whether the install actually landed a fresh exe.
                return InstalledExePath is { } exe && File.Exists(exe);
            }
        }
        catch { return false; }   // e.g. Win32Exception 1223: the user declined the UAC prompt
    }

    /// <summary>Launch the freshly installed build, detached, so an update leaves the user in the NEW app instead
    /// of with a fresh install and nothing running. Only meaningful after <see cref="InstallPortableAsync"/>
    /// returned true (the file exists then). The caller exits right after; the new process waits out our
    /// single-instance mutex (see Bootstrap/Program.cs) and takes over.
    ///
    /// UseShellExecute=true is deliberate: it inherits THIS process's elevation token, and every Windows build
    /// self-elevates (app.manifest requireAdministrator), so the relaunch comes up with the hardware access it
    /// needs and no second consent prompt.</summary>
    public static bool RelaunchInstalled()
    {
        if (InstalledExePath is not { } exe || !File.Exists(exe)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return p != null;
        }
        catch { return false; }
    }

    /// <summary>Spawn the detached upgrade helper and return true; the caller MUST exit immediately after so
    /// the exe unlocks. The helper polls until our PID is gone, runs msiexec (major upgrade, no reboot),
    /// relaunches the app at the same path, then deletes the MSI and itself.</summary>
    public static bool InstallAndExit(string msiPath)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return false;
        var pid = Environment.ProcessId;
        var image = Path.GetFileName(exe);   // "AcerHelper.exe"
        var bat = Path.Combine(StagingDir, $"AcerHelper-update-{Guid.NewGuid():N}.cmd");

        // ping -n 2 == ~1s sleep with no console/stdin (unlike `timeout`). Loop while our PID+image is still
        // listed; once the process is gone, tasklist prints "No tasks" and `find` fails -> fall through.
        var script =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" /FI \"IMAGENAME eq {image}\" 2>nul | find /I \"{image}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)\r\n" +
            $"msiexec /i \"{msiPath}\" /qb /norestart\r\n" +
            $"start \"\" \"{exe}\"\r\n" +
            $"del \"{msiPath}\"\r\n" +
            "del \"%~f0\"\r\n";
        try
        {
            File.WriteAllText(bat, script);
            using (Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
                   {
                       UseShellExecute = false,
                       CreateNoWindow = true,
                       WindowStyle = ProcessWindowStyle.Hidden,
                   })) { }
            return true;
        }
        catch
        {
            // The helper never launched, so nothing will delete what we staged — clean up both here (the
            // MSI would otherwise sit in Program Files forever, an admin-only, user-visible directory).
            try { File.Delete(bat); } catch { /* ignore */ }
            try { File.Delete(msiPath); } catch { /* ignore */ }
            return false;
        }
    }

    // Remove MSI/.cmd left in the staging dir by an update that failed or was interrupted before the helper
    // could delete them (app crash/reboot between download and install; a Process.Start failure). Called at
    // the start of each download; the re-entrancy guard upstream means no in-flight update is ever swept.
    private static void SweepStale()
    {
        try
        {
            foreach (var pattern in new[] { "AcerHelper-Setup-*.msi", "AcerHelper-update-*.cmd" })
                foreach (var f in Directory.EnumerateFiles(StagingDir, pattern))
                    try { File.Delete(f); } catch { /* locked/in-use -> leave it */ }
        }
        catch { /* directory unreadable -> nothing to sweep */ }
    }
}
