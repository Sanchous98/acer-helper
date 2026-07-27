using System.Threading.Tasks;
using Avalonia.Threading;
using AcerHelper.Features;
using AcerHelper.Localization;
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
    private int _flashTicksLeft;                       // of those, how many still re-send the palette

    // How many re-apply ticks a kick schedules (× the 400 ms interval ≈ 3 s). Two jobs: (1) restore the per-zone
    // colours the firmware's own palette repaint clobbers after a profile switch, and (2) self-heal a
    // corrupted apply on a display-contended HID-over-I2C bus (booted-with-external-display), where the first
    // apply can land amber/partial — each retry is a fresh chance to hit a clean bus window, and once one lands
    // it sticks (the device is last-write-wins; idle state isn't re-corrupted). Bounded on purpose: if the bus
    // is corrupting CONSTANTLY (no clean window) this just retries for ~3 s and stops, rather than flickering
    // forever. Re-asserting an already-correct colour is visually silent (the firmware re-latches the same value).
    private const int ReapplyTicks = 8;
    // Of those ticks, how many also RE-SEND the profile palette flash. The flash is a global write that briefly
    // repaints the whole keyboard with the palette colour before the per-zone paint overrides it — one more
    // visible blink of the keyboard and lightbar. It is worth that cost on the RESTORE paths (startup, resume,
    // lid open, host hand-back), where nothing else re-establishes the palette and the bus may be contended, so
    // those kicks ask for it. A profile SWITCH does not: the firmware flashes the new palette itself at the
    // moment of the write and we now send ours in the same instant (see OnProfileApplied), so a re-send 400 ms
    // later is simply a second blink cycle. Hence the flag on KickReapply. The per-zone KEYBOARD paint (the
    // actual "half green/half orange" self-heal) still runs on EVERY tick, which is silent when already correct.
    private const int FlashTicks = 2;

    // How long a locally-applied profile may stay unconfirmed by the refresh pass before we stop suppressing
    // pass-driven repaints. Only a write the firmware silently refused ever gets here; the normal case is
    // confirmed within one pass (~1 s).
    private const double PendingTimeoutSeconds = 5;
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

    // A profile WE applied that the refresh pass hasn't reported back yet (id + when we applied it). The pass
    // discovers the profile by polling, so for the ~1 s until it catches up every pass still describes the
    // PREVIOUS profile — repainting from one of those undoes the switch's lighting. See OnStateChanged.
    private string? _pendingId;
    private DateTime _pendingSince;

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
            // Re-send the palette only while this burst has flash ticks left (a switch-driven burst has none —
            // see KickReapply); every tick re-applies the per-zone keyboard paint.
            var withFlash = _flashTicksLeft > 0;
            if (withFlash) _flashTicksLeft--;
            Paint(includeFlash: withFlash);
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

        // A host (Windows Dynamic Lighting / a LampArray app) taking or releasing the backlight changes who
        // paints it — see OnHostOwnerChanged. Fires on the bridge's worker thread, so marshal like the watchers
        // above; posting (not sending) also means it can't run before Attach has supplied the view-models.
        if (_svc.LampArray is { } lamps)
            lamps.OwnerChanged += hostOwns => Dispatcher.UIThread.Post(() => OnHostOwnerChanged(hostOwns));
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
    public void OnFollowsProfileFlipped() => KickReapply(withFlash: true);

    // Schedule a bounded re-apply burst (see ReapplyTicks). Restarting the timer coalesces overlapping kicks
    // into one running burst. withFlash decides whether its first ticks also re-send the profile palette (see
    // FlashTicks) — restore paths want that, a profile switch does not. Runs on the UI thread (all callers are
    // UI-thread), so no synchronisation needed.
    private void KickReapply(bool withFlash)
    {
        _lightReapplyLeft = ReapplyTicks;
        _flashTicksLeft = withFlash ? FlashTicks : 0;
        _lightReapply.Stop(); _lightReapply.Start();
    }

    /// <summary>A profile was just applied BY US (user pick, tray, hotkey, Turbo switch) — the caller passes the
    /// profile that actually landed, so nothing has to be read back out of the hardware. Repaint NOW.
    ///
    /// This used to wait for the refresh pass to DISCOVER the change by polling, which is what produced the
    /// double blink: the firmware flashes the new palette the instant the profile byte is written, and our own
    /// palette write then landed ~750 ms later as a second, separate flash cycle. Painting here puts our write
    /// in the same instant as the firmware's, so the two coincide into one — and the burst we kick deliberately
    /// carries NO further palette re-sends, only the per-zone self-heal.</summary>
    public void OnProfileApplied(PerformanceProfile applied)
    {
        _pendingId = applied.Id;          // suppress the stale passes still describing the previous profile
        _pendingSince = DateTime.UtcNow;
        _flash = applied.FlashColor;
        Paint();
        KickReapply(withFlash: false);
    }

    /// <summary>The refresh pass observed a profile and/or mode change (the profile's flash colour and the
    /// mode's per-zone lights are read off the UI thread by the caller and handed in). Covers changes we did
    /// NOT make — the firmware's own Turbo key, another tool, a power-source restore — and delivers the new
    /// mode's saved zone colours after one of our own switches.</summary>
    public void OnStateChanged(bool profileChanged, string? profileId, AccentColor? flash,
                               Dictionary<string, LightSettings> lights)
    {
        // A switch of ours that the poll hasn't caught up to yet: until it does, every pass still reports the
        // PREVIOUS profile, and repainting from one of those puts the old palette and the old mode's zones back
        // over the profile the user just picked ("sometimes the old one first, then the new one"). Drop those
        // passes; the one that finally reports our profile clears the claim. The timeout is the safety valve for
        // a write the firmware silently refused — without it we would ignore the poll forever.
        if (_pendingId != null)
        {
            if ((DateTime.UtcNow - _pendingSince).TotalSeconds > PendingTimeoutSeconds) _pendingId = null;
            else if (profileId != _pendingId) return;
            else { _pendingId = null; profileChanged = false; }   // caught up — OnProfileApplied already painted it
        }

        _lights = lights;
        if (BacklightHidden) { BlankBacklight(); return; }   // lid shut in clamshell mode -> keep it dark

        // An out-of-band profile change is the only case left that still needs the palette: nobody has painted
        // it yet, so adopt the colour, show it at once (otherwise the previous one lingers for a beat) and let
        // the burst re-assert it. Everything else — a mode-only change, or the tail of our own switch — just
        // binds the new mode's zones; the profile's colour is either unchanged or already on screen.
        if (profileChanged) _flash = flash;
        Paint(includeFlash: profileChanged);
        KickReapply(withFlash: profileChanged);
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
        KickReapply(withFlash: true);
    }

    // Repaint from the cached (flash, lights). First paint the profile's palette on a follow-lightbar (a GLOBAL
    // write that also flashes the keyboard), then re-apply the per-zone colours so the keyboard settles back to
    // its own custom colour on top. The HID writes are async (EneHidController queues them), so this never
    // blocks the UI thread; and it reads nothing from the EC (uses the cache). Called immediately on a switch
    // and repeated by _lightReapply / resume / lid as a safety net against a late firmware repaint.
    private void Paint(bool includeFlash = true)
    {
        if (BacklightHidden) { BlankBacklight(); return; }   // lid shut in clamshell mode -> keep it dark

        // A host owns the surface (Dynamic Lighting / a LampArray app): the app must not paint over it — but it
        // MUST re-assert the host's last frame, because every caller of Paint() is an event that clobbers the
        // RGB behind everyone's back (the EC forces its amber profile-flash on a profile switch, sleep drops the
        // state, a lid-open restores from black). Without this the keyboard would sit amber until the host
        // happened to send its next frame. The profile flash itself is deliberately skipped: it is a global
        // write that would visibly fight the host's colours.
        if (_svc.LampArray is { HostOwnsLighting: true } lamps) { lamps.Reassert(); return; }

        if (includeFlash && _lighting is { ShowFollowsProfile: true, FollowsProfile: true } && _flash is { } flash)
            _svc.Device.Lighting?.SetProfileFlash(flash);
        _vm.ReloadLighting(_lights);
    }

    /// <summary>A LampArray host took (or released) the backlight. Taking it: nothing to do — the bridge is
    /// already painting, and <see cref="Paint"/> now yields to it; we only tell the user, because the Lighting
    /// panel's controls no longer describe what the keyboard is showing (G HUB blocks its own lighting UI in the
    /// same situation). Releasing it: the surface is frozen on the host's last frame, so repaint the app's own
    /// lighting for the current mode right away — from the cache, no hardware reads on the UI thread.</summary>
    private void OnHostOwnerChanged(bool hostOwns)
    {
        _vm.Status = Loc.T(hostOwns
            ? "Keyboard lighting is controlled by Windows Dynamic Lighting"
            : "Keyboard lighting is back under app control");
        if (hostOwns) return;
        Paint();
        KickReapply(withFlash: true);   // the EC may still repaint late after a host hand-back; the burst overrides it
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
