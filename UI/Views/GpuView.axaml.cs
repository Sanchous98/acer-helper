using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI.Views;

/// <summary>
/// The GPU-overclock section's view. The only code here is what a slider cannot express: the EXACT-entry
/// TextBoxes beside the two offsets commit on Enter and on leaving the box (a slider drag cannot land finer than
/// one pixel of its track — at the driver's full range that is several MHz). The commit itself is the view
/// model's (<c>ApplyCoreText</c>/<c>ApplyMemText</c>), so the debounce/apply/persist path is the same one a drag
/// takes; this class only decides WHEN to call it and routes Enter to it explicitly.
///
/// WHY THE VIEW NAMES ITS OWN CONTROLS' HANDLERS. Avalonia's built-in <c>LostFocus</c>/<c>KeyDown</c> are not
/// bindable commands, so the alternative would be a behaviour or a code-behind hook in the view model — both
/// heavier than two named handlers on a view that already has code-behind for a reason.
/// </summary>
public partial class GpuView : UserControl
{
    public GpuView() => InitializeComponent();

    private void OnCoreKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) (DataContext as GpuViewModel)?.ApplyCoreText();
    }

    private void OnMemKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) (DataContext as GpuViewModel)?.ApplyMemText();
    }

    private void OnCoreLostFocus(object? sender, RoutedEventArgs e)
        => (DataContext as GpuViewModel)?.ApplyCoreText();

    private void OnMemLostFocus(object? sender, RoutedEventArgs e)
        => (DataContext as GpuViewModel)?.ApplyMemText();
}
