using Avalonia;
using Avalonia.Controls;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout window. Layout/bindings live in MainWindow.axaml; this holds
/// only the window behaviour: acrylic backdrop, light-dismiss on focus loss, and tray placement.
/// The window is a fixed size (the side-drawer space is always reserved) so opening/closing the
/// drawer is a pure render-transform slide that never resizes or moves the window — moving/resizing
/// an acrylic window makes the DWM blur flicker. It is positioned once, when shown.</summary>
public partial class MainWindow : Window
{
    private Size _lastSize;
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // On Win11 the DWM blurs the desktop behind the transparent window.
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];

        // Anchor the bottom-right corner to the tray. The window size is constant in normal use, so
        // this effectively runs once (at first layout); the drawer slide doesn't change the size.
        LayoutUpdated += (_, _) =>
        {
            if (Bounds.Size == _lastSize) return;
            _lastSize = Bounds.Size;
            Reanchor();
        };

        // While the drawer is closed its host is an empty transparent strip beside the panel; a click
        // there should dismiss the flyout, just like clicking outside the window.
        DrawerHost.PointerPressed += (_, _) =>
        {
            if (_vm is { IsDrawerOpen: false }) { LastDismissedUtc = DateTime.UtcNow; Hide(); }
        };

        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Deactivated += (_, _) =>
        {
            // Opening one of our own windows (e.g. the calibration confirm dialog) shifts focus off
            // the flyout but is an in-app action, not a click "outside" — skip that one dismissal.
            if (SuppressDismiss) { SuppressDismiss = false; return; }
            LastDismissedUtc = DateTime.UtcNow;
            Hide();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as MainViewModel;
    }

    /// <summary>Set just before we programmatically open another of our windows, so the focus change
    /// doesn't light-dismiss this flyout. One-shot — cleared on the next deactivation.</summary>
    public bool SuppressDismiss { get; set; }

    /// <summary>When the flyout last hid itself on losing focus. Lets a tray click act as a toggle
    /// instead of instantly reopening the panel the same click just dismissed.</summary>
    public DateTime LastDismissedUtc { get; private set; } = DateTime.MinValue;

    public void PositionNearTray() => Reanchor();

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
}
