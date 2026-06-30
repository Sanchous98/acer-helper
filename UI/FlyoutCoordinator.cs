using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>Owns the two flyout windows (the main quick-settings flyout + the Options/Lighting drawer)
/// and all their choreography: opening at the tray corner, the drawer being pre-mapped behind the main
/// card and revealed without a fresh WM map animation, positioning, and light-dismiss. Keeps this fragile
/// window/compositor dance out of <see cref="AppController"/>, which only needs to open/toggle/hide.</summary>
internal sealed class FlyoutCoordinator
{
    private readonly MainWindow _main;
    private readonly SidePanelWindow _sidePanel;
    private readonly MainViewModel _vm;
    private bool _drawerShown;

    public FlyoutCoordinator(MainViewModel vm)
    {
        _vm = vm;
        _main = new MainWindow { DataContext = vm };
        _main.Deactivated += (_, _) => MaybeDismiss();
        _main.BackgroundClicked += HideAll;

        _sidePanel = new SidePanelWindow();
        _sidePanel.SetBack(vm.CloseDrawerCommand);
        _sidePanel.Deactivated += (_, _) => MaybeDismiss();

        _vm.PropertyChanged += OnVmChanged;
    }

    public bool IsMainOpen => _main.IsOpen;

    public void OpenMain()
    {
        if (_main.IsOpen) { _main.Activate(); return; }
        _main.Open();
        ParkDrawerBehindMain();   // pre-map the drawer window so opening it later needs no WM map animation
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
        _drawerShown = false;
        _main.DrawerOpen = false;
        _main.MarkDismissed();
        _main.CloseFlyout();            // sets IsOpen=false first (so UpdateSidePanel won't re-park), then fades + hides
        _vm.IsDrawerOpen = false;
        _sidePanel.AnimateOut(() => _sidePanel.Hide());   // fade the drawer out (if shown) and unmap it with the flyout
    }

    public void ShowLighting()
    {
        // Lighting is a side drawer of the main flyout, not a separate top-level menu.
        OpenMain();
        _vm.OpenLightingCommand.Execute(null);
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

    // ---- drawer ----

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDrawerOpen) or nameof(MainViewModel.DrawerContent))
            UpdateSidePanel();
    }

    /// <summary>Reconcile the (already-mapped, parked) drawer window with the view-model. Open => move it
    /// out flush-left of the main card and reveal it (no Show, so no WM map animation — just our slide);
    /// close => fade it out, then re-park it behind the main card. The window is only hidden when the whole
    /// flyout closes (HideAll). Content/title come from the VM's drawer.</summary>
    private void UpdateSidePanel()
    {
        if (!_main.IsOpen) return;               // flyout closing -> HideAll owns the teardown
        if (_vm.IsDrawerOpen)
        {
            _sidePanel.SetPanel(_vm.DrawerTitle, _vm.DrawerContent);
            PositionSide();                      // move out, left of the main card
            _main.SuppressDismiss = true;
            _main.DrawerOpen = true;             // main's left margin is now the inner gap -> don't dismiss there
            _sidePanel.AnimateIn();              // reveal (already mapped => no WM map animation)
            _drawerShown = true;
        }
        else if (_drawerShown)
        {
            _drawerShown = false;
            _main.DrawerOpen = false;
            _main.SuppressDismiss = true;
            _sidePanel.AnimateOut(() => _sidePanel.Position = DrawerParkPosition());  // fade, then re-park behind main
            _main.Activate();                    // keep main on top over the re-parked panel
        }
    }

    /// <summary>Map the drawer window NOW, transparent and parked behind the main card (the main window is
    /// wider, so it fully covers it). KWin's one-shot map animation thus plays on a transparent, hidden
    /// window — invisible. It's unmapped again only when the whole flyout closes.</summary>
    private void ParkDrawerBehindMain()
    {
        _main.SuppressDismiss = true;            // showing our own window isn't a click "outside"
        _sidePanel.Height = _main.Bounds.Height;
        _sidePanel.Position = DrawerParkPosition();
        if (!_sidePanel.IsVisible) _sidePanel.Show();
        _main.Activate();                        // keep main on top; the parked panel sits behind it
    }

    /// <summary>Where to park the (transparent) drawer so it's fully covered by the main card. Computed
    /// from the working area, NOT from <c>_main.Position</c> — the latter can still be stale (X11 moves
    /// are async), which parked the panel slightly left of the main card and left a transparent click-
    /// catching sliver. Flush bottom-right (narrower than the main card) tucks it into the main's right
    /// portion, fully behind it.</summary>
    private PixelPoint DrawerParkPosition()
    {
        var screen = _main.Screens.Primary ?? _main.Screens.All.FirstOrDefault();
        if (screen == null) return _main.Position;
        var wa = screen.WorkingArea;
        var s = screen.Scaling;
        int wPhys = (int)(_sidePanel.Width * s);
        int hPhys = (int)(_main.Bounds.Height * s);
        return new PixelPoint(
            Math.Max(wa.X, wa.X + wa.Width - wPhys),
            Math.Max(wa.Y, wa.Y + wa.Height - hPhys));
    }

    /// <summary>Pin the side panel so its card sits just left of the main card, top-aligned and the same
    /// height. The main window's right edge is flush to the working area, so its X is stable (no spill =>
    /// no WM clamp jitter), and the panel positioned relative to it (main.X − 368s) is stable too.</summary>
    private void PositionSide()
    {
        var screen = _main.Screens.Primary ?? _main.Screens.All.FirstOrDefault();
        double s = screen?.Scaling ?? 1.0;
        _sidePanel.Height = _main.Bounds.Height;     // DIP; identical margins => inner cards match height
        _sidePanel.Position = new PixelPoint(
            _main.Position.X - (int)(368 * s),
            _main.Position.Y);
    }

    /// <summary>Light-dismiss: if focus left BOTH the flyout and the drawer, the user clicked outside the
    /// app, so hide everything. SuppressDismiss skips the one deactivation we cause by opening our own
    /// window/dialog.</summary>
    private void MaybeDismiss()
    {
        if (_main.SuppressDismiss) { _main.SuppressDismiss = false; return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (_main.IsActive || _sidePanel.IsActive) return;
            HideAll();
        }, DispatcherPriority.Background);
    }
}
