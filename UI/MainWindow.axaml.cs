using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>The one flyout window — a fixed-size "phone frame" holding an in-window navigation stack
/// (Home / Options / Lighting push over each other; see MainWindow.axaml). It never resizes at runtime
/// (resizing on X11 is async and races with repositioning => sideways jitter), so its anchored corner is
/// set once on open. This holds the window behaviour: tray placement, foregrounding, and the card's
/// open/close reveal; light-dismiss is coordinated by <see cref="FlyoutCoordinator"/>.
///
/// Open/close = plain <see cref="Window.Show"/>/<see cref="Window.Hide"/>; the WM animates the map/unmap
/// (KWin's Scale effect), but the card content starts transparent so that effect plays over nothing,
/// then <see cref="SlideFader"/> reveals the content.</summary>
public partial class MainWindow : Window
{
    private readonly SlideFader _fader;

    public bool IsOpen { get; private set; }

    /// <summary>Set just before we programmatically open another of our windows (the calibration dialog)
    /// so the focus change isn't treated as a click "outside". One-shot.</summary>
    public bool SuppressDismiss { get; set; }

    /// <summary>When the flyout last hid itself. Lets a tray click toggle instead of reopening.</summary>
    public DateTime LastDismissedUtc { get; private set; } = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        _fader = new SlideFader(Root);

        Closing += (_, e) => { e.Cancel = true; CloseFlyout(); };   // hide, never destroy

        // A click on the transparent shadow margin around the card (the Backdrop itself, not the card) is
        // a click "outside" the flyout -> dismiss, like clicking outside it.
        Backdrop.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Backdrop)) BackgroundClicked?.Invoke();
        };
    }

    /// <summary>Raised when the user clicks the transparent margin around the card (outside the flyout).</summary>
    public event Action? BackgroundClicked;

    public void MarkDismissed() => LastDismissedUtc = DateTime.UtcNow;

    /// <summary>Show at the tray corner (content transparent so the WM map effect is invisible), then
    /// reveal the content with the slide+fade.</summary>
    public void Open()
    {
        IsOpen = true;
        if (!IsVisible) Show();
        UpdateLayout();          // force a layout pass so Bounds is real before we anchor
        PositionNearTray();
        _fader.In();             // reveal content (after Show -> attached, so the transition fires)
    }

    public void CloseFlyout()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _fader.Out(Hide);        // fade content out, then hide the (now transparent) window
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
