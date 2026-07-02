using System.IO;
using System.Net.Http;

namespace AcerHelper;

/// <summary>Self-update for the Linux AppImage: download the newer .AppImage from the release and atomically
/// replace the running one in place — so updates work WITHOUT the OS package manager (the point on immutable
/// systems: the binary lives in $HOME, no rpm-ostree/reboot). Only active when launched as an AppImage
/// ($APPIMAGE is set); every other install (RPM, raw) falls back to opening the release page. Replacing the
/// running .AppImage file is safe on Linux — the process keeps the old inode open until it exits.</summary>
public static class AppImageUpdater
{
    /// <summary>The running .AppImage path (from $APPIMAGE), or null if not launched as an AppImage.</summary>
    public static string? AppImagePath =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } p && File.Exists(p)
            ? p : null;

    public static bool IsAppImage => AppImagePath != null;

    /// <summary>The release's .AppImage asset, if present.</summary>
    public static ReleaseAsset? PickAsset(IReadOnlyList<ReleaseAsset> assets)
        => assets.FirstOrDefault(a => a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));

    /// <summary>Download the new AppImage and atomically replace the running one. (bool ok, error). On
    /// success the caller offers <see cref="Restart"/>. Blocking I/O — call off the UI thread.</summary>
    public static async Task<(bool ok, string? error)> ReplaceAsync(string assetUrl, CancellationToken ct = default)
    {
        var target = AppImagePath;
        if (target == null) return (false, "not running as an AppImage");
        var tmp = target + ".new";   // same directory => same filesystem => atomic File.Move
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AcerHelper-update");   // some GitHub endpoints 403 UA-less
                await using (var src = await http.GetStreamAsync(assetUrl, ct))   // GitHub asset URL 302s to a CDN; HttpClient follows
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, ct);
            }

            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                          UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                          UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(tmp, target, overwrite: true);
            return (true, null);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            return (false, ex.Message);
        }
    }

    /// <summary>Relaunch the (now-updated) AppImage detached; the caller exits right after.</summary>
    public static void Restart()
    {
        if (AppImagePath is not { } target) return;
        try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = false })) { } }
        catch { /* ignore */ }
    }
}
