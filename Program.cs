using Avalonia;
using AcerHelper.UI;

namespace AcerHelper;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // single instance
        using var mutex = new Mutex(true, "AcerHelper_SingleInstance_8F1C", out bool isNew);
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
