using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout window. Layout/bindings live in MainWindow.axaml; this holds
/// the window behaviour: acrylic backdrop, tray placement, and forcing itself to the foreground when
/// shown. The window never resizes or moves after placement (so the DWM blur never flickers); the
/// side panels are separate windows managed by AppController. Light-dismiss is also driven by
/// AppController (it must consider the side windows' focus too).</summary>
public partial class MainWindow : Window
{
    private Size _lastSize;

    public MainWindow()
    {
        InitializeComponent();

        // Mica (not Acrylic): Mica samples only the desktop wallpaper, so it does NOT show the windows
        // behind it — which means a side panel sliding behind this window is properly occluded (no
        // bleed-through), unlike acrylic which would blur the panel into view. Falls back to acrylic.
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
        ];

        // Round the acrylic backdrop's corners to match the rounded content border.
        Opened += (_, _) => WindowEffects.RoundCorners(this);

        // Re-anchor the bottom-right corner to the tray when the size changes (effectively once).
        LayoutUpdated += (_, _) =>
        {
            if (Bounds.Size == _lastSize) return;
            _lastSize = Bounds.Size;
            Reanchor();
        };

        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    /// <summary>Set just before we programmatically open another of our windows, so the focus change
    /// doesn't light-dismiss this flyout. One-shot — cleared by AppController on the next deactivation.</summary>
    public bool SuppressDismiss { get; set; }

    /// <summary>When the flyout last hid itself. Lets a tray click act as a toggle instead of instantly
    /// reopening the panel the same click just dismissed.</summary>
    public DateTime LastDismissedUtc { get; private set; } = DateTime.MinValue;

    public void MarkDismissed() => LastDismissedUtc = DateTime.UtcNow;

    public void PositionNearTray()
    {
        Reanchor();
        // When opened from the Nitro hotkey our process isn't the foreground app, so a plain Activate()
        // is ignored by Windows and the window never gets focus — meaning it never receives Deactivated
        // and so can't light-dismiss. Force it to the foreground explicitly.
        ForceForeground();
    }

    private void Reanchor()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var wa = screen.WorkingArea;             // physical pixels
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

        // The AttachThreadInput dance lets a background process legitimately take the foreground:
        // attach our input queue to the current foreground thread's, then SetForegroundWindow succeeds.
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
