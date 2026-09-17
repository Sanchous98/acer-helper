using System.Diagnostics;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Infrastructure.Diagnostics;
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
    // The CPU-power row is built with a PLACEHOLDER, not a read (wave 6), so it needs a real value from the first
    // pass that follows each UI build — same shape as the two seeds above, and reset in the same place. Written by
    // the background pass, cleared by RebuildForLanguage (on the UI thread) when it puts a fresh placeholder back.
    private volatile bool _cpuPrimed;
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
        // Timed (see GateStats): the delta is the hardware time this block spent on the UI thread, which is the
        // "before" number wave 6 exists to move. It is a delta of the gate counters, so it measures blocked-on-EC
        // time, not wall time.
        var startupHw0 = GateStats.NonPoolTicks();
        _svc.ApplyStartupState();
        GateStatsLog.RecordStartup(GateStats.NonPoolTicks() - startupHw0);

        // The lighting re-apply / lid-blank / resume machine lives in its own coordinator. Created up front —
        // before the UI — so the follows-profile toggle lambda built in BuildUi can reach it, and so it persists
        // untouched across a live language rebuild (only its view-model targets are re-pointed, via Attach).
        _lightingCoord = new LightingCoordinator(_svc);

        // Read the current hardware profile ONCE, and before the UI is built, so everything that keys off the
        // current mode derives it from this one read instead of each doing its own EC round-trip: BuildUi's
        // lighting lookup and its per-mode fan/GPU/Curve-Optimizer values, the two seeds below and
        // the follow-lighting hand-off. Hoisting it above BuildUi is safe
        // because BuildUi only READS the device — the state changes live in ApplyStartupState above, which has
        // already run — and it keeps the read outside the gate-stats window below, whose subject is the time
        // BuildUi itself spends blocked on hardware.
        var cur0 = _svc.CurrentProfile();

        // Build the string-baked UI (view-models, flyout window, tray). It reads all its text via Loc at
        // construction, so a live language switch simply tears this down and rebuilds it in the new language
        // (RebuildForLanguage). Everything set up AFTER this point holds no localized text and survives the swap.
        var buildHw0 = GateStats.NonPoolTicks();
        (_vm, _windows, _tray, _lighting) = BuildUi(cur0);
        GateStatsLog.RecordBuild(GateStats.NonPoolTicks() - buildHw0);   // see the startup sample above
        _lightingCoord.Attach(_vm, _lighting);
        // Fill in the option rows' placeholder values, off the UI thread: these reads used to happen right here,
        // on the UI thread, while the UI was being built (wave 6). Deliberately AFTER ApplyStartupState above —
        // the clamshell row takes its state from the device object the takeover just touched, and must see the
        // applied value, which is why that row has no deferred read at all.
        _vm.OptionsPage?.Prime();
        // ...and the battery section's three charging rows, which are built the same way and for the same reason:
        // their values come from the charge-limit, calibration and charge-mode ports (wave 4a). Called from here —
        // and not from the view-model's constructor — for the ordering guarantee above, and so that a test can
        // assert that construction alone reads no port.
        _vm.Battery?.Prime();
        // ...and the lighting section with them: the plain-backlight slider is built with a placeholder too. The
        // RGB panels' brightness is NOT deferred (its construction value is what the startup re-apply sends to the
        // device), so nothing re-reads them here — a read that is not an event must not touch state, which is what
        // Prime exists to keep separate from AdoptFromInput (docs/state-and-events.md).
        _lighting?.Prime();
        _lastModeKey = _svc.CurrentModeKey(cur0);   // VMs already seeded with this mode's presets; don't re-trigger
        _lastProfileId = cur0?.Id ?? "";
        _cpuPrimed = false;                         // the fresh CPU-power row holds a placeholder — prime it

        // On startup nothing else drives a profile-following lightbar (no switch yet), so paint the current
        // profile's palette once (and settle the keyboard's own colour on top) so it matches from launch. The
        // flash colour + mode lights are read here and handed to the coordinator, which caches them.
        _lightingCoord.ApplyFollowLighting(cur0?.FlashColor, _svc.LightsForCurrentMode(cur0));

        // Linux (AppImage): if the udev rules aren't installed yet, offer a one-click pkexec install.
        ApplyHardwareAccessBanner();

        // Out-of-band keyboard-brightness input: the Fn brightness key raises raw input, so brightness the app did
        // not author is adopted from there instead of from the refresh pass — the pass read the same register
        // while our own write was still in flight, took the wire's lag as the user's choice, and saved it (see
        // docs/state-and-events.md). The read is off-thread and self-coalescing, so no debounce is needed.
        if (d.Hotkeys != null)
        {
            d.Hotkeys.Pressed += OnHotkey;
            d.Hotkeys.InputActivity += OnInputActivity;
        }

        Refresh();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        // The periodic gate-stats write rides the poll's own timer (it self-throttles to a line a minute), so
        // instrumentation costs no timer of its own. Periodic and not only on exit: a killed process never
        // reaches ExitApp, and an owner's real session is exactly the one worth measuring.
        _timer.Tick += (_, _) => { Refresh(); GateStatsLog.MaybeWrite(); };
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
        _ = OfferDriverAsync(d, startMinimized);   // one-time consent prompt for the driver a feature needs
    }

    // ---- third-party driver offer ----

    // A feature (CPU undervolt) needs a third-party kernel driver we do not ship installed. Ask ONCE, remember the
    // answer, and never nag: the port only exists on a machine where the driver is relevant and the build carries
    // its installer, so this is silent everywhere else. Declining is sticky — the user can still install it by hand,
    // and the feature appears by itself on the next launch either way, because the port probes for it.
    private async Task OfferDriverAsync(IDevice d, bool startMinimized)
    {
        if (d.DriverSetup is not { Installed: false } setup) return;
        // A logon start runs us into the tray with no window on screen — a modal dialog there would be an ambush, so
        // say nothing and leave the flag unset: the offer comes the next time the user actually opens the app.
        if (startMinimized) return;

        const string askedFlag = "driver.setupAsked.";
        if (_svc.GetDeviceFlag(askedFlag + setup.Name, false)) return;

        await Task.Delay(1500);   // let the flyout finish opening before stealing focus
        var yes = await _windows.ConfirmDriverAsync(setup.Name, setup.Purpose, setup.SourceUrl);
        _svc.SetDeviceFlag(askedFlag + setup.Name, true);
        if (!yes) return;

        // The installer blocks for seconds and self-elevates; keep it off the dispatcher and report either way.
        var error = await Task.Run(setup.Install);
        Notify(error ?? Loc.T("{0} installed — restart Acer Helper to use {1}", setup.Name, setup.Purpose));
    }

    // Assemble the localized UI: the lighting view-model, the dashboard view-model (with its UiActions), the
    // flyout window and the tray. Returns them for the caller to store — kept side-effect-free (no field writes
    // beyond what the closures capture) so it can run both at startup and on a live language rebuild.
    // <paramref name="cur"/> is the current hardware profile, already read by the caller: the lighting view-model
    // and the per-mode fan/GPU/Curve-Optimizer values all key off it, and passing it in means none of those
    // lookups costs a second EC round-trip.
    private (MainViewModel, FlyoutCoordinator, TrayController, LightingViewModel?) BuildUi(PerformanceProfile? cur)
    {
        var d = _svc.Device;

        // The Lighting window hosts RGB zones AND/OR a plain (non-RGB) keyboard-backlight brightness control,
        // so it opens whenever the device has either.
        // The "follows performance profile" flag is vendor-scoped: its key is owned by the backend (the Acer
        // lightbar) and surfaced through the neutral IRgbDevice port, so the app persists it via the generic
        // DeviceSettings bag without any vendor coupling here. Null when the device has no such zone.
        var followKey = d.Lighting?.ProfileFollowKey;
        var lighting = d.Lighting != null || d.KeyboardBrightness != null
            ? new LightingViewModel(d.Lighting, _svc.LightsForCurrentMode(cur), _svc.EnsureLightZone, _svc.PersistLighting,
                                    followKey != null && _svc.GetDeviceFlag(followKey, true),
                                    // On flip: persist the flag (AppController owns the vendor key), then have the
                                    // coordinator kick the re-apply so the lightbar repaints now (ON -> this
                                    // profile's palette; OFF -> its custom colour) instead of waiting for the next
                                    // switch. Safe: _lightingCoord is created before BuildUi and outlives rebuilds.
                                    v => { if (followKey != null) _svc.SetDeviceFlag(followKey, v);
                                           _lightingCoord.OnFollowsProfileFlipped(); },
                                    // The backlight slider verifies its write by read-back (VerifiedHwValue) and
                                    // snaps itself back when the write does not take, so it needs only the
                                    // success flag — the reason has no reader here and is deliberately dropped,
                                    // rather than starting to show a message the app never showed.
                                    d.KeyboardBrightness, lvl => _svc.SetKeyboardBrightness(lvl).ok,
                                    // The LampArray surface, for its OWNERSHIP flag only: opening the drawer must
                                    // not paint over a frame a host owns (LightingViewModel.Reapply). Null on a
                                    // machine that publishes none, which is every Linux build.
                                    host: _svc.LampArray)
            : null;
        // The post delegate is supplied here, not resolved inside: OptionsAssembler lives in the Application
        // layer now, which must not reference a UI toolkit.
        var opts = new OptionsAssembler(_svc, Notify, ConfirmCalibrationAsync, a => Dispatcher.UIThread.Post(a));
        // The three per-mode preset builds below are keyed by the CURRENT mode, and the key is derived from the
        // live profile — a PowerProfiles read, i.e. a WMI transaction on Windows. `cur` is that profile, read
        // once by the caller (the constructor and RebuildForLanguage hoist it), so all three pass it in: BuildUi
        // performs no profile read of its own. Through the parameterless forms these were three EC round-trips
        // taken on the UI thread while the UI was being built (the hazard recorded as D16 in
        // docs/hardware-actor-design.md); the values were always Settings' — only the key needed the hardware.
        var fan0 = _svc.CurrentFan(cur);   // current mode's fan preset (defaults if none saved)
        var vm = new MainViewModel(d, new UiActions(
            new ProfileActions(ApplyProfile, _svc.TurboToggles, SetTurbo),
            new FanSection(fan0, SetFan, SetFanCurve, ShowFanCurve),
            new GpuSection(_svc.CurrentGpuOc(cur), SetGpuOc),
            // CPU power is the odd one out: a PLACEHOLDER, not a read. Its construction read used to run right
            // here on the UI thread, and `null` is exactly what a failed read would have given — CpuViewModel
            // maps an unknown id to Balanced. The real value arrives from the first background pass (see the
            // prime in BackgroundPass / `_cpuPrimed`). The three BATTERY rows below are deferred in the same way
            // (their ports are read to fill them), and so are the Options drawer's rows; both sets are primed off
            // the UI thread right after BuildUi returns — OptionsPage by OptionsViewModel.Prime(), the battery
            // rows by BatteryViewModel.Prime(). Nothing else in this record list is deferred, but not because
            // nothing else needed the hardware: the fan/GPU/Curve-Optimizer values come from the Settings graph
            // while their KEY came from the port, and they take `cur` for it above. A row is deferred when its
            // value is readable only from a port; these three are keyed by one.
            new CpuSection(d.CpuPower?.Modes ?? [], null, SetCpuPower),
            new CoSection(d.CurveOptimizer?.Domains ?? [], _svc.CurrentCoDomains(cur), SetCo),
            new BatterySection(d.Battery, opts.BatteryLimit(), opts.BatteryCalibration(), opts.BatteryChargeMode()),
            new OptionsSection(opts.Toggles(), opts.Choices(), opts.PowerSourceProfiles(),
                _svc.TurboToggles, SetTurboToggles,
                b => _svc.SetClamshell(b), b => _svc.SetAutostart(b),
                _svc.Language, SetLanguage)),
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
        if (language == _svc.Language) return;
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

        Loc.Use(_svc.Language);
        var cur = _svc.CurrentProfile();          // read once, before the UI — see the same hoist in the constructor
        (_vm, _windows, _tray, _lighting) = BuildUi(cur);
        _lightingCoord.Attach(_vm, _lighting);    // re-point the persistent coordinator at the fresh view-models
        _vm.OptionsPage?.Prime();                 // the rebuilt rows hold placeholders again — see the constructor
        _vm.Battery?.Prime();                     // ...and so do the battery section's three charging rows
        _lighting?.Prime();                       // ...and so does the backlight slider
        _lastModeKey = _svc.CurrentModeKey(cur);  // freshly seeded VMs; don't let Refresh re-trigger a mode reload
        _lastProfileId = cur?.Id ?? "";
        _cpuPrimed = false;                       // ...and the rebuilt CPU-power row holds a placeholder again
        _lightingCoord.ApplyFollowLighting(cur?.FlashColor, _svc.LightsForCurrentMode(cur));
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

    // Any special-key input -> ADOPT the keyboard brightness the hardware now holds (no debounce), but only while
    // the Lighting panel is visible so normal typing does ZERO work here. This is the one path where a read is an
    // author of intent — the Fn key is a user like the slider is, it just delivers its value through a read
    // (docs/state-and-events.md). The read is off-thread and coalesces back-to-back requests, so rapid presses
    // track without piling up or blocking input.
    // Marshaled to the UI thread like OnHotkey: on Linux the event fires on the evdev reader thread, and
    // IsLightingVisible/AdoptLightingIfVisible touch UI-bound state (incl. enumerating the Panels collection the
    // UI mutates).
    private void OnInputActivity() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm.IsLightingVisible) _vm.AdoptLightingIfVisible();
    });

    // ---- actions ----

    // Repaint the lighting from the profile we just applied, rather than waiting for the refresh pass to
    // rediscover it by polling: the firmware flashes the new palette the moment the profile byte is written, so
    // a repaint that lands ~750 ms later reads as a SECOND blink cycle of the keyboard and lightbar (and, if it
    // catches a still-running burst from the previous switch, in the PREVIOUS profile's colour). Every path that
    // changes the profile — pick, tray, hotkey, Turbo switch — goes through here.
    private void ApplyProfile(PerformanceProfile p)
    {
        var r = _svc.ApplyProfile(p);
        if (r.ok) _lightingCoord.OnProfileApplied(p);
        else Notify(Loc.T("Failed to set {0}", Loc.T(p.DisplayName)) + Err(r.error));
        Refresh();
    }

    // Turbo used as a switch (the "Turbo toggles" mode). SetTurbo reports the profile that landed (Turbo, or the
    // remembered base when switching off), so the lighting follows it without a read-back.
    private void SetTurbo(bool on)
    {
        var r = _svc.SetTurbo(on);
        if (r.applied is { } applied) _lightingCoord.OnProfileApplied(applied);
        else Notify(Loc.T("Turbo failed") + Err(r.error));
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
        var r = _svc.SetGpuOc(core, mem);
        if (!r.ok) Notify(Loc.T("GPU overclock failed") + Err(r.error));
    }

    // CPU power-mode overlay, applied + persisted per performance mode by the service. Like SetGpuOc: no
    // Refresh() (nothing shared depends on it); failure surfaced.
    private void SetCpuPower(string id)
    {
        var r = _svc.SetCpuPower(id);
        if (!r.ok) Notify(Loc.T("Power mode failed") + Err(r.error));
    }

    // CPU undervolt (all-core Curve Optimizer), applied + persisted per performance mode by the service. Unlike
    // SetGpuOc/SetCpuPower this may NOT run inline: the SMU mailbox transaction waits on a machine-wide lock that
    // other tuning tools also take, so it can block for seconds — on the dispatcher that would freeze the window
    // mid-drag. Hand it off and post the failure back, the same shape OptionsAssembler uses for its slow rows.
    private void SetCo(int[] counts)
    {
        _ = Task.Run(() =>
        {
            var r = _svc.SetCoValues(counts);
            // The reason travels WITH the result, so there is nothing to capture before the post: reading a
            // shared field after handing the work off was a race on the error itself.
            if (!r.ok) Dispatcher.UIThread.Post(() => Notify(Loc.T("CPU undervolt failed") + Err(r.error)));
        });
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
        if (applied != null)
        {
            Notify(Loc.T("Profile: {0}", Loc.T(applied.DisplayName)));
            _lightingCoord.OnProfileApplied(applied);
        }
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
    // CpuPrimed says the CPU-power row has a value to take and is NOT a mode change (wave 6): its row is built
    // with a placeholder, so without this the first pass's value would be dropped and the row would sit on
    // Balanced until the user switched profile.
    private readonly record struct Tick(
        PerformanceProfile? Current, IReadOnlyList<PerformanceProfile> Selectable, PerformanceProfile? Base,
        SensorSnapshot Sensors, BatteryInfoSnapshot Battery, string? Status, bool TurboToggles,
        bool ModeChanged, FanPreset? Fan, GpuOcPreset? Gpu, string? CpuId, int[]? Co,
        bool ProfileChanged, bool CpuPrimed, AccentColor? Flash,
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
            var turbo = _svc.TurboToggles;

            bool modeChanged = modeKey != _lastModeKey;
            bool profileChanged = profileId != _lastProfileId;
            // The current mode's per-zone lights, read once and shared by both the mode- and profile-change paths
            // (same dict). Only read when something changed — the coordinator caches it for its re-paints. Keyed
            // off the profile read above rather than re-read: until this passed `current` in, the "ONE hardware
            // profile read this pass" claim two lines up was not true — this lookup did its own, inside the lock.
            var lights = (modeChanged || profileChanged) ? _svc.LightsForCurrentMode(current) : null;

            FanPreset? fan = null; GpuOcPreset? gpu = null; string? cpu = null; int[]? co = null;
            AccentColor? flash = null;
            // Performance mode changed (user pick / hotkey / power-source restore)? Apply that mode's saved fan +
            // GPU/CPU presets to the hardware here (background); reflect them in the UI on the UI pass.
            if (modeChanged)
            {
                _lastModeKey = modeKey;
                // The mode's axes come from ONE place — the domain's schedule, executed by the reconciler — which
                // drives the fans, the GPU offsets and the CPU power overlay on THIS thread (a mode switch is the
                // only trigger that touches the fans: ApplyModeFan is the one axis that takes _state while calling
                // its port, and it does that inside LaptopService) and hands the Curve Optimizer to the pool. Its
                // SMU mailbox waits on the machine-wide PCI lock other tuning tools also take, so inline it would
                // delay the Tick post below (chip/tray/presets) and hold the single-flight guard behind it. What
                // comes back is what this pass reflects in the UI — including the Curve Optimizer's stored
                // domains, read here rather than from the deferred apply, which has no value on this thread.
                var applied = _svc.Reconciler.Reapply(ReapplyTrigger.ModeChange);
                fan = applied.Fan; gpu = applied.GpuOc; cpu = applied.CpuPower; co = applied.Co;
            }
            // On a HARDWARE profile change (incl. base<->Turbo, which shares its base's preset KEY so the block
            // above won't fire) repaint the lighting on the UI pass (immediately, then a couple of retries).
            if (profileChanged)
            {
                _lastProfileId = profileId;
                flash = current?.FlashColor;
            }

            // ---- the CPU-power row's prime (wave 6) ----
            // The row is built with a placeholder because its read used to run on the UI thread during BuildUi;
            // this is that read, moved here, once per UI build. Deliberately NOT ApplyModeCpuPower: the app does
            // not write an overlay at startup and must not start — the mode-change block above is the only place
            // that applies, and it wins when it fired (it hands back what the hardware holds after applying).
            // Two independent sources can produce a value, and the UI pass must be told about either, because a
            // first pass after a UI build is not a mode change. `_cpuPrimed` is "this row already holds a real
            // value": RebuildForLanguage clears it, since a rebuild puts a fresh placeholder back.
            // Keyed on modeChanged and NOT on `cpu != null`, so the mode-change path reloads the row even when
            // the overlay came back unreadable — that is what it did before the prime existed, and `null` has a
            // meaning there (Balanced) rather than being a reason to skip the reload.
            var cpuPrimed = modeChanged || !_cpuPrimed;
            if (cpu == null && !_cpuPrimed) cpu = _svc.CurrentCpuPower();
            _cpuPrimed = true;   // the prime has run; an unreadable overlay stays unreadable, so no per-tick retry

            _svc.ApplyCustom(sensors);   // Custom mode: drive each fan from its curve (or fixed speed) using live temps
            var baseP = _svc.BaseProfile(current);

            var t = new Tick(current, selectable, baseP, sensors, battery, status, turbo,
                             modeChanged, fan, gpu, cpu, co, profileChanged, cpuPrimed, flash, lights);
            Dispatcher.UIThread.Post(() => UiPass(t));
        }
        // A hardware throw aborts the WHOLE pass, not just the axis it came from: the reads above, the
        // mode-change re-apply and the UI post are all inside this try. Swallowed because a stalled ACPI-EC is
        // routine and the poll comes round again in 3 s — but the next pass does NOT retry what this one skipped,
        // and the comment here used to say it did. _lastModeKey is latched at the top of the mode-change block,
        // BEFORE any axis is applied, so a throw leaves the key recorded and the next pass computes
        // modeChanged == false: the axis that never ran is never run, until some other event changes the mode
        // again. Whether the axes SHOULD be retried is a question about hardware this machine cannot settle, so
        // it is recorded as the owner's call rather than decided here — docs/domain-refactoring-plan.md §7.
        catch { }
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
            if (t.Co is { } co) _vm.ReloadCo(co);
        }
        // CPU power is reloaded on its OWN flag rather than under the mode change above, because the two are
        // independent: a mode change does not fill the row's placeholder (a fresh UI's first pass does, and is
        // not a mode change), and the prime does not need a mode change to have happened. CpuId is null when the
        // overlay was unreadable, and is still loaded: `Load` maps it to Balanced, which is what this row has
        // always shown for an overlay it cannot read.
        if (t.CpuPrimed) _vm.ReloadCpuPower(t.CpuId);
        // ONE lighting hand-off for both kinds of change (Lights is non-null whenever either fired). Two separate
        // calls made the coordinator paint twice per switch and let a mode change repaint from a flash colour the
        // profile hand-off had not updated yet.
        if (t.ModeChanged || t.ProfileChanged)
            _lightingCoord.OnStateChanged(t.ProfileChanged, t.Current?.Id, t.Flash, t.Lights!);

        _vm.Refresh(t.Current, t.Selectable, t.TurboToggles, t.Base, t.Sensors, t.Battery, t.Status);
        // No lighting sync on the UI pass: a read taken here carries whatever the wire last held, so the pass
        // used to ADOPT the previous profile's brightness (and persist it) while our own write was still in
        // flight. The Fn-key read on OnInputActivity is the event path; everything else re-applies instead.
        _tray.Update(t.Current, t.Selectable);
    }

    private void ExitApp()
    {
        _timer.Stop();
        GateStatsLog.Write();   // inline, not queued: a task started here might never get to run
        _lightingCoord.Dispose();
        _tray.Dispose();
        _svc.Dispose();
        _desktop.Shutdown();
    }

    // ---- helpers ----

    private void Notify(string text) => _vm.Status = text;

    private static string Err(string? e) => e != null ? $": {e}" : string.Empty;
}
