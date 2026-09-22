using System.Threading.Tasks;
using Avalonia.Threading;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Composition;
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
/// It does NO hardware reads of its own: the current profile's flash colour is read on the (background) refresh
/// pass and handed in, and cached here so the timer / resume / lid / follows-flip re-paints reuse it. The
/// per-zone lighting is not cached here at all — the section holds it as values and re-applies them when asked
/// (<see cref="LightingViewModel.Repaint"/>), and a pass that reports a change hands in the mode's own door
/// instead of a copy of its contents. This keeps every path on the UI thread free of a blocking EC read — the
/// HID writes it issues are already async (EneHidController's background writer).</summary>
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
    // visible blink of keyboard and lightbar. Worth that cost on the RESTORE paths (startup, resume, lid open),
    // where nothing else re-establishes the palette and the bus may be contended, so those kicks ask for it.
    // A profile SWITCH does not: the firmware flashes the new palette itself at the moment of
    // the write and we now send ours in the same instant (see OnProfileApplied), so a re-send 400 ms later is
    // simply a second blink cycle. The per-zone KEYBOARD paint (the actual "half green/half orange" self-heal)
    // still runs on EVERY tick, which is silent when already correct. See docs/lighting-an18-61.md.
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
    // palette flash colour. The per-zone lights are NOT cached here any more — they belong to the lighting
    // section, which holds them as values and re-applies them itself (LightingViewModel.Repaint), so this class
    // keeps no reference into the settings graph and a burst tick cannot paint a state older than the user's
    // last edit.
    private AccentColor? _flash;

    // A mode the refresh pass reported while the backlight was hidden: the panels could not be rebound then
    // (nothing may paint under a shut lid in clamshell), so the door is kept for the paint that restores them —
    // the lid watcher's (see OnLidChanged). Null the rest of the time, which is the ordinary case.
    private ILightZoneMode? _rebindWhenVisible;

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
    /// profile that actually landed, so nothing has to be read back out of the hardware. Repaint NOW, in the same
    /// instant as the firmware's own palette flash, so the two coincide into one.
    ///
    /// This used to wait for the refresh pass to DISCOVER the change by polling, which is what produced the double
    /// blink: the firmware flashes the new palette the instant the profile byte is written, and our own palette
    /// write then landed ~750 ms later as a second, separate flash cycle. The burst we kick here deliberately
    /// carries NO further palette re-sends, only the per-zone self-heal. See docs/lighting-an18-61.md.</summary>
    public void OnProfileApplied(PerformanceProfile applied)
    {
        _pendingId = applied.Id;          // suppress the stale passes still describing the previous profile
        _pendingSince = DateTime.UtcNow;
        _flash = applied.FlashColor;
        Paint();
        KickReapply(withFlash: false);
    }

    /// <summary>The refresh pass observed a profile and/or mode change (the profile's flash colour and the door
    /// for the current mode's lighting are read off the UI thread by the caller and handed in). Covers changes we
    /// did NOT make — the firmware's own Turbo key, another tool, a power-source restore — and delivers the new
    /// mode's saved zone colours after one of our own switches.
    ///
    /// <paramref name="mode"/> is the MODE's own door, not a copy of its values: the panels rebind through it, so
    /// the mode they end up bound to is the one this pass read, and the values they take are read under the graph
    /// lock by whoever asks (LaptopService.LightsForCurrentMode states why the mode is fixed at the read).</summary>
    public void OnStateChanged(bool profileChanged, string? profileId, AccentColor? flash, ILightZoneMode mode)
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

        if (BacklightHidden)
        {
            // Lid shut in clamshell mode: nothing may paint, so the panels are NOT rebound yet — but the mode this
            // pass reported is KEPT, because the lid watcher's restore is the paint that will need it. Without
            // the hand-off a mode that moved under a shut lid would surface on the next lid-open as the OLD
            // mode's lighting: the panels would still be bound to it, and nothing else would tell them otherwise
            // until the following pass. This is the door and not a copy of its values, so nothing is held here
            // that is not already the section's to read.
            _rebindWhenVisible = mode;
            BlankBacklight();
            return;
        }
        _rebindWhenVisible = null;

        // An out-of-band profile change is the only case left that still needs the palette: nobody has painted
        // it yet, so adopt the colour, show it at once (otherwise the previous one lingers for a beat) and let
        // the burst re-assert it. Everything else — a mode-only change, or the tail of our own switch — just
        // binds the new mode's zones; the profile's colour is either unchanged or already on screen.
        if (profileChanged) _flash = flash;
        Paint(includeFlash: profileChanged, rebind: mode);
        KickReapply(withFlash: profileChanged);
    }

    /// <summary>Startup / language-rebuild paint: seed the cached flash colour (read by the caller off the UI
    /// thread) and paint, then re-apply for a few seconds. Startup is exactly the boot-with-external-display case
    /// where the first apply is most likely to land corrupted on the contended HID-over-I2C bus, so the burst
    /// gives the initial lighting several chances to settle correctly.
    ///
    /// IT TAKES NO LIGHTING: the sections are built with the current mode's values only moments before this runs
    /// (<c>BuildUi</c> hands them over, then <c>Attach</c>, then this), so there is nothing to rebind and the
    /// paint is the panels' own values.</summary>
    public void ApplyFollowLighting(AccentColor? flash)
    {
        _flash = flash;
        Paint();
        KickReapply(withFlash: true);
    }

    // Repaint from the cached flash colour and the section's own values. First paint the profile's palette on a
    // follow-lightbar (a GLOBAL write that also flashes the keyboard), then re-apply the per-zone colours so the
    // keyboard settles back to its own custom colour on top. The HID writes are async (EneHidController queues
    // them), so this never blocks the UI thread; and it reads nothing from the EC — it asks the section, which
    // holds the values as its own (LaptopService's door is not re-entered here). Called immediately on a switch
    // and repeated by _lightReapply / resume / lid as a safety net against a late firmware repaint.
    //
    // `rebind` is the ONE case that needs state this class does not have: a pass that reports the mode or the
    // profile moved hands in the new mode's door, and the panels rebind through it before painting (exactly one
    // apply per Paint either way — a rebind already applies, so the two must not both run).
    private void Paint(bool includeFlash = true, ILightZoneMode? rebind = null)
    {
        if (BacklightHidden) { BlankBacklight(); return; }   // lid shut in clamshell mode -> keep it dark

        if (includeFlash && _lighting is { ShowFollowsProfile: true, FollowsProfile: true } && _flash is { } flash)
            _svc.Device.Lighting?.SetProfileFlash(flash);
        if (rebind is { } mode) _vm.ReloadLighting(mode);
        else _vm.RepaintLighting();
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
        // The HARDWARE half of a wake, from the one place that knows the schedule: the GPU clock offsets (the dGPU
        // power-cycles across suspend, Optimus D3-cold, and comes back at 0), the CPU power overlay and the Curve
        // Optimizer (SMU state the platform restores to stock across a power transition). The reconciler drives
        // all three off this (UI) thread — ApplyModeCpuPower reads the EC, which can stall right after wake, and
        // we must not block the UI — in the domain's order, under ONE catch, which is this path's guarantee and
        // not one shared with the other sites: the failure is swallowed because the device can be tearing down
        // (an exit racing the wake) while the task runs, because the next resume or mode switch re-asserts, and
        // because an escaping throw here would be an unobserved task exception. No UI reflect needed (the values
        // are unchanged), so the outcome is discarded. No-op where those ports are absent.
        // See docs/nvidia-gpu-oc.md and docs/curve-optimizer-strix-point.md.
        _svc.Reconciler.Reapply(ReapplyTrigger.Resume);
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
            // clamshell mode, so the handle is live). A mode the pass reported while the lid was shut is bound
            // HERE, which is the paint that has to carry it (see OnStateChanged).
            if (_blankedByLid)
            {
                _blankedByLid = false;
                Paint(rebind: _rebindWhenVisible);
                _rebindWhenVisible = null;
            }
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
