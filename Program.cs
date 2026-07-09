using Avalonia;
using AcerHelper.UI;

namespace AcerHelper;

internal static class Program
{
    // Single-instance guard: only one live app at a time. Autostart relaunches the app periodically (Task
    // Scheduler is its watchdog) and a manual launch can race a self-update restart — both must no-op while a
    // live instance holds this.
    private const string SingleInstanceMutex = "AcerHelper_SingleInstance_8F1C";

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, SingleInstanceMutex, out bool isNew);
        if (!isNew)
        {
            // Another instance holds the lock. This also happens for a moment during a self-update RESTART:
            // AppImageUpdater.Restart() spawns the new build before the outgoing one has released the mutex.
            // Bailing instantly would lose that race and leave NOTHING running after an update — so wait briefly
            // for the outgoing instance to exit and take over. A genuine second launch just times out and exits.
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) return; }
            catch (AbandonedMutexException) { /* previous owner died without releasing -> we now hold it */ }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .LogToTrace()
                     .With(new Win32PlatformOptions
                     {
                         WinUICompositionBackdropCornerRadius = 12f
                     });
}
