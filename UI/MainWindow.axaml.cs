using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AcerHelper.Infrastructure;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>The one flyout window — a fixed-size "phone frame" holding an in-window navigation stack
/// (Home / Options / Lighting push over each other; see MainWindow.axaml). It never resizes at runtime
/// (resizing on X11 is async and races with repositioning => sideways jitter), so its anchored corner is
/// set once on open. This holds the window behaviour: tray placement and foregrounding; light-dismiss is
/// coordinated by <see cref="FlyoutCoordinator"/>.
///
/// Open/close = plain <see cref="Window.Show"/>/<see cref="Window.Hide"/> (instant — no app-level reveal).
/// The in-window Home <-> drawer swap DOES animate (a 0.24 s push), and this class makes that motion cheap:
/// see <see cref="ArmSlideCache"/> for why the two page grids carry a <see cref="BitmapCache"/> for exactly
/// the duration of a slide and not a frame longer.</summary>
public partial class MainWindow : Window
{
    public bool IsOpen { get; private set; }

    private bool _destroying;   // set by Destroy() so the Closing handler lets a real close through (see below)

    // ---- the push slide's render cache (see ArmSlideCache) ----
    // A shared BitmapCache instance is fine: BitmapCache caches per-compositor internally and the two page
    // grids live on the same compositor. Reusing it avoids re-allocating one per slide.
    private readonly BitmapCache _slideCacheMode = new();
    // Clears the cache once the slide has settled. A ONE-SHOT use of the app's shared timer primitive —
    // Stop() on the first tick — never a raw DispatcherTimer (which the suite forbids: it defaults to the
    // starvable Background priority). Restart() on each slide is what coalesces a rapid re-switch.
    private readonly PeriodicSchedule _slideCache;
    private MainViewModel? _navVm;   // the VM whose IsDrawerOpen drives the slide; subscribed in BindNavigation

    /// <summary>Set just before we programmatically open another of our windows (the calibration dialog)
    /// so the focus change isn't treated as a click "outside". One-shot.</summary>
    public bool SuppressDismiss { get; set; }

    /// <summary>When the flyout last hid itself. Lets a tray click toggle instead of reopening.</summary>
    public DateTime LastDismissedUtc { get; private set; } = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        // The push slide's render cache (see ArmSlideCache): a one-shot timer that clears the cache once
        // the 0.24 s slide has settled. A hair longer than the transition plus a frame of slack.
        _slideCache = new PeriodicSchedule(DisarmSlideCache, TimeSpan.FromMilliseconds(SlideCacheMs), UiSchedule.Normal);

        // The slide is driven by IsDrawerOpen; arm the cache when it flips (BindNavigation subscribes once
        // the DataContext arrives — FlyoutCoordinator sets it as an object initializer, so this fires here).
        DataContextChanged += (_, _) => BindNavigation();

        // Hide, never destroy (unless torn down for a rebuild) — but never refuse the SESSION either: a close the
        // desktop initiated means it is going away, and cancelling that is what made KDE report "session end
        // cancelled by an application" and refuse to switch the machine off until the app was quit by hand.
        // Which reasons hide and which must go through is FlyoutClosePolicy's single decision.
        // The flyout is a tray panel: it must NOT take a taskbar entry. ShowInTaskbar/Topmost from the XAML do not
        // reach a live X11 window (measured — see WindowFlags), and without this the owner could minimise the flyout
        // from the taskbar and never get it back.
        Opened += (_, _) => WindowFlags.Apply(this);

        Closing += (_, e) =>
        {
            if (FlyoutClosePolicy.HideInsteadOfClose(_destroying, e.CloseReason))
            {
                e.Cancel = true;             // the tray rule: a window close hides the flyout, the app stays up
                CloseFlyout();
                return;
            }
            if (_destroying) return;         // our own teardown (a UI rebuild): this close is exactly the intent

            // Posted rather than invoked here: this handler runs inside the window's own close, and the teardown
            // it leads to (AppController.ExitApp -> desktop.Shutdown) closes the windows again. Letting this close
            // finish first keeps the two apart.
            Dispatcher.UIThread.Post(() => SessionEnding?.Invoke());
        };

