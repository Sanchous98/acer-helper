using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout window. Layout/bindings live in MainWindow.axaml; this holds
/// only the window behaviour: acrylic backdrop, light-dismiss on focus loss, tray placement, and the
/// side-drawer open/close. The drawer slides via a render transform (smooth, composited); the host's
/// width is changed in just two discrete steps so the window resizes/re-anchors once per open and
/// once per close — never per animation frame (which caused visible jitter).</summary>
public partial class MainWindow : Window
{
    private const double DrawerWidth = 360;
    private Size _lastSize;
    private DispatcherTimer? _collapse;
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // On Win11 the DWM blurs the desktop behind the transparent window.
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];

        // Keep the bottom-right corner pinned to the tray when the window size changes (drawer open/
        // close, or content height changes). The drawer's slide is a transform, not a layout change,
        // so it doesn't trigger this — only the two discrete width steps do.
        LayoutUpdated += (_, _) =>
        {
            if (Bounds.Size == _lastSize) return;
            _lastSize = Bounds.Size;
            Reanchor();
        };

        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Deactivated += (_, _) =>
        {
            // Opening one of our own windows (e.g. the calibration confirm dialog) shifts focus off
            // the flyout but is an in-app action, not a click "outside" — skip that one dismissal.
            if (SuppressDismiss) { SuppressDismiss = false; return; }
            // With a drawer open (and its popups, e.g. the colour picker), don't auto-dismiss; the
            // user closes the drawer explicitly. Avoids the whole flyout vanishing mid-edit.
            if (_vm is { IsDrawerOpen: true }) return;
            LastDismissedUtc = DateTime.UtcNow;
            Hide();
        };
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
        if (e.PropertyName != nameof(MainViewModel.IsDrawerOpen)) return;

        if (_vm!.IsDrawerOpen)
        {
            // Reserve the space (one resize + re-anchor); the panel then slides in via its transform.
            _collapse?.Stop();
            DrawerHost.Margin = new Thickness(0, 0, 8, 0);
            DrawerHost.Width = DrawerWidth;
        }
        else
        {
            // Let the panel slide back out first, then collapse the host (one resize + re-anchor).
            _collapse?.Stop();
            _collapse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _collapse.Tick += (_, _) =>
            {
                _collapse!.Stop();
                DrawerHost.Width = 0;
                DrawerHost.Margin = default;
            };
            _collapse.Start();
        }
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
