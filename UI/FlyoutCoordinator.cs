using Avalonia.Threading;
using AcerHelper.Localization;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>Owns the single flyout window and its lifecycle: opening at the tray corner, toggling,
/// light-dismiss, and the modal confirmations. Options/Lighting are NOT separate windows anymore —
/// they're pages inside the flyout, navigated purely via view-model state (<c>IsDrawerOpen</c>), so this
/// coordinator no longer choreographs a second window (no park/reveal/positioning). Keeps the window
/// behaviour out of <see cref="AppController"/>, which only needs to open/toggle/hide.</summary>
internal sealed class FlyoutCoordinator : IDisposable
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

    /// <summary>The desktop is ending the session and the flyout is closing for good: the app has to exit.
    /// Forwarded from the flyout window, which is the only place that can tell a session end from a close that
    /// merely hides it — <see cref="FlyoutClosePolicy"/> draws that line, and <see cref="MainWindow"/> applies it.</summary>
    public event Action? SessionEnding
    {
        add => _main.SessionEnding += value;
        remove => _main.SessionEnding -= value;
    }

    /// <summary>The flyout was brought UP onto the screen — a hidden-to-visible transition, not a re-activation
    /// of an already-open window. Raised by every way the app shows its window: the tray icon / its "Show" menu
    /// item, the Nitro toggle hotkey, the update tray item and a language rebuild re-opening what was open. The
    /// app is tray-resident and normally hides its window, so THIS transition is what "the app unfolds onto the
    /// screen" means here; there is no taskbar entry to restore from, so no separate minimized/restored event
    /// exists to hook. AppController listens to run an update check on it (see UpdateSchedule.OnWindowShown).</summary>
    public event Action? Shown;

    /// <summary>Tear the flyout window down for good — used when the UI is rebuilt for a live language switch.
    /// Unlike <see cref="HideAll"/> (which only hides it), this really closes the window and unhooks it.</summary>
    public void Dispose() => _main.Destroy();

    public void OpenMain()
    {
        if (_main.IsOpen) { _main.Activate(); return; }   // already on screen: no Shown (see the event)
        _main.Open();
        Shown?.Invoke();
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
    /// mouse-hook) so dragging in the dialog doesn't hide the flyout; restores it on close — but only if
    /// the flyout is still open: the Nitro key still works while the dialog is up (HideAll -> the flyout
    /// hides and stops its own watch), and re-installing the global WH_MOUSE_LL hook for a HIDDEN flyout
    /// would leave every system-wide mouse event routing through this process until the next open/close.</summary>
    public async Task EditFanCurveAsync(FanCurveDialogViewModel vm)
    {
        _main.SuppressDismiss = true;
        _main.SetOutsideWatch(false);
        try { await Views.FanCurveWindow.ShowAsync(_main, vm); }
        finally { if (_main.IsOpen) _main.SetOutsideWatch(true); }
    }

    /// <summary>Shown over the flyout for battery calibration. Steals focus (not a click "outside").</summary>
    public Task<bool> ConfirmCalibrationAsync()
    {
        _main.SuppressDismiss = true;
        return Views.ConfirmDialog.ShowAsync(_main,
            Loc.T("Start battery calibration?"),
            Loc.T("This runs a full charge then a full discharge cycle and can take several hours. Keep the "
            + "laptop plugged in and don't depend on it meanwhile. Turn the switch back off to stop."),
            Loc.T("Start"));
    }

    /// <summary>Shown over the flyout before the installer writes anything into /etc. The install is not one
    /// permission but several — udev rules and a tmpfiles.d entry that make specific kernel nodes group-writable,
    /// and, on an Acer, an acer-wmi options line that changes how the driver behaves at load — so the prompt
    /// LISTS them (the installer's own words, one row per file, see <see cref="HardwareAccessConsent"/>) rather
    /// than asking for a bare "hardware access" the user cannot see into. Answering no installs nothing and
    /// leaves the banner in place, so the offer can be taken later.
    ///
    /// Same idiom as <see cref="ConfirmCalibrationAsync"/> and <see cref="ConfirmDriverAsync"/> — a modal
    /// <see cref="Views.ConfirmDialog"/> over the flyout with light-dismiss suspended for it — because the user
    /// is already looking at the banner they clicked and a second window would lose them.</summary>
    public Task<bool> ConfirmHardwareAccessAsync()
    {
        _main.SuppressDismiss = true;
        return Views.ConfirmDialog.ShowAsync(_main,
            HardwareAccessConsent.Title(),
            HardwareAccessConsent.Message(),
            HardwareAccessConsent.ConfirmText());
    }

    /// <summary>Shown over the flyout before the app asks cardwire for GPU access — the Options drawer's "Use the
    /// discrete GPU" switch, whose own confirm this is (see <see cref="CardwireGpuAccessConsent"/>).
    ///
    /// Same idiom as the three above (a modal <see cref="Views.ConfirmDialog"/> over the flyout with light-dismiss
    /// suspended), and it needs it for a sharper reason than they do: this switch is the only one in the app whose
    /// consequence CANNOT BE UNDONE while the app runs. The words are the consent class's, not this file's, so
    /// that what the user agrees to is held to the translation table and to the four facts the prompt owes.</summary>
    public Task<bool> ConfirmCardwireGpuAccessAsync()
    {
        _main.SuppressDismiss = true;
        return Views.ConfirmDialog.ShowAsync(_main,
            CardwireGpuAccessConsent.Title(),
            CardwireGpuAccessConsent.Message(),
            CardwireGpuAccessConsent.ConfirmText());
    }

    /// <summary>Shown over the flyout before a GPU-mode (MUX) change. A MUX change is QUEUED and only takes
    /// effect on the next restart, and a wrong value can leave the screen black, so this is the explicit-consent
    /// gate for the shared <c>IGpuMux</c> port; the caller passes the warning sentence (a localization key).
    /// Same modal-over-flyout idiom as the four above.</summary>
    public Task<bool> ConfirmGpuMuxAsync(string warning)
    {
        _main.SuppressDismiss = true;
        return Views.ConfirmDialog.ShowAsync(_main,
            Loc.T("Change the GPU mode?"),
            Loc.T(warning),
            Loc.T("Change"));
    }

    /// <summary>Shown over the flyout to ask before installing a third-party driver. Names the driver and where it
    /// comes from, because that is somebody else's kernel software and the user is agreeing to install it — not to
    /// "enable a feature".</summary>
    public Task<bool> ConfirmDriverAsync(string name, string purpose, string sourceUrl)
    {
        _main.SuppressDismiss = true;
        return Views.ConfirmDialog.ShowAsync(_main,
            Loc.T("Install {0}?", name),
            Loc.T("{0} needs the {1} kernel driver from {2}. It is third-party software, shared with other tuning "
            + "tools, and Acer Helper will never update or remove it. You can install it yourself instead.",
                purpose, name, sourceUrl),
            Loc.T("Install"));
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