        // The window is SizeToContent, so switching to a taller page (e.g. the fan Curve editor) grows it.
        // It's anchored by its top-left, so growth would push the bottom off-screen — re-anchor on every size
        // change while open so the bottom-right corner stays put (the window grows upward instead).
        SizeChanged += (_, _) => { if (IsOpen) Reanchor(); };

        // A click on the transparent shadow margin around the card (the Backdrop itself, not the card) is
        // a click "outside" the flyout -> dismiss, like clicking outside it.
        Backdrop.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Backdrop)) BackgroundClicked?.Invoke();
        };

        // The bell's list dismisses on a click outside it. The dismiss layer is a sibling UNDER the list (see
        // the XAML), so a press that reaches it cannot have landed on the list itself — no hit-testing against
        // the panel is needed, and a press on the Install button, on an expanding row or on the changelog's
        // scrollbar stays with the list. The command is a one-way close, never the bell's toggle: an outside
        // click must not open a list that is already away.
        NotificationDismissLayer.PointerPressed += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.CloseNotificationsCommand.Execute(null);
        };
    }

    /// <summary>Raised when the user clicks the transparent margin around the card (outside the flyout).</summary>
    public event Action? BackgroundClicked;

    /// <summary>Raised when the DESKTOP is ending the session and this window is being closed for good (KDE's
    /// logout, Windows' session end) — never for a close that only hides the flyout.
    ///
    /// The app has to exit on it, and nothing else will do that for us: the lifetime is OnExplicitShutdown and the
    /// tray keeps the process alive with no window at all, so an app that merely stops showing a window stays
    /// running — which the session manager reports as the application refusing to quit.</summary>
    public event Action? SessionEnding;

    public void MarkDismissed() => LastDismissedUtc = DateTime.UtcNow;

    /// <summary>Show at the tray corner (instant — no reveal animation).</summary>
    public void Open()
    {
        IsOpen = true;
        if (!IsVisible) Show();
        UpdateLayout();          // force a layout pass so Bounds is real before we anchor
        Reanchor();

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

    /// <summary>Really close and dispose the window (bypassing the hide-on-close behaviour), unhooking the
    /// low-level mouse hook first. Used when the app rebuilds its UI for a live language switch.</summary>
    public void Destroy()
    {
        CloseFlyout();           // hide + unhook the outside-click mouse hook
        _slideCache.Stop();      // a pending disarm must not fire against a torn-down tree
        _slideCache.Dispose();
        UnbindNavigation();
        _destroying = true;      // let the Closing handler perform a real close this time
        Close();
    }

    // ---- the push slide's render cache ----
    //
    // WHY. The Home <-> drawer swap is a 0.24 s RenderTransform transition over two full-card trees that carry
    // several ExperimentalAcrylicBorder cards each. Without a cache, every animated frame re-samples the acrylic
    // and re-rasterizes the clip — the owner's "переключает экран, но как-то дёргано, как-будто поводов для
    // тормозов нет" (there is no logical cost at all; the cost was the re-render). Avalonia's compositor honours
    // Visual.CacheMode: with a BitmapCache the subtree is drawn into an offscreen layer once and blitted per
    // frame (ServerCompositionVisualCache.Draw + ServerCompositionVisual.Render's PreSubgraph "draw from cache
    // and skip rendering"), and a RenderTransform change is a compositor transform that does NOT invalidate that
    // layer (ServerCompositionVisual.DirtyInputs: transform fields never set _contentChanged). So the slide
    // becomes ~2 rasterizations (arm + disarm) and ~14 texture blits instead of ~15 full re-renders.
    //
    // WHY SCOPED TO THE SLIDE, NOT ALWAYS-ON. A cache bakes its layer, so text is grayscale-antialiased and the
    // acrylic material is fixed for the frames it covers (BitmapCache.EnableClearType defaults false; the layer
    // is created at Root.Scaling, so it is still DPR-sharp — only subpixel AA is lost). That is invisible for a
    // quarter-second of motion and would be wrong for the static page the user then reads. So the cache is set
    // when the slide starts and cleared once it has settled, and the page sits at full fidelity the rest of the
    // time. The window shows/hides instantly and its content on open/close is NOT cached.
    private const int SlideCacheMs = 300;   // the 0.24 s transition plus a frame of slack

    private void BindNavigation()
    {
        UnbindNavigation();
        if (DataContext is MainViewModel vm)
        {
            _navVm = vm;
            _navVm.PropertyChanged += OnNavigationChanged;
        }
    }

    private void UnbindNavigation()
    {
        if (_navVm != null) _navVm.PropertyChanged -= OnNavigationChanged;
        _navVm = null;
    }

    /// <summary>The push began (IsDrawerOpen flipped): cache both animated page trees for the duration of the
    /// slide, then clear the cache. Restart coalesces a rapid re-switch into one fresh window — the transition
    /// itself restarts on each class change too, so clearing after the last one is exactly right.</summary>
    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsDrawerOpen)) return;
        ArmSlideCache();
    }

    private void ArmSlideCache()
    {
        HomePage.CacheMode = _slideCacheMode;
        DrawerPage.CacheMode = _slideCacheMode;
        _slideCache.Restart();
    }

    private void DisarmSlideCache()
    {
        _slideCache.Stop();              // one shot: the first tick is the whole job
        HomePage.CacheMode = null;
        DrawerPage.CacheMode = null;
    }

    private void Reanchor()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var s = screen.Scaling;
        var w = (int)(Bounds.Width * s);
        var h = (int)(Bounds.Height * s);
        var gap = (int)(20 * s);   // 20 DIP visual gap from the screen corner (card is no longer margined)
        // WHICH SPACE THE WORKING AREA ARRIVES IN IS MEASURED, NOT ASSUMED — because it is not a property of the
        // display, it is a property of what the toolkit happens to report.
        //
        // This used to hard-code the factor. The first measurement said WorkingArea.Width comes back as 2752 on
        // a 3440-wide output, i.e. LOGICAL units, so the area was multiplied by the scale; the unconverted
        // arithmetic had put the flyout at x=2142 instead of 2830, a quarter of the screen short of the corner,
        // which read as "the window sits near the middle". Then the SAME session reported the same area as 3440
        // — DEVICE pixels — and that multiplication asked for x=3690: past the right edge, and the flyout came
        // up off the screen. Nothing about the display, the scale or the layout had changed; only what the
        // toolkit reported. So the space is now OBSERVED: PopupPlacement.SpaceOf compares the area against the
        // output's own rectangle (Screen.Bounds, the one report that is the same in both spaces), and
        // PopupPlacement.Settle places the window there, reads its position back, and falls back to the other
        // space if the compositor moved it. A 1.0-scale session is unaffected either way (both candidates are
        // the same corner there), and on the Windows TFM none of this differs from before.
        //
        // (THE SAME DISAGREEMENT IS NOT ONLY HERE: popups and the two CenterOwner modals are placed by the
        // toolkit and land wrong on this session for the same reason — the correction for those, and the
        // measurement it comes from, is in PopupPlacement.cs, which is the sibling of this method.)
        Position = PopupPlacement.Settle(
            screen.WorkingArea, screen.Bounds, new PixelSize(w, h), gap, s,
            place: ask => { Position = ask; return Position; }).Position;
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

    /// <summary>Suspend/resume the outside-click watch — used while we show our own modal (the fan-curve
    /// editor) so interacting with it isn't treated as a click "outside" that dismisses the flyout.</summary>
    public void SetOutsideWatch(bool on) { if (on) StartOutsideWatch(); else StopOutsideWatch(); }

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
            // "Outside" means outside EVERYTHING we own. Avalonia opens dropdowns/pickers (the colour picker,
            // combo boxes, tooltips) as separate popup HWNDs that can extend past the main window — a click
            // there must NOT dismiss. Treat any HWND belonging to our process as "inside".
            if (IsOpen && !PointInWindow(x, y) && !PointInOurProcess(x, y))
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

    // Does the click land on a window owned by THIS process (main window OR any Avalonia popup — colour
    // picker, combo dropdown, tooltip)? Those popups are separate top-level HWNDs, so the main-window rect
    // check alone misses them and a click there would wrongly dismiss the flyout.
    private static bool PointInOurProcess(int px, int py)
    {
        var hwnd = WindowFromPoint(new POINT { X = px, Y = py });
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == GetCurrentProcessId();
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
}
