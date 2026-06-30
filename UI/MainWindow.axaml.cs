using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout window — one card, sized to its content. The Options/Lighting
/// drawer is a SEPARATE window (<see cref="SidePanelWindow"/>) to the left, so this window never resizes
/// (resizing on X11 is async and races with repositioning => sideways jitter). This holds the window
/// behaviour: tray placement and foregrounding (light-dismiss is coordinated by AppController).
///
/// Open/close = plain <see cref="Window.Show"/>/<see cref="Window.Hide"/>; the WM animates the map/unmap
/// (KWin's Scale effect), but the card content starts transparent so that effect plays over nothing,
/// then <see cref="SlideFader"/> reveals the content.</summary>
public partial class MainWindow : FlyoutWindow
{
    private const int ShadowMargin = 20;   // matches the Root Border Margin (room for the BoxShadow)
    private Size _lastSize;

    public bool IsOpen { get; private set; }

    /// <summary>Set just before we programmatically open another of our windows (a side panel or the
    /// calibration dialog) so the focus change isn't treated as a click "outside". One-shot.</summary>
    public bool SuppressDismiss { get; set; }

    /// <summary>When the flyout last hid itself. Lets a tray click toggle instead of reopening.</summary>
    public DateTime LastDismissedUtc { get; private set; } = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        InitFlyout(Root);

        // Re-anchor the bottom-right corner to the tray when the content height changes (e.g. a wrapping
        // status line). Width is fixed, so X never moves — only Y. Only while open.
        LayoutUpdated += (_, _) =>
        {
            if (!IsOpen || Bounds.Size == _lastSize) return;
            _lastSize = Bounds.Size;
            Reanchor();
        };

        Closing += (_, e) => { e.Cancel = true; CloseFlyout(); };   // hide, never destroy

        // A click on the transparent shadow margin around the card (the Backdrop itself, not the card) is
        // a click "outside" the flyout -> dismiss. EXCEPT the left margin while the drawer is open: that's
        // the inner gap toward the drawer (between the two cards), not outside, so don't dismiss there.
        Backdrop.PointerPressed += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, Backdrop)) return;
            if (DrawerOpen && e.GetPosition(Backdrop).X < Root.Bounds.Left) return;
            BackgroundClicked?.Invoke();
        };
    }

    /// <summary>Raised when the user clicks the transparent margin around the card (outside the flyout).</summary>
    public event Action? BackgroundClicked;

    /// <summary>True while the Options/Lighting drawer is open to the left, so the left shadow margin is
    /// the inner gap (between the cards) rather than "outside" — clicks there must not dismiss.</summary>
    public bool DrawerOpen { get; set; }

    public void MarkDismissed() => LastDismissedUtc = DateTime.UtcNow;

    /// <summary>Show at the tray corner (content transparent so the WM map effect is invisible), then
    /// reveal the content with the slide+fade.</summary>
    public void Open()
    {
        IsOpen = true;
        if (!IsVisible) Show();
        UpdateLayout();          // force a layout pass so Bounds.Height is real before we anchor
        PositionNearTray();
        AnimateIn();             // reveal content (after Show -> attached, so the transition fires)
    }

    public void CloseFlyout()
    {
        if (!IsOpen) return;
        IsOpen = false;
        AnimateOut(Hide);        // fade content out, then hide the (now transparent) window
    }

    private void PositionNearTray()
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
        // Anchor the window flush to the working-area bottom-right, never spilling past it. If it spills
        // (right/bottom edge beyond the working area) KWin clamps it back and the two fight = jitter /
        // "creeping onto the next monitor". The 20px shadow margin is the visual gap from the edge.
        Position = new PixelPoint(
            Math.Max(wa.X, wa.X + wa.Width - w),
            Math.Max(wa.Y, wa.Y + wa.Height - h));
    }

    private void ForceForeground()
    {
        Activate();
        if (!OperatingSystem.IsWindows()) return;
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;

        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var thisThread = GetCurrentThreadId();
        var attached = fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);
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
