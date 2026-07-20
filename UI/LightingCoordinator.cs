using System.Threading.Tasks;
using Avalonia.Threading;
using AcerHelper.Features;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>Owns the lighting re-apply state machine that used to live inline in <see cref="AppController"/>:
/// the post-switch re-apply timer, the sleep/resume re-paint, and the clamshell lid blank/restore. Built with
/// the <see cref="LaptopService"/> BEFORE any UI exists (so the follows-profile toggle can reach it), then
/// pointed at the current view-models via <see cref="Attach"/> after each <c>BuildUi</c> (startup + live
/// language rebuild). AppController drives it from its refresh loop (<see cref="OnProfileChanged"/> /
/// <see cref="OnModeChanged"/>) and forwards the startup/rebuild repaint (<see cref="ApplyFollowLighting"/>)
/// and the follows-profile flip (<see cref="OnFollowsProfileFlipped"/>).
///
/// It does NO hardware reads of its own: the current profile's flash colour and the mode's per-zone lights are
/// read on the (background) refresh pass and handed in, then cached here so the timer / resume / lid / follows-
/// flip re-paints reuse them. This keeps every path on the UI thread free of a blocking EC read — the HID
/// writes it issues are already async (EneHidController's background writer).</summary>
internal sealed class LightingCoordinator : IDisposable
{
    private readonly LaptopService _svc;
    private readonly DispatcherTimer _lightReapply;   // re-applies lighting for a while after a profile switch
    private int _lightReapplyLeft;                     // remaining re-apply ticks

    // How many re-apply ticks a kick schedules (× the 400 ms interval ≈ 3 s). Two jobs: (1) override a late
    // firmware palette repaint after a profile switch (the original 2-tick purpose), and (2) self-heal a
    // corrupted apply on a display-contended HID-over-I2C bus (booted-with-external-display), where the first
    // apply can land amber/partial — each retry is a fresh chance to hit a clean bus window, and once one lands
    // it sticks (the device is last-write-wins; idle state isn't re-corrupted). Bounded on purpose: if the bus
    // is corrupting CONSTANTLY (no clean window) this just retries for ~3 s and stops, rather than flickering
    // forever. Re-asserting an already-correct colour is visually silent (the firmware re-latches the same value).
    private const int ReapplyTicks = 8;
    // Of those ticks, how many also RE-SEND the profile palette flash. The flash is a global write that briefly
    // repaints the whole keyboard with the palette colour before the per-zone paint overrides it, so re-sending
    // it every tick would add a visible shimmer on a follows-profile lightbar. Limit it to the first couple of
    // ticks (matching the pre-existing behaviour) — enough to catch a late firmware repaint / give the lightbar
    // a couple of clean-window chances — while the per-zone KEYBOARD paint (the actual "half green/half orange"
    // self-heal) is re-applied on EVERY tick, which is silent when it's already correct.
    private const int FlashTicks = 2;
    private readonly ResumeWatcher _resume;            // re-applies lighting on wake (firmware drops it over sleep)
    private readonly LidWatcher _lid;                  // blanks/restores the RGB as the lid shuts/opens in clamshell mode
    private bool _lidShut;           // last lid state from the LidWatcher (Windows); true = shut. Drives blanking.
    private bool _blankedByLid;      // WE blanked the backlight under a shut lid; restore on the next open (see OnLidChanged)
    private DateTime _lastResume = DateTime.MinValue;   // coalesce Windows' double Resume event (see OnResume)

    // Cached lighting inputs, refreshed by the caller (who reads them off the UI thread): the current profile's
    // palette flash colour and the current mode's per-zone lights. The re-paint paths (timer/resume/lid/follows-
    // flip) reuse these instead of reading the EC/Settings on the UI thread. _lights is the LIVE dictionary
    // reference (same aliasing as before — only the UI thread touches it after hand-off).
    private AccentColor? _flash;
    private Dictionary<string, LightSettings> _lights = new();

    // The current (rebuildable) view-models. Reassigned by Attach after each BuildUi; a re-apply/blank always
    // drives the live pair. Non-null before any callback can run (Attach is called synchronously right after
    // BuildUi, and every watcher callback is posted to the UI thread — so it can't run mid-construction).
    private MainViewModel _vm = null!;
    private LightingViewModel? _lighting;

