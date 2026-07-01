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
