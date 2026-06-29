using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout: one frameless acrylic window. The main card is pinned to the
/// tray; Options/Lighting open in a drawer to its left — the window expands once (clean resize on the
/// modern DWM acrylic backdrop) and the drawer card slides in via a content transform (smooth). This
/// holds the window behaviour: backdrop/corners, tray placement + foregrounding, light-dismiss, and
/// the drawer expand/slide.</summary>
public partial class MainWindow : Window
{
    private const double DrawerWidth = 368;   // 360 card + 8 gap
    private readonly TranslateTransform _slide = new();
    private DispatcherTimer? _anim;
    private Size _lastSize;
    private MainViewModel? _vm;
    private object? _shown;   // drawer content currently displayed (null = collapsed)

    public MainWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        DrawerCard.RenderTransform = _slide;

        // Modern translucent acrylic + rounded corners (once the native handle exists).
        Opened += (_, _) => WindowEffects.ApplyAcrylic(this);

        // Keep the bottom-right corner pinned to the tray when the size changes (drawer expand/collapse).
        LayoutUpdated += (_, _) =>
        {
            if (Bounds.Size == _lastSize) return;
            _lastSize = Bounds.Size;
            Reanchor();
        };

        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Deactivated += (_, _) =>
        {
            if (SuppressDismiss) { SuppressDismiss = false; return; }
            HideFlyout();
        };
    }

    /// <summary>Collapse the drawer and hide the flyout (light-dismiss, or the tray/hotkey toggling
    /// it shut). Marks the dismiss time so a tray click acts as a toggle.</summary>
    public void HideFlyout()
    {
        CollapseInstant();
        if (_vm != null) _vm.IsDrawerOpen = false;
        LastDismissedUtc = DateTime.UtcNow;
        Hide();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as MainViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDrawerOpen) or nameof(MainViewModel.DrawerContent))
            UpdateDrawer();
    }

    private void UpdateDrawer()
    {
        if (_vm == null) return;
        object? want = _vm.IsDrawerOpen ? _vm.DrawerContent : null;
        if (ReferenceEquals(want, _shown)) return;

        if (want == null)
        {
            // Close: slide the card out behind the main card, then collapse the window.
            _shown = null;
            SlideCard(0, DrawerWidth, () => DrawerHost.Width = 0);
        }
        else if (_shown == null)
        {
            // Open: show content, expand the window once (clean resize), slide the card in.
            _shown = want;
            DrawerContentHost.Content = want;
            DrawerTitle.Text = _vm.DrawerTitle;
            _slide.X = DrawerWidth;          // parked off the right edge (behind the main card)
            DrawerHost.Width = DrawerWidth;  // expand the window (one-time resize + re-anchor)
            SlideCard(DrawerWidth, 0, null);
        }
        else
        {
            // Switch: slide current out, swap content, slide new in.
            _shown = want;
            var title = _vm.DrawerTitle;
            SlideCard(0, DrawerWidth, () =>
            {
                DrawerContentHost.Content = want;
                DrawerTitle.Text = title;
                SlideCard(DrawerWidth, 0, null);
            });
        }
    }

    private void CollapseInstant()
    {
        _anim?.Stop();
        _shown = null;
        _slide.X = 0;
        DrawerHost.Width = 0;
    }

    /// <summary>Animate the drawer card's X (content transform — smooth, no window move).</summary>
    private void SlideCard(double fromDip, double toDip, Action? onDone)
    {
        _anim?.Stop();
        _slide.X = fromDip;

        var start = DateTime.UtcNow;
        var dur = TimeSpan.FromMilliseconds(180);
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _anim.Tick += (_, _) =>
        {
            double t = Math.Clamp((DateTime.UtcNow - start) / dur, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3);   // cubic ease-out
            _slide.X = fromDip + (toDip - fromDip) * e;
            if (t >= 1) { _anim!.Stop(); onDone?.Invoke(); }
        };
        _anim.Start();
    }

    /// <summary>Set just before we programmatically open another of our windows (e.g. the calibration
    /// dialog) so the focus change doesn't light-dismiss this flyout. One-shot.</summary>
    public bool SuppressDismiss { get; set; }

    /// <summary>When the flyout last hid itself. Lets a tray click toggle instead of reopening.</summary>
    public DateTime LastDismissedUtc { get; private set; } = DateTime.MinValue;

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
