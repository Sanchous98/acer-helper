using System.Diagnostics;
using AcerHelper.Features;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>Composition root for the running app: builds the view-model from the device, owns the tray
/// (<see cref="TrayController"/>), the windows (<see cref="FlyoutCoordinator"/>) and the refresh loop, and
/// routes hotkeys + profile/fan actions to the <see cref="LaptopService"/>.</summary>
internal sealed class AppController
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly LaptopService _svc;
    private readonly MainViewModel _vm;
    private readonly FlyoutCoordinator _windows;
    private readonly TrayController _tray;
    private readonly DispatcherTimer _timer;
    private readonly UpdateChecker _updates = new();

    private DateTime _lastTurbo = DateTime.MinValue;
    private DateTime _lastNitro = DateTime.MinValue;
    private string? _lastModeKey;   // performance mode we last applied per-mode presets for

    public AppController(IClassicDesktopStyleApplicationLifetime desktop, LaptopService svc)
    {
        _desktop = desktop;
        _svc = svc;

        var d = _svc.Device;
        // The Lighting window hosts RGB zones AND/OR a plain (non-RGB) keyboard-backlight brightness control,
        // so it opens whenever the device has either.
        var lighting = d.Lighting != null || d.KeyboardBrightness != null
            ? new LightingViewModel(d.Lighting, _svc.LightsForCurrentMode(), _svc.PersistLighting,
                                    d.KeyboardBrightness, _svc.SetKeyboardBrightness)
            : null;
        var opts = new OptionsAssembler(_svc, Notify, ConfirmCalibrationAsync);
        var fan0 = _svc.CurrentFan();   // current mode's fan preset (defaults if none saved)
        _vm = new MainViewModel(d, new UiActions(
            ApplyProfile, SetTurbo, ApplyFan,
            (m, c, g) => _svc.PersistFan((FanMode)m, (byte)c, (byte)g),
            opts.Toggles(), opts.Choices(),
            () => d.Clamshell?.Enabled ?? false, b => _svc.SetClamshell(b),
            _svc.Settings.TurboToggles, SetTurboToggles,
            () => d.Autostart?.IsEnabled() ?? false, b => _svc.SetAutostart(b),
            fan0.Mode, fan0.Cpu, fan0.Gpu,
            d.BatteryInfo != null, opts.BatteryLimit(), opts.BatteryCalibration(), opts.BatteryChargeMode()),
            lighting);

        _windows = new FlyoutCoordinator(_vm);
        _lastModeKey = _svc.CurrentModeKey();   // VMs already seeded with this mode's presets; don't re-trigger

        _svc.ApplyStartupState();

        _tray = new TrayController(d, ApplyProfile, _windows.ToggleMain, _windows.OpenMain, _windows.ShowLighting, ExitApp);

        // Real-time keyboard-brightness sync: the Fn brightness key raises raw input, so instead of polling we
        // re-read on that input, while the Lighting panel is visible. The read is off-thread and self-
        // coalescing, so no debounce is needed and the slider tracks each press immediately.
        if (d.Hotkeys != null)
        {
            d.Hotkeys.Pressed += OnHotkey;
            d.Hotkeys.InputActivity += OnInputActivity;
        }

        Refresh();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        _windows.OpenMain();

        _ = CheckForUpdatesAsync();   // fire-and-forget GitHub-Releases check; surfaces a banner + tray item

        // Linux (AppImage): if the udev rules aren't installed yet, offer a one-click pkexec install.
        if (HardwareAccess.RulesNeeded())
            _vm.SetHardwareAccessNeeded(() => _ = GrantHardwareAccessAsync());
    }

    // ---- update check + apply ----

    private async Task CheckForUpdatesAsync()
    {
        var info = await _updates.CheckAsync();
        if (info == null) return;   // current / offline / no releases -> nothing shown

        // Running as an AppImage with an .AppImage asset -> self-replace in place; otherwise open the page.
        var asset = AppImageUpdater.IsAppImage ? AppImageUpdater.PickAsset(info.Assets) : null;
        Action act = asset != null
            ? () => _ = SelfUpdateAsync(asset.Url)
            : () => OpenUrl(info.Url);

        Dispatcher.UIThread.Post(() =>
        {
            _vm.SetUpdate(info.Version, act);
            _tray.SetUpdate($"Update available: v{info.Version}", act);
        });
    }

    private async Task SelfUpdateAsync(string assetUrl)
    {
        Notify("Downloading update…");
        var (ok, err) = await AppImageUpdater.ReplaceAsync(assetUrl);
        if (ok) { AppImageUpdater.Restart(); ExitApp(); }   // relaunch the updated AppImage, then quit
        else Notify("Update failed" + Err(err));
    }

    private async Task GrantHardwareAccessAsync()
    {
        var (ok, err) = await Task.Run(() => { var r = HardwareAccess.Install(out var e); return (r, e); });
        Dispatcher.UIThread.Post(() =>
        {
            if (ok) { _vm.NeedsHardwareAccess = false; Notify("Hardware access granted — restart to use the unlocked controls."); }
            else Notify("Grant access failed" + Err(err));
        });
    }

    private static void OpenUrl(string url)
    {
        try { using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { } }
        catch { /* no handler / blocked -> ignore */ }
    }

    // Any special-key input -> re-read keyboard brightness immediately (no debounce), but only while the
    // Lighting panel is visible so normal typing does ZERO work here. The read is off-thread and coalesces
    // back-to-back requests, so rapid presses track without piling up or blocking input.
    private void OnInputActivity()
    {
        if (_vm.IsLightingVisible) _vm.SyncLightingIfVisible();
    }

    // ---- actions ----

    private void ApplyProfile(PerformanceProfile p)
    {
        if (!_svc.ApplyProfile(p)) Notify("Failed to set " + p.DisplayName + Err(_svc.LastError));
        Refresh();
    }

    // Turbo used as a switch (the "Turbo toggles" mode).
    private void SetTurbo(bool on)
    {
        if (!_svc.SetTurbo(on)) Notify("Turbo failed" + Err(_svc.LastError));
        Refresh();
    }

    // Flipping "Turbo key toggles Turbo" reshapes the Performance section (Turbo becomes a switch), so
    // persist it and refresh immediately rather than waiting for the poll.
    private void SetTurboToggles(bool on)
    {
        _svc.SetTurboToggles(on);
        Refresh();
    }

    private void ApplyFan(FanMode mode, byte cpu, byte gpu)
    {
        if (!_svc.ApplyFan(mode, cpu, gpu)) Notify("Fans failed" + Err(_svc.LastError));
        Refresh();
    }

    private Task<bool> ConfirmCalibrationAsync() => _windows.ConfirmCalibrationAsync();

    // ---- hotkeys ----

    private void OnHotkey(HotkeyAction action) => Dispatcher.UIThread.Post(() =>
    {
        switch (action)
        {
            case HotkeyAction.TogglePerformance: OnTogglePerformance(); break;
            case HotkeyAction.ToggleWindow:      OnToggleWindow();      break;
        }
    });

    private void OnTogglePerformance()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTurbo).TotalMilliseconds < 800) return;
        _lastTurbo = now;

        var applied = _svc.TogglePerformance();
        if (applied != null) Notify("Profile: " + applied.DisplayName);
        Refresh();
    }

    private void OnToggleWindow()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastNitro).TotalMilliseconds < 600) return;
        _lastNitro = now;
        if (_windows.IsMainOpen) _windows.HideAll();
        else _windows.OpenMain();
    }
    
    // ---- refresh ----

    private void Refresh()
    {
        _svc.EvaluateClamshell();

        var battery = _svc.ReadBatteryInfo();
        _svc.SyncPowerSource(battery);   // re-apply the per-source remembered mode on AC<->battery change

        var current = _svc.CurrentProfile();
        var selectable = _svc.SelectableProfiles();
        var sensors = _svc.ReadSensors();
        var status = _svc.Device.StatusMessage ?? string.Empty;

        // Performance mode changed (user pick / hotkey / power-source restore)? Apply that mode's saved fan +
        // lighting presets to the hardware and reflect them in the UI.
        var modeKey = _svc.CurrentModeKey();
        if (modeKey != _lastModeKey)
        {
            _lastModeKey = modeKey;
            if (_svc.ApplyModeFan() is { } fan) _vm.ReloadFans(fan.Mode, fan.Cpu, fan.Gpu);
            _vm.ReloadLighting(_svc.LightsForCurrentMode());
        }

        _vm.Refresh(current, selectable, _svc.Settings.TurboToggles, _svc.BaseProfile(), sensors, battery, status);
        _vm.SyncLightingIfVisible();   // keep the keyboard-brightness slider live while the Lighting panel is open
        _tray.Update(current, selectable);
    }

    private void ExitApp()
    {
        _timer.Stop();
        _tray.Dispose();
        _svc.Dispose();
        _desktop.Shutdown();
    }

    // ---- helpers ----

    private void Notify(string text) => _vm.Status = text;

    private static string Err(string? e) => e != null ? $": {e}" : string.Empty;
}
