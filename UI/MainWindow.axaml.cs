using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AcerHelper.UI;

/// <summary>The one flyout window — a fixed-size "phone frame" holding an in-window navigation stack
/// (Home / Options / Lighting push over each other; see MainWindow.axaml). It never resizes at runtime
/// (resizing on X11 is async and races with repositioning => sideways jitter), so its anchored corner is
/// set once on open. This holds the window behaviour: tray placement and foregrounding; light-dismiss is
/// coordinated by <see cref="FlyoutCoordinator"/>.
///
/// Open/close = plain <see cref="Window.Show"/>/<see cref="Window.Hide"/> (instant — no app-level reveal;
/// only the in-window page-push transitions animate).</summary>
public partial class MainWindow : Window
{
    public bool IsOpen { get; private set; }

    /// <summary>Set just before we programmatically open another of our windows (the calibration dialog)
    /// so the focus change isn't treated as a click "outside". One-shot.</summary>
    public bool SuppressDismiss { get; set; }

    /// <summary>When the flyout last hid itself. Lets a tray click toggle instead of reopening.</summary>
    public DateTime LastDismissedUtc { get; private set; } = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

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

    /// <summary>Show at the tray corner (instant — no reveal animation).</summary>
    public void Open()
    {
        IsOpen = true;
        if (!IsVisible) Show();
        UpdateLayout();          // force a layout pass so Bounds is real before we anchor
        Reanchor();
        ApplyRoundedBlur();      // Linux/X11 (KDE): round the KWin blur to the card; elided on Windows

        // Grab focus AFTER the window is mapped, not synchronously inside Open(): doing it in the same call
        // as Show() loses the race (the HWND isn't ready to take foreground yet), so a hotkey-opened flyout
        // came up unfocused and thus never got Activated -> Deactivated never fired -> clicking outside
        // didn't dismiss it. Posting defers the grab to after the map completes.
        Dispatcher.UIThread.Post(ForceForeground, DispatcherPriority.Input);
        StartOutsideWatch();     // dismiss on outside click even if we never got focus (hotkey opens unfocused)
    }

    public void CloseFlyout()
    {
        if (!IsOpen) return;
        IsOpen = false;
        StopOutsideWatch();
        Hide();                  // hide instantly (no fade-out)
    }

    // Rounds the KWin blur region to the card on Linux/X11 (see MainWindow.Linux.cs); elided on Windows.
    partial void ApplyRoundedBlur();

    private void Reanchor()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var wa = screen.WorkingArea;
        var s = screen.Scaling;
        var w = (int)(Bounds.Width * s);
        var h = (int)(Bounds.Height * s);
        var gap = (int)(20 * s);   // 20 DIP visual gap from the screen corner (card is no longer margined)
        // Anchor near the working-area bottom-right with the gap, never spilling past it. If it spills
        // (right/bottom edge beyond the working area) KWin clamps it back and the two fight = jitter /
        // "creeping onto the next monitor".
        Position = new PixelPoint(
            Math.Max(wa.X, wa.X + wa.Width - w - gap),
            Math.Max(wa.Y, wa.Y + wa.Height - h - gap));
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
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
            SetFocus(hwnd);
        }
        finally { if (attached) AttachThreadInput(thisThread, fgThread, false); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    // ---- outside-click dismiss (Windows) ----
    // The flyout can open WITHOUT focus (from the Nitro global hotkey), so Deactivated never fires and a
    // click outside wouldn't dismiss it. A low-level mouse hook sees clicks anywhere while open and dismisses
    // when they land outside the window — independent of focus. Active only while the flyout is open.
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;

    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseProc;   // keep the delegate alive so the GC can't collect the callback

    private void StartOutsideWatch()
    {
        if (!OperatingSystem.IsWindows() || _mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookProc;
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, GetModuleHandleW(null), 0);
    }

    private void StopOutsideWatch()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            int x = Marshal.ReadInt32(lParam);       // MSLLHOOKSTRUCT.pt.x (screen px, offset 0)
            int y = Marshal.ReadInt32(lParam, 4);    //                 .pt.y (offset 4)
            if (IsOpen && !PointInWindow(x, y))
                Dispatcher.UIThread.Post(() => BackgroundClicked?.Invoke());
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool PointInWindow(int px, int py)
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        var s = screen?.Scaling ?? 1.0;
        int w = (int)(Bounds.Width * s), h = (int)(Bounds.Height * s);
        var p = Position;
        return px >= p.X && px < p.X + w && py >= p.Y && py < p.Y + h;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
