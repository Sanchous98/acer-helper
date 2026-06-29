using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>Windows-only window chrome tweaks not exposed by Avalonia. Currently: ask DWM to round the
/// window (and its acrylic/mica backdrop) corners, so a frameless transparent window doesn't show
/// square blurred corners behind the rounded content border.</summary>
internal static class WindowEffects
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void RoundCorners(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
