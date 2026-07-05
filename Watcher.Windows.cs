using System.Runtime.InteropServices;
using AcerHelper.Features;
using AcerHelper.Vendors.Acer;

namespace AcerHelper;

// Windows watch mode: a message-only RawInput listener (reusing AcerHotkeys) plus a Win32 message pump,
// so WM_INPUT is delivered even though there's no Avalonia loop. On the Nitro key it launches the full UI
// (only if it isn't already running — a live UI handles its own toggle). Turbo/other keys are ignored here.
internal static partial class Watcher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int ptX; public int ptY;
    }

    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);

    public static partial void Run()
    {
        // one watcher per session
        using var wmutex = new Mutex(true, WatcherMutexName, out bool isNew);
        if (!isNew) return;

        var last = DateTime.MinValue;

        // AcerHotkeys creates the HWND_MESSAGE window + RawInput registration on THIS thread; the pump below
        // feeds it. Keep it alive for the whole loop.
        using var keys = new AcerHotkeys();
        keys.Pressed += action =>
        {
            if (action != HotkeyAction.ToggleWindow) return;   // only the Nitro key concerns the watcher

            var now = DateTime.UtcNow;
            if ((now - last).TotalMilliseconds < 600) return;   // debounce (the key can repeat)
            last = now;

            // If the UI is already up, its own hotkey listener toggles the window — don't launch a 2nd instance.
            if (!UiRunning()) LaunchUi();
        };

        // Standard Win32 message pump; exits if the window posts WM_QUIT (it won't, so this runs for the
        // lifetime of the logon session / until the process is killed).
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }
}
