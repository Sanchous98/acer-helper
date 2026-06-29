using Avalonia;
using Avalonia.Controls;

namespace AcerHelper.UI;

/// <summary>The quick-settings flyout window. Layout/bindings live in MainWindow.axaml; this holds
/// only the window behaviour: acrylic backdrop, light-dismiss on focus loss, and tray placement.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // On Win11 the DWM blurs the desktop behind the transparent window.
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];

        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Deactivated += (_, _) =>
        {
            // Opening one of our own windows (e.g. Lighting) shifts focus off the flyout but is an
            // in-app action, not a click "outside" — skip that one dismissal.
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

    public void PositionNearTray()
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
