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
    }

    public static Task ShowAsync(Window owner, FanCurveDialogViewModel vm)
        => new FanCurveWindow { DataContext = vm }.ShowDialog(owner);
}