    public LightingCoordinator(LaptopService svc)
    {
        _svc = svc;

        // Acer firmware repaints the lit zones with the profile's palette colour a moment AFTER our WMI profile
        // set. A single re-apply can land too early (before that repaint), so we re-apply the mode's lighting
        // several times (ReapplyTicks) right after the switch/startup — whichever tick lands after the repaint
        // (or in a clean window on a display-contended bus) overrides it and it then stays. (Only user-driven
        // zones are re-applied; a "follows profile" lightbar has no panel and is left as the firmware's palette.
        // The palette flash itself is firmware and can't be suppressed.)
        _lightReapply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _lightReapply.Tick += (_, _) =>
        {
            // Re-send the flash only on the first FlashTicks ticks (_lightReapplyLeft counts down from
            // ReapplyTicks); every tick re-applies the per-zone keyboard paint.
            Paint(includeFlash: _lightReapplyLeft > ReapplyTicks - FlashTicks);
            if (--_lightReapplyLeft <= 0) _lightReapply.Stop();
        };

        // Sleep/hibernate clears the EC's RGB state; re-apply the current mode's lighting on wake — ONCE.
        // The internal keyboard is always connected and never re-enumerates across sleep, so its HID handle
        // stays valid and a single write lands; there's nothing to poll for. (See OnResume.)
        _resume = new ResumeWatcher(() => Dispatcher.UIThread.Post(OnResume));
        _resume.Start();

        // In clamshell (keep-awake) mode the machine stays on with the lid shut, but the backlight is then hidden —
        // so blank it while the lid is closed and restore it on open. Gated on clamshell being enabled (see
        // OnLidChanged): without it a lid-close just sleeps the machine and the backlight is moot (restored by the
        // resume re-apply above). The watcher fires on its message thread, so marshal to the UI thread here.
        _lid = new LidWatcher(open => Dispatcher.UIThread.Post(() => OnLidChanged(open)));
        _lid.Start();
    }

    /// <summary>Point the coordinator at the current view-models. Called after each BuildUi (startup + live
    /// language rebuild) so the re-apply/blank paths always drive the live pair.</summary>
    public void Attach(MainViewModel vm, LightingViewModel? lighting)
    {
        _vm = vm;
        _lighting = lighting;
    }

    /// <summary>The follows-profile flag was flipped in the Lighting panel: kick the re-apply so the lightbar
    /// repaints now (ON -> this profile's palette; OFF -> its custom colour) instead of waiting for the next
    /// switch, reusing the cached flash/lights. (Persisting the flag itself stays in AppController.)</summary>
    public void OnFollowsProfileFlipped() => KickReapply();

    // Schedule a bounded re-apply burst (see ReapplyTicks). Restarting the timer coalesces overlapping kicks
    // into one running burst. Runs on the UI thread (all callers are UI-thread), so no synchronisation needed.
    private void KickReapply()
    {
        _lightReapplyLeft = ReapplyTicks;
        _lightReapply.Stop(); _lightReapply.Start();
    }

    /// <summary>A HARDWARE profile change (incl. base&lt;-&gt;Turbo, which shares its base's preset KEY). Repaint
    /// the lighting IMMEDIATELY with the new profile's flash colour + the mode's lights (both read off the UI
    /// thread by the caller) — a following lightbar must show the NEW profile's palette at once, otherwise the
    /// previous colour lingers for a beat (NitroSense flips it instantly). Then repeat a couple of times to catch
    /// any late firmware repaint.</summary>
    public void OnProfileChanged(AccentColor? flash, Dictionary<string, LightSettings> lights)
    {
        _flash = flash;
        _lights = lights;
        Paint();
        KickReapply();
    }

    /// <summary>The performance MODE changed (its preset key): reflect the mode's saved lighting (handed in by the
    /// caller), or keep the backlight dark if it's hidden under a shut lid (don't light a hidden keyboard).</summary>
    public void OnModeChanged(Dictionary<string, LightSettings> lights)
    {
        _lights = lights;
        if (BacklightHidden) BlankBacklight();
        else { _vm.ReloadLighting(_lights); KickReapply(); }   // retry the paint for a few seconds (contended-bus self-heal)
    }

