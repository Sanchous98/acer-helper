using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout window. Frameless acrylic, fixed size (never resizes/moves, so
/// the backdrop never flickers). Holds the window behaviour: backdrop + rounded corners, tray
/// placement and foregrounding. Light-dismiss is coordinated by AppController (it must also consider
/// the side panel window's focus).</summary>
public partial class MainWindow : Window
{
    private Size _lastSize;

    public MainWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        Opened += (_, _) => WindowEffects.ApplyAcrylic(this);   // rounded corners + modern acrylic

        // Re-anchor the bottom-right corner to the tray when the size changes (effectively once).
        LayoutUpdated += (_, _) =>
        {
            if (Bounds.Size == _lastSize) return;
            _lastSize = Bounds.Size;
            Reanchor();
        };

        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    /// <summary>Set just before we programmatically open another of our windows (the side panel or the
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
        Position = new PixelPoint(
            Math.Max(wa.X, wa.X + wa.Width - w - 12),
            Math.Max(wa.Y, wa.Y + wa.Height - h - 12));
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
