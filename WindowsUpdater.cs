using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace AcerHelper;

/// <summary>Self-update for the Windows MSI install: download the release MSI and hand off to msiexec for an
/// in-place major upgrade (the .wxs carries a fixed UpgradeCode + &lt;MajorUpgrade&gt;), then relaunch. The
/// running exe lives in Program Files and is LOCKED while we're alive, so we can't overwrite it ourselves;
/// instead a tiny detached .cmd waits for THIS process to exit, runs msiexec, restarts the app, and deletes
/// itself + the MSI. Our process is already elevated (app.manifest requireAdministrator), so the child cmd
/// and msiexec inherit elevation — no second UAC prompt. Only meaningful for the installed build; a
/// portable/dev run reports unsupported and the caller falls back to opening the release page.</summary>
public static class WindowsUpdater
{
    /// <summary>True only for the MSI-installed build (AcerHelper.exe under Program Files). A portable or
    /// dev-tree run returns false so the caller opens the release page instead.</summary>
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
            if (!string.IsNullOrEmpty(dir) && exe.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The release's .msi asset, if present.</summary>
    public static ReleaseAsset? PickAsset(IReadOnlyList<ReleaseAsset> assets)
        => assets.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

    /// <summary>Download the MSI to a temp file. (ok, msiPath-or-error). Blocking I/O — call off the UI
    /// thread.</summary>
    public static async Task<(bool ok, string? result)> DownloadAsync(string assetUrl, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"AcerHelper-Setup-{Guid.NewGuid():N}.msi");
        try
        {
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

    /// <summary>Spawn the detached upgrade helper and return true; the caller MUST exit immediately after so
    /// the exe unlocks. The helper polls until our PID is gone, runs msiexec (major upgrade, no reboot),
    /// relaunches the app at the same path, then deletes the MSI and itself.</summary>
    public static bool InstallAndExit(string msiPath)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return false;
        var pid = Environment.ProcessId;
        var image = Path.GetFileName(exe);   // "AcerHelper.exe"
        var bat = Path.Combine(Path.GetTempPath(), $"AcerHelper-update-{Guid.NewGuid():N}.cmd");

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
        catch { return false; }
    }
}
