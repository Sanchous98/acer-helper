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
    private readonly DispatcherTimer _lightReapply;   // re-applies lighting for a while after a profile switch
    private int _lightReapplyLeft;                     // remaining re-apply ticks
    private readonly LightingViewModel? _lighting;     // kept so the re-apply can drive the profile-follow lightbar
    private readonly ResumeWatcher _resume;            // re-applies lighting on wake (firmware drops it over sleep)
    private readonly LidWatcher _lid;                  // blanks/restores the RGB as the lid shuts/opens in clamshell mode
    private readonly UpdateChecker _updates = new();

    private DateTime _lastTurbo = DateTime.MinValue;
    private DateTime _lastNitro = DateTime.MinValue;
    private string? _lastModeKey;    // preset key we last loaded (Turbo shares its base mode's key)
    private string? _lastProfileId;  // actual hardware profile last seen (distinct for base vs Turbo)
    private bool _lidShut;           // last lid state from the LidWatcher (Windows); true = shut. Drives blanking.

    public AppController(IClassicDesktopStyleApplicationLifetime desktop, LaptopService svc, bool startMinimized = false)
    {
        _desktop = desktop;
        _svc = svc;

        var d = _svc.Device;

        // Re-apply persisted device state (clamshell keep-awake, blue-light tint) NOW, before the option
        // view-models below read it — otherwise the "Stay awake when lid closed" toggle captures the pre-startup
        // default (off) and shows off after every restart even though the setting is persisted (Settings.Clamshell).
        _svc.ApplyStartupState();

        // Acer firmware repaints the lit zones with the profile's palette colour a moment AFTER our WMI profile
        // set. A single re-apply can land too early (before that repaint), so we re-apply the mode's lighting a
        // couple of times right after the switch — whichever tick lands after the repaint overrides it and it
        // then stays. (Only user-driven zones are re-applied; a "follows profile" lightbar has no panel and is
        // left as the firmware's palette. The palette flash itself is firmware and can't be suppressed.)
        // Created up front so the follows-profile toggle lambda below can safely start it.
        _lightReapply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _lightReapply.Tick += (_, _) =>
        {
            ApplyFollowLighting();
            if (--_lightReapplyLeft <= 0) _lightReapply.Stop();
        };

        // The Lighting window hosts RGB zones AND/OR a plain (non-RGB) keyboard-backlight brightness control,
        // so it opens whenever the device has either.
        // The "follows performance profile" flag is vendor-scoped: its key is owned by the backend (the Acer
        // lightbar) and surfaced through the neutral IRgbDevice port, so the app persists it via the generic
        // DeviceSettings bag without any vendor coupling here. Null when the device has no such zone.
        var followKey = d.Lighting?.ProfileFollowKey;
        _lighting = d.Lighting != null || d.KeyboardBrightness != null
            ? new LightingViewModel(d.Lighting, _svc.LightsForCurrentMode(), _svc.PersistLighting,
                                    followKey != null && _svc.GetDeviceFlag(followKey, true),
                                    // On flip: persist the flag, then kick the re-apply so the lightbar repaints
                                    // now (ON -> this profile's palette; OFF -> its custom colour) instead of
                                    // waiting for the next switch. Safe: _lightReapply is created just above.
                                    v => { if (followKey != null) _svc.SetDeviceFlag(followKey, v);
                                           _lightReapplyLeft = 2; _lightReapply.Stop(); _lightReapply.Start(); },
                                    d.KeyboardBrightness, _svc.SetKeyboardBrightness)
            : null;
        var opts = new OptionsAssembler(_svc, Notify, ConfirmCalibrationAsync);
        var fan0 = _svc.CurrentFan();   // current mode's fan preset (defaults if none saved)
        _vm = new MainViewModel(d, new UiActions(
            ApplyProfile, SetTurbo, SetFan, SetFanCurve, ShowFanCurve,
            opts.Toggles(), opts.Choices(),
            () => d.Clamshell?.Enabled ?? false, b => _svc.SetClamshell(b),
            _svc.Settings.TurboToggles, SetTurboToggles,
            () => d.Autostart?.IsEnabled() ?? false, b => _svc.SetAutostart(b),
            fan0.Mode, fan0.Cpu, fan0.Gpu,
            fan0.CpuUseCurve, fan0.GpuUseCurve, fan0.CpuCurve, fan0.GpuCurve,
            d.BatteryInfo != null, opts.BatteryLimit(), opts.BatteryCalibration(), opts.BatteryChargeMode()),
            _lighting);

        _windows = new FlyoutCoordinator(_vm);
        _lastModeKey = _svc.CurrentModeKey();   // VMs already seeded with this mode's presets; don't re-trigger
        _lastProfileId = _svc.CurrentProfile()?.Id ?? "";

        // On startup nothing else drives a profile-following lightbar (no switch yet), so paint the current
        // profile's palette once (and settle the keyboard's own colour on top) so it matches from launch.
        ApplyFollowLighting();

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

        // Sleep/hibernate clears the EC's RGB state; re-apply the current mode's lighting on wake.
        _resume = new ResumeWatcher(() => Dispatcher.UIThread.Post(ReapplyLighting));
        _resume.Start();

        // In clamshell (keep-awake) mode the machine stays on with the lid shut, but the backlight is then hidden —
        // so blank it while the lid is closed and restore it on open. Gated on clamshell being enabled (see
        // OnLidChanged): without it a lid-close just sleeps the machine and the backlight is moot (restored by the
        // resume re-apply above). The watcher fires on its message thread, so marshal to the UI thread here.
        _lid = new LidWatcher(open => Dispatcher.UIThread.Post(() => OnLidChanged(open)));
        _lid.Start();

        // Heal a stale run-at-logon entry from an older build (wrong launch command) so an in-place upgrade
        // migrates to the current definition. Best-effort, off the UI thread; only touches the entry if it
        // points at this exe (see Autostart.EnsureCurrent). The task itself is the watchdog (it relaunches us if
        // killed), so there's no separate watcher process to keep alive.
        _ = Task.Run(() => { try { d.Autostart?.EnsureCurrent(); } catch { /* best-effort */ } });

        // Autostart (--startup) runs us resident in the tray — don't pop the flyout on every logon. The window is
        // still created (shown lazily) and opens on demand via the tray icon or the Nitro key.
        if (!startMinimized) _windows.OpenMain();

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

        // Real self-update where we can: the Windows MSI install upgrades in place via msiexec; a Linux
        // AppImage self-replaces. Anything else (portable/dev run, RPM, missing asset) opens the release page.
        var msi = WindowsUpdater.IsSupported ? WindowsUpdater.PickAsset(info.Assets) : null;
        var appImage = AppImageUpdater.IsAppImage ? AppImageUpdater.PickAsset(info.Assets) : null;
        Action act =
            msi != null      ? () => _ = SelfUpdateWindowsAsync(msi.Url) :
            appImage != null ? () => _ = SelfUpdateAsync(appImage.Url) :
                               () => OpenUrl(info.Url);

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

    // Windows: download the MSI, then hand off to the detached msiexec helper and quit so the exe unlocks —
    // the helper upgrades in place and relaunches us.
    private async Task SelfUpdateWindowsAsync(string assetUrl)
    {
        Notify("Downloading update…");
        var (ok, res) = await WindowsUpdater.DownloadAsync(assetUrl);
        if (!ok) { Notify("Update failed" + Err(res)); return; }

        Notify("Installing update…");
        if (WindowsUpdater.InstallAndExit(res!)) ExitApp();
        else Notify("Update failed");
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

    // Fan mode + fixed speeds (and, in Custom, per-fan curves) are applied and persisted by the service. No
    // Refresh() here: nothing in the shared UI/tray depends on fan state, and this fires on every debounced
    // slider/curve drag — a full refresh each time would spam WMI reads.
    private void SetFan(FanMode mode, byte cpu, byte gpu) => _svc.SetFan(mode, cpu, gpu);
    private void SetFanCurve(bool gpu, bool use, int[] points) => _svc.SetFanCurve(gpu, use, points);

    private Task ShowFanCurve(FanCurveDialogViewModel vm) => _windows.EditFanCurveAsync(vm);

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
            if (_svc.ApplyModeFan() is { } fan)
                _vm.ReloadFans(fan.Mode, fan.Cpu, fan.Gpu, fan.CpuUseCurve, fan.GpuUseCurve, fan.CpuCurve, fan.GpuCurve);
            if (BacklightHidden) _svc.Device.Lighting?.Blank();   // don't light a hidden keyboard on a mode change under a shut lid
            else _vm.ReloadLighting(_svc.LightsForCurrentMode());
        }

        // On a HARDWARE profile change (incl. base<->Turbo, which shares its base's preset KEY so the block above
        // won't fire) repaint the lighting. Do it IMMEDIATELY — a following lightbar must show the NEW profile's
        // palette at once, otherwise the previous colour lingers for a beat and looks like a stray flash of the
        // old colour (NitroSense flips it the instant the profile changes). Then repeat a couple of times to
        // catch any late firmware repaint.
        var profileId = current?.Id ?? "";
        if (profileId != _lastProfileId)
        {
            _lastProfileId = profileId;
            ApplyFollowLighting();
            _lightReapplyLeft = 2;
            _lightReapply.Stop(); _lightReapply.Start();
        }

        _svc.ApplyCustom(sensors);   // Custom mode: drive each fan from its curve (or fixed speed) using live temps

        _vm.Refresh(current, selectable, _svc.Settings.TurboToggles, _svc.BaseProfile(), sensors, battery, status);
        _vm.SyncLightingIfVisible();   // keep the keyboard-brightness slider live while the Lighting panel is open
        _tray.Update(current, selectable);
    }

    // Repaint after a profile change/toggle. First paint the new profile's palette on a follow-lightbar (a GLOBAL
    // write that also flashes the keyboard), then re-apply the per-zone colours so the keyboard settles back to
    // its own custom colour on top. Called immediately on a switch (so the lightbar flips at once, no lingering
    // old colour) and repeated by _lightReapply as a safety net against a late firmware repaint.
    private void ApplyFollowLighting()
    {
        if (BacklightHidden) { _svc.Device.Lighting?.Blank(); return; }   // lid shut in clamshell mode -> keep it dark
        if (_lighting is { ShowFollowsProfile: true, FollowsProfile: true } && _svc.CurrentProfile()?.FlashColor is { } flash)
            _svc.Device.Lighting?.SetProfileFlash(flash);
        _vm.ReloadLighting(_svc.LightsForCurrentMode());
    }

    // Re-drive the current mode's lighting via the same path as a profile switch (palette flash for a follow-
    // lightbar + per-zone re-apply). Used on wake from sleep/hibernation (the firmware drops the EC's RGB) and on
    // lid-open in clamshell mode (we blanked it on lid-close). Uses more retries than a switch because the HID/EC
    // can take a second or two to be ready after a hibernation resume.
    private void ReapplyLighting()
    {
        ApplyFollowLighting();
        _lightReapplyLeft = 6;
        _lightReapply.Stop(); _lightReapply.Start();
    }

    // Lid opened/closed while clamshell keep-awake is enabled: shut -> blank the (now hidden) backlight without
    // touching the app's stored per-zone state; opened -> restore the current mode's lighting. Ignored when
    // clamshell is off — a lid-close then sleeps the machine, and the backlight is re-applied on resume instead.
    // Posted to the UI thread by the lid watcher, so all HID writes stay serialized with the rest of the app.
    private void OnLidChanged(bool open)
    {
        _lidShut = !open;   // remembered so a repaint (profile/mode/power change) under a shut lid re-blanks (see BacklightHidden)
        if (_svc.Device.Clamshell?.Enabled != true) return;
        if (open) ReapplyLighting();
        else _svc.Device.Lighting?.Blank();
    }

    // True while the backlight must stay dark: the lid is shut AND clamshell keep-awake is on, so the machine runs
    // with a hidden keyboard/lightbar. Every repaint path checks this, so a profile/mode/power-source change under
    // a closed lid re-blanks instead of lighting the (hidden) keyboard back up until the lid is next opened.
    private bool BacklightHidden => _lidShut && _svc.Device.Clamshell?.Enabled == true;

    private void ExitApp()
    {
        _timer.Stop();
        _lightReapply.Stop();
        _resume.Dispose();
        _lid.Dispose();
        _tray.Dispose();
        _svc.Dispose();
        _desktop.Shutdown();
    }

    // ---- helpers ----

    private void Notify(string text) => _vm.Status = text;

    private static string Err(string? e) => e != null ? $": {e}" : string.Empty;
}
