using Avalonia.Threading;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>Owns the single flyout window and its lifecycle: opening at the tray corner, toggling,
/// light-dismiss, and the battery-calibration dialog. Options/Lighting are NOT separate windows anymore —
/// they're pages inside the flyout, navigated purely via view-model state (<c>IsDrawerOpen</c>), so this
/// coordinator no longer choreographs a second window (no park/reveal/positioning). Keeps the window
/// behaviour out of <see cref="AppController"/>, which only needs to open/toggle/hide.</summary>
internal sealed class FlyoutCoordinator
{
    private readonly MainWindow _main;
    private readonly MainViewModel _vm;

    public FlyoutCoordinator(MainViewModel vm)
    {
        _vm = vm;
        _main = new MainWindow { DataContext = vm };
        _main.Deactivated += (_, _) => MaybeDismiss();
        _main.BackgroundClicked += HideAll;
    }

    public bool IsMainOpen => _main.IsOpen;

    public void OpenMain()
    {
        if (_main.IsOpen) { _main.Activate(); return; }
        _main.Open();
    }

    /// <summary>Tray click: open if closed, hide if open. Skip reopen if the flyout just light-dismissed
    /// itself because this very click moved focus off it (else the tray could never close the panel).</summary>
    public void ToggleMain()
    {
        if (_main.IsOpen) { HideAll(); return; }
        if ((DateTime.UtcNow - _main.LastDismissedUtc).TotalMilliseconds < 300) return;
        OpenMain();
    }

    public void HideAll()
    {
        _vm.IsDrawerOpen = false;       // reset navigation to Home for next open
        _main.MarkDismissed();
        _main.CloseFlyout();            // fade the card out, then hide the window
    }

    public void ShowLighting()
    {
        // Lighting is a page of the flyout, not a separate top-level menu.
        OpenMain();
        _vm.OpenLightingCommand.Execute(null);
    }

    /// <summary>Modal fan-curve editor over the flyout. Suspends light-dismiss (focus steal + the global
    /// mouse-hook) so dragging in the dialog doesn't hide the flyout; restores it on close.</summary>
    public async Task EditFanCurveAsync(FanCurveDialogViewModel vm)
    {
        _main.SuppressDismiss = true;
        _main.SetOutsideWatch(false);
        try { await Views.FanCurveWindow.ShowAsync(_main, vm); }
        finally { _main.SetOutsideWatch(true); }
    }

    /// <summary>Shown over the flyout for battery calibration. Steals focus (not a click "outside").</summary>
    public Task<bool> ConfirmCalibrationAsync()
    {
        _main.SuppressDismiss = true;
        return Views.ConfirmDialog.ShowAsync(_main,
            "Start battery calibration?",
            "This runs a full charge then a full discharge cycle and can take several hours. Keep the "
            + "laptop plugged in and don't depend on it meanwhile. Turn the switch back off to stop.",
            "Start");
    }

    /// <summary>Light-dismiss: if focus left the flyout, the user clicked outside the app, so hide it.
    /// SuppressDismiss skips the one deactivation we cause by opening our own dialog.</summary>
    private void MaybeDismiss()
    {
        if (_main.SuppressDismiss) { _main.SuppressDismiss = false; return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (_main.IsActive) return;
            HideAll();
        }, DispatcherPriority.Background);
    }
}
