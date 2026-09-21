using Avalonia.Controls;

namespace AcerHelper.UI.Views;

/// <summary>A minimal modal yes/no dialog (frameless acrylic, matching the flyout). Use
/// <see cref="ShowAsync"/>; it resolves to true only if the user picks the confirm button.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        CancelButton.Click  += (_, _) => Close(false);
        ConfirmButton.Click += (_, _) => Close(true);
        // CenterOwner is centred in device pixels and then clamped against the LOGICAL work area, which on a
        // scaled XWayland pulls this dialog out of the flyout it belongs to (PopupPlacement.RecenterDialog).
        // Opened, not the constructor: the startup position exists only once the window has been shown.
        // WindowFlags first: the XAML flags do not reach a live X11 window (see WindowFlags), so this dialog would
        // otherwise take a taskbar entry of its own. Then the centring.
        Opened += (_, _) => { WindowFlags.Apply(this); PopupPlacement.RecenterDialog(this); };
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, string confirmText)
    {
        var dlg = new ConfirmDialog
        {
            TitleText =
            {
                Text = title
            },
            MessageText =
            {
                Text = message
            },
            ConfirmButton =
            {
                Content = confirmText
            }
        };
        return dlg.ShowDialog<bool>(owner);
    }
}