    /// <summary>Startup / language-rebuild paint: seed the cached flash colour + mode lights (read by the caller
    /// off the UI thread) and paint, then re-apply for a few seconds. Startup is exactly the boot-with-external-
    /// display case where the first apply is most likely to land corrupted on the contended HID-over-I2C bus, so
    /// the burst gives the initial lighting several chances to settle correctly.</summary>
    public void ApplyFollowLighting(AccentColor? flash, Dictionary<string, LightSettings> lights)
    {
        _flash = flash;
        _lights = lights;
        Paint();
        KickReapply();
    }

    // Repaint from the cached (flash, lights). First paint the profile's palette on a follow-lightbar (a GLOBAL
    // write that also flashes the keyboard), then re-apply the per-zone colours so the keyboard settles back to
    // its own custom colour on top. The HID writes are async (EneHidController queues them), so this never
    // blocks the UI thread; and it reads nothing from the EC (uses the cache). Called immediately on a switch
    // and repeated by _lightReapply / resume / lid as a safety net against a late firmware repaint.
    private void Paint(bool includeFlash = true)
    {
        if (BacklightHidden) { BlankBacklight(); return; }   // lid shut in clamshell mode -> keep it dark
        if (includeFlash && _lighting is { ShowFollowsProfile: true, FollowsProfile: true } && _flash is { } flash)
            _svc.Device.Lighting?.SetProfileFlash(flash);
        _vm.ReloadLighting(_lights);
    }

    // Wake from sleep/hibernation: re-establish the RGB the firmware dropped over the suspend — a SINGLE
    // re-apply from the cache (the internal keyboard's HID handle survives sleep, so one write lands; no
    // readiness to poll for). Windows raises PowerModeChanged(Resume) ~twice per wake (PBT_APMRESUMEAUTOMATIC +
    // PBT_APMRESUMESUSPEND); coalesce within a few seconds so the palette isn't flashed twice.
    private void OnResume()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastResume).TotalSeconds < 3) return;
        _lastResume = now;
        Paint();
        // GPU clock offsets are volatile GPU state too: the dGPU power-cycles across suspend (Optimus D3-cold)
        // and comes back at 0 offset, so re-assert the current mode's offsets; likewise the CPU power mode.
        // Off the UI thread — ApplyModeCpuPower reads the EC (profile) which can stall right after wake, and we
        // must not block the UI. No UI reflect needed (values unchanged); no-op when those ports are absent.
        _ = Task.Run(() => { _svc.ApplyModeGpuOc(); _svc.ApplyModeCpuPower(); });
    }

    // Lid opened/closed: shut while clamshell keep-awake is enabled -> blank the (now hidden) backlight without
    // touching the app's stored per-zone state; opened -> restore the current mode's lighting from the cache. The
    // blank is gated on clamshell (a lid-close otherwise sleeps the machine and resume re-applies), but the
    // RESTORE is gated on _blankedByLid — the remembered fact that we blanked — NOT on clamshell still being
    // enabled at open time: the user can flip clamshell off from the external screen while the lid is shut, and
    // the EC-latched blank would otherwise stick until the next profile switch / resume / restart.
    // Posted to the UI thread by the lid watcher, so all HID writes stay serialized with the rest of the app.
    private void OnLidChanged(bool open)
    {
        _lidShut = !open;   // remembered so a repaint (profile/mode/power change) under a shut lid re-blanks (see BacklightHidden)
        if (open)
        {
            // Restore the mode's lighting we blanked on close — one apply (the machine stayed awake in
            // clamshell mode, so the handle is live).
            if (_blankedByLid) { _blankedByLid = false; Paint(); }
        }
        else if (_svc.Device.Clamshell?.Enabled == true)
            BlankBacklight();
    }

    // Blank the hidden backlight and remember that WE did it, so the next lid-open restores it (see OnLidChanged).
    private void BlankBacklight()
    {
        _blankedByLid = true;
        _svc.Device.Lighting?.Blank();
    }

    // True while the backlight must stay dark: the lid is shut AND clamshell keep-awake is on, so the machine runs
    // with a hidden keyboard/lightbar. Every repaint path checks this, so a profile/mode/power-source change under
    // a closed lid re-blanks instead of lighting the (hidden) keyboard back up until the lid is next opened.
    private bool BacklightHidden => _lidShut && _svc.Device.Clamshell?.Enabled == true;

    public void Dispose()
    {
        _lightReapply.Stop();
        _resume.Dispose();
        _lid.Dispose();
    }
}
