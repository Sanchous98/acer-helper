using Avalonia.Controls;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI.Views;

/// <summary>Modal fan-curve editor (frameless acrylic, matching the flyout). Hosts the drag-graph and a
/// "Follow curve" switch, bound to a <see cref="FanCurveDialogViewModel"/>; edits apply/persist live, so the
/// dialog just needs a Done button to dismiss.</summary>
public partial class FanCurveWindow : Window
{
    public FanCurveWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        // CenterOwner is centred in device pixels and then clamped against the LOGICAL work area, which on a
        // scaled XWayland pulls this dialog out of the flyout it belongs to (PopupPlacement.RecenterDialog).
        // Opened, not the constructor: the startup position exists only once the window has been shown.
        // WindowFlags first: the XAML flags do not reach a live X11 window (see WindowFlags), so this dialog would
        // otherwise take a taskbar entry of its own. Then the centring.
        Opened += (_, _) => { WindowFlags.Apply(this); PopupPlacement.RecenterDialog(this); };
    }

    public static Task ShowAsync(Window owner, FanCurveDialogViewModel vm)
        => new FanCurveWindow { DataContext = vm }.ShowDialog(owner);
}
