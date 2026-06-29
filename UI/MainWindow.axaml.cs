using Avalonia;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout window. Layout/bindings live in MainWindow.axaml; this holds
/// only the window behaviour: acrylic backdrop, light-dismiss on focus loss, and tray placement.
/// Width is fixed; height auto-sizes to content. Opening a sub-page (Options/Lighting) slides an
/// overlay over the main panel via a render transform, so the window never resizes or moves — that
/// avoids the DWM-blur flicker and the blurred strip a wider window would show.</summary>
public partial class MainWindow : Window
{
    private Size _lastSize;

    public MainWindow()
    {
        InitializeComponent();

        // On Win11 the DWM blurs the desktop behind the transparent window.
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];

        // Re-anchor the bottom-right corner to the tray whenever the size changes. The sub-page slide
        // is a transform (not a layout change), so this only runs on the rare content-height change.
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
            LastDismissedUtc = DateTime.UtcNow;
            Hide();
        };
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
