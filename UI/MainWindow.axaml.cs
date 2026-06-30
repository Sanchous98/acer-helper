using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout window. Fully app-painted (no DWM): an ExperimentalAcrylicBorder
/// for the blur and a BoxShadow for the shadow, matching the side panels. Fixed size; this holds the
/// window behaviour: tray placement and foregrounding (light-dismiss is coordinated by AppController).</summary>
public partial class MainWindow : Window
{
    private const int ShadowMargin = 20;   // matches the Root Border Margin (room for the BoxShadow)
    private Size _lastSize;

    public MainWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];   // lets the acrylic border blur the desktop

        // Re-anchor the bottom-right corner to the tray when the size changes (effectively once).
        LayoutUpdated += (_, _) =>
        {
            if (Bounds.Size == _lastSize) return;
            _lastSize = Bounds.Size;
            Reanchor();
        };

        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    /// <summary>Set just before we programmatically open another of our windows (a side panel or the
    /// calibration dialog) so the focus change isn't treated as a click "outside". One-shot.</summary>
    public bool SuppressDismiss { get; set; }

    /// <summary>When the flyout last hid itself. Lets a tray click toggle instead of reopening.</summary>
    public DateTime LastDismissedUtc { get; private set; } = DateTime.MinValue;

    public void MarkDismissed() => LastDismissedUtc = DateTime.UtcNow;

    public void PositionNearTray()
    {
        Reanchor();
        ForceForeground();   // opened from the Nitro hotkey, our background process needs to force focus
    }

    private void Reanchor()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var wa = screen.WorkingArea;
        var s = screen.Scaling;
        var w = (int)(Bounds.Width * s);
        var h = (int)(Bounds.Height * s);
        int m = (int)(ShadowMargin * s);   // the visible card is inset by the shadow margin; offset so it stays 12px off the corner
        Position = new PixelPoint(
            Math.Max(wa.X, wa.X + wa.Width - w - 12 + m),
            Math.Max(wa.Y, wa.Y + wa.Height - h - 12 + m));
    }

    private void ForceForeground()
    {
        Activate();
        if (!OperatingSystem.IsWindows()) return;
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;

        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);
        try { SetForegroundWindow(hwnd); SetActiveWindow(hwnd); }
        finally { if (attached) AttachThreadInput(thisThread, fgThread, false); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
