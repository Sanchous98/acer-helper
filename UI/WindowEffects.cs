using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>Windows-only window chrome via DWM, not exposed by Avalonia: rounded corners and the
/// modern system-backdrop material. The modern Acrylic backdrop (DWMSBT_TRANSIENTWINDOW) is
/// translucent (shows the windows behind) AND is composited by DWM, so unlike the legacy acrylic it
/// resizes/moves cleanly without the flicker.</summary>
internal static class WindowEffects
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2;       // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic (translucent, see-through)

    /// <summary>Round the corners and apply the modern translucent Acrylic backdrop.</summary>
    public static void ApplyAcrylic(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
