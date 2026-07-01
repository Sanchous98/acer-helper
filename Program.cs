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
        if (!isNew) return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UsePlatformDetect()
                     .LogToTrace()
                     .With(new Win32PlatformOptions());
}
