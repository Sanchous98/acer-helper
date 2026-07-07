using System.Diagnostics;
using System.IO;

namespace AcerHelper;

/// <summary>Lightweight background "watch" mode (<c>--watch</c>): a tiny resident process that listens for
/// the Nitro key and launches the full UI on demand — so the button works even when AcerHelper isn't open,
/// without keeping the whole Avalonia app in memory. Started at logon by <see cref="Vendors.Generic.Autostart"/>.
///
/// Design: the watcher owns "launch when closed" only. When the full UI is already running it does nothing —
/// the running instance handles the toggle itself via its own hotkey listener (both the watcher's and the
/// UI's RawInput sinks receive the key; the watcher just yields to a live UI). So no IPC and no changes to
/// the UI are needed. The per-OS message pump lives in Watcher.Windows.cs / Watcher.Linux.cs.</summary>
internal static partial class Watcher
{
    /// <summary>Single-instance mutex the FULL UI holds while alive (see <see cref="Program"/>). The watcher
    /// only reads it (never owns it) to tell whether the UI is already running.</summary>
    internal const string UiMutexName = "AcerHelper_SingleInstance_8F1C";

    /// <summary>Mutex the watcher itself holds so only one watcher runs per session.</summary>
    internal const string WatcherMutexName = "AcerHelper_Watcher_8F1C";

    /// <summary>Run the watch loop (blocks until the process exits). OS-specific.</summary>
    public static partial void Run();

    /// <summary>Diagnostic log (temp file) — the watcher has no console/UI, so this is how we see what it does.</summary>
    internal static readonly string LogPath = Path.Combine(Path.GetTempPath(), "acerhelper-watch.log");
    internal static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}"); } catch { /* ignore */ }
    }

    /// <summary>Is the full UI already running? (Its single-instance mutex exists.)</summary>
    internal static bool UiRunning()
    {
        try { using var m = Mutex.OpenExisting(UiMutexName); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch { return false; }   // access-denied etc. -> assume not ours, let launch attempt decide
    }

    /// <summary>Launch the full UI (this same exe, no args). It shows its window on start.</summary>
    internal static void LaunchUi()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) { Log("LaunchUi: ProcessPath is null"); return; }
        try
        {
            using (Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })) { }
            Log($"LaunchUi: started {exe}");
        }
        catch (Exception ex) { Log($"LaunchUi FAILED: {ex.GetType().Name}: {ex.Message}"); }
    }
}
