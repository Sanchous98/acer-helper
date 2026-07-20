using System.Diagnostics;
using AcerHelper.Features;
using AcerHelper.Localization;
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
    // The string-baked UI (view-models, flyout window, tray). Not readonly: a live language switch rebuilds all
    // three from scratch in the new language (see RebuildForLanguage) — everything below this stays put.
    private MainViewModel _vm;
    private FlyoutCoordinator _windows;
    private TrayController _tray;
    private readonly DispatcherTimer _timer;
    // Owns the lighting re-apply / lid-blank / sleep-resume state machine (its timer + watchers). Created before
    // the UI so the follows-profile toggle can reach it, then re-pointed at each fresh UI via Attach.
    private readonly LightingCoordinator _lightingCoord;
    private LightingViewModel? _lighting;              // current lighting VM (rebuilt per language); handed to _lightingCoord
    private readonly UpdateChecker _updates = new();

    private DateTime _lastTurbo = DateTime.MinValue;
    private DateTime _lastNitro = DateTime.MinValue;
    // Touched by the background refresh pass (below) and seeded on the UI thread (ctor / language rebuild);
    // volatile for clean publication across the Task.Run barrier. The pass is single-flight (_busy), so no two
    // passes race on them.
    private volatile string? _lastModeKey;    // preset key we last loaded (Turbo shares its base mode's key)
    private volatile string? _lastProfileId;  // actual hardware profile last seen (distinct for base vs Turbo)
    private int _busy;                         // 0/1 single-flight guard for the refresh background pass (Interlocked)
    private int _rerun;                        // set when a Refresh() arrives mid-pass -> run exactly once more (coalesced)
    private (string version, Action act)? _pendingUpdate;   // found update, remembered so a UI rebuild can re-show it
    private bool _updating;   // a self-update download/install is in flight — both the banner and the tray item
                              // stay clickable, so without this a second click starts a second download and a
                              // second msiexec (which then trips over the Windows Installer mutex mid-upgrade)

    public AppController(IClassicDesktopStyleApplicationLifetime desktop, LaptopService svc, bool startMinimized = false)
    {
        _desktop = desktop;
        _svc = svc;

        var d = _svc.Device;

        // Re-apply persisted device state (clamshell keep-awake, blue-light tint) NOW, before the option
        // view-models below read it — otherwise the "Stay awake when lid closed" toggle captures the pre-startup
        // default (off) and shows off after every restart even though the setting is persisted (Settings.Clamshell).
        _svc.ApplyStartupState();

        // The lighting re-apply / lid-blank / resume machine lives in its own coordinator. Created up front —
        // before the UI — so the follows-profile toggle lambda built in BuildUi can reach it, and so it persists
        // untouched across a live language rebuild (only its view-model targets are re-pointed, via Attach).
        _lightingCoord = new LightingCoordinator(_svc);

        // Build the string-baked UI (view-models, flyout window, tray). It reads all its text via Loc at
        // construction, so a live language switch simply tears this down and rebuilds it in the new language
        // (RebuildForLanguage). Everything set up AFTER this point holds no localized text and survives the swap.
        (_vm, _windows, _tray, _lighting) = BuildUi();
        _lightingCoord.Attach(_vm, _lighting);
        var cur0 = _svc.CurrentProfile();
        _lastModeKey = _svc.CurrentModeKey(cur0);   // VMs already seeded with this mode's presets; don't re-trigger
        _lastProfileId = cur0?.Id ?? "";

        // On startup nothing else drives a profile-following lightbar (no switch yet), so paint the current
        // profile's palette once (and settle the keyboard's own colour on top) so it matches from launch. The
        // flash colour + mode lights are read here and handed to the coordinator, which caches them.
        _lightingCoord.ApplyFollowLighting(cur0?.FlashColor, _svc.LightsForCurrentMode());

        // Linux (AppImage): if the udev rules aren't installed yet, offer a one-click pkexec install.
        ApplyHardwareAccessBanner();

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

        // Heal a stale run-at-logon entry from an older build (wrong launch command) so an in-place upgrade
        // migrates to the current definition. Best-effort, off the UI thread; only touches the entry if it
        // points at this exe (see Autostart.EnsureCurrent). The task itself is the watchdog (it relaunches us if
        // killed), so there's no separate watcher process to keep alive.
        _ = Task.Run(() => { try { d.Autostart?.EnsureCurrent(); } catch { /* best-effort */ } });

        // Autostart (--startup) runs us resident in the tray — don't pop the flyout on every logon. The window is
        // still created (shown lazily) and opens on demand via the tray icon or the Nitro key.
        if (!startMinimized) _windows.OpenMain();

        _ = CheckForUpdatesAsync();   // fire-and-forget GitHub-Releases check; surfaces a banner + tray item
    }

    // Assemble the localized UI: the lighting view-model, the dashboard view-model (with its UiActions), the
    // flyout window and the tray. Returns them for the caller to store — kept side-effect-free (no field writes
    // beyond what the closures capture) so it can run both at startup and on a live language rebuild.
    private (MainViewModel, FlyoutCoordinator, TrayController, LightingViewModel?) BuildUi()
    {
        var d = _svc.Device;

        // The Lighting window hosts RGB zones AND/OR a plain (non-RGB) keyboard-backlight brightness control,
        // so it opens whenever the device has either.
        // The "follows performance profile" flag is vendor-scoped: its key is owned by the backend (the Acer
        // lightbar) and surfaced through the neutral IRgbDevice port, so the app persists it via the generic
        // DeviceSettings bag without any vendor coupling here. Null when the device has no such zone.
        var followKey = d.Lighting?.ProfileFollowKey;
        var lighting = d.Lighting != null || d.KeyboardBrightness != null
            ? new LightingViewModel(d.Lighting, _svc.LightsForCurrentMode(), _svc.EnsureLightZone, _svc.PersistLighting,
                                    followKey != null && _svc.GetDeviceFlag(followKey, true),
                                    // On flip: persist the flag (AppController owns the vendor key), then have the
                                    // coordinator kick the re-apply so the lightbar repaints now (ON -> this
                                    // profile's palette; OFF -> its custom colour) instead of waiting for the next
                                    // switch. Safe: _lightingCoord is created before BuildUi and outlives rebuilds.
                                    v => { if (followKey != null) _svc.SetDeviceFlag(followKey, v);
                                           _lightingCoord.OnFollowsProfileFlipped(); },
                                    d.KeyboardBrightness, _svc.SetKeyboardBrightness)
            : null;
        var opts = new OptionsAssembler(_svc, Notify, ConfirmCalibrationAsync);
        var fan0 = _svc.CurrentFan();   // current mode's fan preset (defaults if none saved)
        var vm = new MainViewModel(d, new UiActions(
            new ProfileActions(ApplyProfile, _svc.Settings.TurboToggles, SetTurbo),
            new FanSection(fan0, SetFan, SetFanCurve, ShowFanCurve),
            new GpuSection(_svc.CurrentGpuOc(), SetGpuOc),
            new CpuSection(d.CpuPower?.Modes ?? [], _svc.CurrentCpuPower(), SetCpuPower),
            new BatterySection(d.BatteryInfo != null, opts.BatteryLimit(), opts.BatteryCalibration(), opts.BatteryChargeMode()),
            new OptionsSection(opts.Toggles(), opts.Choices(),
                _svc.Settings.TurboToggles, SetTurboToggles,
                b => _svc.SetClamshell(b), b => _svc.SetAutostart(b),
                _svc.Settings.Language, SetLanguage)),
            lighting);

        var windows = new FlyoutCoordinator(vm);
        var tray = new TrayController(d, ApplyProfile, windows.ToggleMain, windows.OpenMain, windows.ShowLighting, ExitApp);
        return (vm, windows, tray, lighting);
    }

    // ---- live language switch ----

    // The user picked a language in Options. Persist it and rebuild the UI in that language. Deferred to the
    // next UI-thread turn: we're inside the language dropdown's own change handler, on the very window we're
    // about to tear down, so let this event unwind first.
    private void SetLanguage(AppLanguage language)
    {
        if (language == _svc.Settings.Language) return;
        _svc.SetLanguage(language);
        Dispatcher.UIThread.Post(RebuildForLanguage);
    }

    // Swap the whole string-baked UI for a fresh copy in the newly-selected language. The service, timers, lid/
    // resume watchers and the hotkey subscription persist untouched — they carry no localized text and keep
    // running across the swap (they read _vm/_windows/_tray/_lighting via fields, so they pick up the new ones).
    private void RebuildForLanguage()
    {
        var wasOpen = _windows.IsMainOpen;

        _tray.Dispose();       // remove the old tray icon
        _windows.Dispose();    // close + unhook the old flyout window for good

        Loc.Use(_svc.Settings.Language);
        (_vm, _windows, _tray, _lighting) = BuildUi();
        _lightingCoord.Attach(_vm, _lighting);    // re-point the persistent coordinator at the fresh view-models
        var cur = _svc.CurrentProfile();
        _lastModeKey = _svc.CurrentModeKey(cur);  // freshly seeded VMs; don't let Refresh re-trigger a mode reload
        _lastProfileId = cur?.Id ?? "";
        _lightingCoord.ApplyFollowLighting(cur?.FlashColor, _svc.LightsForCurrentMode());
        ApplyUpdateBanner();                       // re-show the update banner if the startup check already found one
        ApplyHardwareAccessBanner();
        Refresh();                                 // push live state into the fresh view-models + tray

        // Reopen where the language switch lives so the change is immediately visible (only if it was open — the
        // switch is only reachable from the Options drawer, so in practice it always was).
        if (wasOpen) { _windows.OpenMain(); _vm.OpenOptionsCommand.Execute(null); }
    }

    // Re-show the "update available" banner + tray item from the remembered check result (used after a UI
    // rebuild, and by the initial check when it completes). No-op until an update has been found.
    private void ApplyUpdateBanner()
    {
        if (_pendingUpdate is not { } u) return;
        _vm.SetUpdate(u.version, u.act);
        _tray.SetUpdate(Loc.T("Update available: v{0}", u.version), u.act);
    }

    private void ApplyHardwareAccessBanner()
    {
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
            _pendingUpdate = (info.Version, act);   // remembered so a language rebuild can re-show it
            ApplyUpdateBanner();
        });
    }

    private async Task SelfUpdateAsync(string assetUrl)
    {
        if (_updating) return;   // one update at a time (see the field); failure resets so retry works
        _updating = true;
        try
        {
            Notify(Loc.T("Downloading update…"));
            var (ok, err) = await AppImageUpdater.ReplaceAsync(assetUrl);
            if (ok) { AppImageUpdater.Restart(); ExitApp(); }   // relaunch the updated AppImage, then quit
            else Notify(Loc.T("Update failed") + Err(err));
        }
        finally { _updating = false; }
    }

    // Windows: download the MSI, then hand off to the detached msiexec helper and quit so the exe unlocks —
    // the helper upgrades in place and relaunches us.
    private async Task SelfUpdateWindowsAsync(string assetUrl)
    {
        if (_updating) return;   // one update at a time (see the field); failure resets so retry works
        _updating = true;
        try
        {
            Notify(Loc.T("Downloading update…"));
            var (ok, res) = await WindowsUpdater.DownloadAsync(assetUrl);
            if (!ok) { Notify(Loc.T("Update failed") + Err(res)); return; }

            Notify(Loc.T("Installing update…"));
            if (WindowsUpdater.InstallAndExit(res!)) ExitApp();
            else Notify(Loc.T("Update failed"));
        }
        finally { _updating = false; }
    }

    private async Task GrantHardwareAccessAsync()
    {
        var (ok, err) = await Task.Run(() => { var r = HardwareAccess.Install(out var e); return (r, e); });
        Dispatcher.UIThread.Post(() =>
        {
            if (ok) { _vm.NeedsHardwareAccess = false; Notify(Loc.T("Hardware access granted — restart to use the unlocked controls.")); }
            else Notify(Loc.T("Grant access failed") + Err(err));
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
    // Marshaled to the UI thread like OnHotkey: on Linux the event fires on the evdev reader thread, and
    // IsLightingVisible/Sync touch UI-bound state (incl. enumerating the Panels collection the UI mutates).
    private void OnInputActivity() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm.IsLightingVisible) _vm.SyncLightingIfVisible();
    });

    // ---- actions ----

    private void ApplyProfile(PerformanceProfile p)
    {
        if (!_svc.ApplyProfile(p)) Notify(Loc.T("Failed to set {0}", Loc.T(p.DisplayName)) + Err(_svc.LastError));
        Refresh();
    }

    // Turbo used as a switch (the "Turbo toggles" mode).
    private void SetTurbo(bool on)
    {
        if (!_svc.SetTurbo(on)) Notify(Loc.T("Turbo failed") + Err(_svc.LastError));
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

    // GPU core/memory clock offsets, applied + persisted per performance mode by the service. Like SetFan:
    // no Refresh() — nothing in the shared UI/tray depends on it and it fires on every debounced slider drag.
    // Failure is surfaced so a rejected write (e.g. dGPU powered off) doesn't fail silently.
    private void SetGpuOc(int core, int mem)
    {
        if (!_svc.SetGpuOc(core, mem)) Notify(Loc.T("GPU overclock failed") + Err(_svc.LastError));
    }

    // CPU power-mode overlay, applied + persisted per performance mode by the service. Like SetGpuOc: no
    // Refresh() (nothing shared depends on it); failure surfaced.
    private void SetCpuPower(string id)
    {
        if (!_svc.SetCpuPower(id)) Notify(Loc.T("Power mode failed") + Err(_svc.LastError));
    }

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
        if (applied != null) Notify(Loc.T("Profile: {0}", Loc.T(applied.DisplayName)));
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

    // One poll's worth of state, read on the background pass and consumed on the UI pass. Immutable snapshot so
    // the UI thread never re-touches the hardware (that's what used to freeze the app when the ACPI-EC stalled
    // during a display connect). Lights is the live per-mode dict (only the UI thread touches it after hand-off).
    private readonly record struct Tick(
        PerformanceProfile? Current, IReadOnlyList<PerformanceProfile> Selectable, PerformanceProfile? Base,
        SensorSnapshot Sensors, BatteryInfoSnapshot Battery, string? Status, bool TurboToggles,
        bool ModeChanged, FanPreset? Fan, GpuOcPreset? Gpu, string? CpuId,
        bool ProfileChanged, AccentColor? Flash,
        Dictionary<string, LightSettings>? Lights);

    // Kick a refresh. All hardware I/O runs on a pool thread (BackgroundPass) so a stalled EC/WMI read can never
    // freeze the UI; the VM/tray updates are posted back to the UI thread (UiPass). Single-flight: if a pass is
    // still running (e.g. blocked on a contended EC), DON'T drop this request — mark _rerun so exactly one more
    // pass runs when the current one finishes. That matters for user actions (profile/Turbo/hotkey) which call
    // Refresh() right after their synchronous hardware Set: the follow-up pass reflects the new state (chip/tray,
    // lightbar palette, the new mode's fan/GPU/CPU presets) on the very next pass instead of waiting for the 3s tick.
    private void Refresh()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _rerun, 1);
            // The in-flight pass may have finished (and read _rerun==0) between our failed CAS above and this
            // write, leaving _rerun==1 with nothing scheduled. Re-check: if the guard is now free, adopt the run
            // ourselves so a user action isn't deferred to the next 3s tick. Still busy -> its finally sees _rerun.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        }
        try { _ = Task.Run(BackgroundPass); }
        catch { Interlocked.Exchange(ref _busy, 0); }   // scheduler refused (rare) -> release, don't wedge the guard
    }

    // Pool thread: every blocking hardware read/write lives here. LaptopService guards its shared Settings state
    // with its own lock, and the WMI layer serializes EC access process-wide, so this never corrupts a
    // concurrent user-initiated write — they just serialize.
    private void BackgroundPass()
    {
        try
        {
            _svc.EvaluateClamshell();                 // QueryDisplayConfig can hitch during a topology change

            var battery = _svc.ReadBatteryInfo();
            _svc.SyncPowerSource(battery);            // re-apply the per-source remembered mode on AC<->battery change

            var current = _svc.CurrentProfile();      // the ONE hardware profile read this pass
            var modeKey = _svc.CurrentModeKey(current);
            var profileId = current?.Id ?? "";
            var selectable = _svc.SelectableProfiles();
            var sensors = _svc.ReadSensors();
            // null (not "") when the device has no diagnostic message, so MainViewModel.Refresh's `if (status != null)`
            // guard leaves the status line alone and a transient Notify() survives (a device StatusMessage, when
            // present, is a latched startup diagnostic — localized and shown; it never reverts to null).
            var status = _svc.Device.StatusMessage is { } m ? Loc.T(m) : null;
            var turbo = _svc.Settings.TurboToggles;

            bool modeChanged = modeKey != _lastModeKey;
            bool profileChanged = profileId != _lastProfileId;
            // The current mode's per-zone lights, read once and shared by both the mode- and profile-change paths
            // (same dict). Only read when something changed — the coordinator caches it for its re-paints.
            var lights = (modeChanged || profileChanged) ? _svc.LightsForCurrentMode() : null;

            FanPreset? fan = null; GpuOcPreset? gpu = null; string? cpu = null; AccentColor? flash = null;
            // Performance mode changed (user pick / hotkey / power-source restore)? Apply that mode's saved fan +
            // GPU/CPU presets to the hardware here (background); reflect them in the UI on the UI pass.
            if (modeChanged)
            {
                _lastModeKey = modeKey;
                fan = _svc.ApplyModeFan();
                gpu = _svc.ApplyModeGpuOc();       // re-apply this mode's GPU clock offsets
                cpu = _svc.ApplyModeCpuPower();    // re-apply this mode's CPU power mode
            }
            // On a HARDWARE profile change (incl. base<->Turbo, which shares its base's preset KEY so the block
            // above won't fire) repaint the lighting on the UI pass (immediately, then a couple of retries).
            if (profileChanged)
            {
                _lastProfileId = profileId;
                flash = current?.FlashColor;
            }

            _svc.ApplyCustom(sensors);   // Custom mode: drive each fan from its curve (or fixed speed) using live temps
            var baseP = _svc.BaseProfile(current);

            var t = new Tick(current, selectable, baseP, sensors, battery, status, turbo,
                             modeChanged, fan, gpu, cpu, profileChanged, flash, lights);
            Dispatcher.UIThread.Post(() => UiPass(t));
        }
        catch { /* transient hardware error — next tick retries */ }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);   // release even on throw, or every future tick wedges
            // A tick/user action arrived while we were busy -> run exactly one more pass to reflect it now.
            if (Interlocked.Exchange(ref _rerun, 0) == 1) Refresh();
        }
    }

    // UI thread: reflect the snapshot into the view-models / tray / lighting coordinator. Order matches the old
    // inline Refresh exactly. No hardware reads here — everything is pre-read in the Tick.
    private void UiPass(Tick t)
    {
        if (t.ModeChanged)
        {
            if (t.Fan is { } fan) _vm.ReloadFans(fan);
            if (t.Gpu is { } gpu) _vm.ReloadGpuOc(gpu);
            _vm.ReloadCpuPower(t.CpuId);
            _lightingCoord.OnModeChanged(t.Lights!);   // Lights is non-null whenever Mode/Profile changed
        }
        if (t.ProfileChanged)
            _lightingCoord.OnProfileChanged(t.Flash, t.Lights!);

        _vm.Refresh(t.Current, t.Selectable, t.TurboToggles, t.Base, t.Sensors, t.Battery, t.Status);
        _vm.SyncLightingIfVisible();   // keep the keyboard-brightness slider live (its read is already off-thread)
        _tray.Update(t.Current, t.Selectable);
    }

    private void ExitApp()
    {
        _timer.Stop();
        _lightingCoord.Dispose();
        _tray.Dispose();
        _svc.Dispose();
        _desktop.Shutdown();
    }

    // ---- helpers ----

    private void Notify(string text) => _vm.Status = text;

    private static string Err(string? e) => e != null ? $": {e}" : string.Empty;
}
