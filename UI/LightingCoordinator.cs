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
/// It reads no hardware of its own EXCEPT at wake, and that one exception is deliberate: the current profile's
/// flash colour is read on the (background) refresh pass and handed in, and cached here so the timer / resume /
/// lid / follows-flip re-paints reuse it. The per-zone lighting is not cached here at all — the section holds it
/// as values and re-applies them when asked (<see cref="LightingViewModel.Repaint"/>), and a pass that reports a
/// change hands in the mode's own door instead of a copy of its contents. The announced landing does the same:
/// it resolves the LANDED profile's door through the service (<c>LightsForCurrentMode(applied)</c>, the overload
/// that takes an already-read profile and so costs no EC read) rather than repainting the section's stale cache,
/// which is what makes a profile saved at brightness 0 come back dark. This keeps every path on the UI thread
/// free of a blocking EC read. <see cref="OnResume"/> is the exception because a wake is also a source-change
/// moment the app cannot see from a cache: it drives the two Application use cases (<see cref="ReadBatteryInfo"/>
/// and <see cref="SyncPowerSource"/>) so a source that changed over sleep is restored BEFORE any paint, and the
/// battery read there is the cheap OS gauge, not an EC transaction. See OnResume.</summary>
internal sealed class LightingCoordinator : IDisposable, IProfileAnnouncer
{
    private readonly LaptopService _svc;
    // The one re-apply use case (Application/ReapplySettings.cs), owned through the constructor and SHARED with
    // the service's boot path and the controller's refresh pass: the resume handler names the ACTION
    // (Reapply.Run(ReapplyTrigger.Resume)), not the executor behind it.
    private readonly ReapplySettings _reapply;
    // The two use cases the WAKE source re-sync needs (see OnResume): the cheap OS battery read and the same
    // per-source restore the refresh pass drives. Held as Application use cases, not as the service, so this
    // stays the coordinator naming an ACTION rather than a hardware method.
    private readonly ReadBatteryInfo _readBattery;
    private readonly SyncPowerSource _syncSource;
    private readonly PeriodicSchedule _lightReapply;  // re-applies the per-zone lighting for a while after a profile switch
    private int _lightReapplyLeft;                     // remaining re-apply ticks

    // How many re-apply ticks a kick schedules (× the 400 ms interval ≈ 3 s). Two jobs: (1) restore the per-zone
    // colours the firmware's own palette repaint clobbers after a profile switch, and (2) self-heal a
    // corrupted apply on a display-contended HID-over-I2C bus (booted-with-external-display), where the first
    // apply can land amber/partial — each retry is a fresh chance to hit a clean bus window, and once one lands
    // it sticks (the device is last-write-wins; idle state isn't re-corrupted). Bounded on purpose: if the bus
    // is corrupting CONSTANTLY (no clean window) this just retries for ~3 s and stops, rather than flickering
    // forever. Re-asserting an already-correct colour is visually silent (the firmware re-latches the same value).
    //
    // THE BURST NEVER RE-SENDS THE PALETTE, and that is a rule rather than an omission. The palette flash is a
    // GLOBAL write that briefly repaints the whole keyboard with the palette colour before the per-zone paint
    // overrides it — one visible blink of keyboard AND lightbar. Every path that wants the palette sends it
    // ONCE, at the action instant (Paint); a burst tick re-asserts only the per-zone colours, which is silent
    // when already correct. The shape this replaced re-sent the palette on the first couple of ticks, so a
    // restore or a follows-profile flip blinked TWICE — the second write landing 400 ms after the first. One
    // flash per action is the rule; the per-zone self-heal is the safety net this burst keeps (see
    // docs/lighting-an18-61.md). A per-zone palette retry is not needed: re-sending the same palette is what
    // BLINKS, while the lightbar keeps its colour between profile switches.
    private const int ReapplyTicks = 8;

    // How long a locally-applied profile may stay unconfirmed by the refresh pass before we stop suppressing
    // pass-driven repaints. Only a write the firmware silently refused ever gets here; the normal case is
    // confirmed within one pass (~1 s).
    private const double PendingTimeoutSeconds = 5;

    // The window that coalesces a wake with the refresh pass that follows it. OnResume paints the current
    // profile's palette once; the 3 s poll then re-reads the same profile and reports it changed. When that pass
    // still describes the palette the wake just painted it is the TAIL of the wake, not a second action, and
    // repainting it is the other half of the reported resume double blink (see OnStateChanged). The window
    // covers the poll interval, so the tail lands inside it. A pass carrying a DIFFERENT palette is a genuine
    // restore and keeps its one flash.
    private const double WakeTailSeconds = 5;
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

    // The one place this class crosses onto the UI thread, as an injected delegate rather than a direct
    // Dispatcher call. In the app it is <c>Dispatcher.UIThread</c> — inline when already on the UI thread (the
    // pick/tray/hotkey paths), posted when the call comes from the pool (the sweep's switch) — and a test can
    // pass a synchronous poster so the announced repaint runs without a dispatcher loop, the same seam (and the
    // same measured reason) as <c>LightingViewModel</c>'s `post` and the option rows' poster.
    private readonly Action<Action> _uiPost;

    /// <summary>The default marshaller: run inline on the UI thread, else post. Stated once so the coordinator's
    /// own default and any future call site agree.</summary>
    private static void PostToUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public LightingCoordinator(LaptopService svc, ReapplySettings reapply,
                               ReadBatteryInfo readBattery, SyncPowerSource syncSource,
                               Action<Action>? uiPost = null)
    {
        _svc = svc;
        _reapply = reapply;
        _readBattery = readBattery;
        _syncSource = syncSource;
        _uiPost = uiPost ?? PostToUiThread;

        // Acer firmware repaints the lit zones with the profile's palette colour a moment AFTER our WMI profile
        // set. A single re-apply can land too early (before that repaint), so we re-apply the mode's lighting
        // several times (ReapplyTicks) right after the switch/startup — whichever tick lands after the repaint
        // (or in a clean window on a display-contended bus) overrides it and it then stays. (Only user-driven
        // zones are re-applied; a "follows profile" lightbar has no panel and is left as the firmware's palette.
        // The palette flash itself is firmware and can't be suppressed.)
        _lightReapply = new PeriodicSchedule(ReapplyTick, TimeSpan.FromMilliseconds(400), UiSchedule.Normal);

        // Sleep/hibernate clears the EC's RGB state; re-apply the current mode's lighting on wake — ONCE.
        // The internal keyboard is always connected and never re-enumerates across sleep, so its HID handle
        // stays valid and a single write lands; there's nothing to poll for. (See OnResume.)
        _resume = new ResumeWatcher(() => _uiPost(OnResume));
        _resume.Start();

        // In clamshell (keep-awake) mode the machine stays on with the lid shut, but the backlight is then hidden —
        // so blank it while the lid is closed and restore it on open. Gated on clamshell being enabled (see
        // OnLidChanged): without it a lid-close just sleeps the machine and the backlight is moot (restored by the
        // resume re-apply above). The watcher fires on its message thread, so marshal to the UI thread here.
        _lid = new LidWatcher(open => _uiPost(() => OnLidChanged(open)));
        _lid.Start();
    }

    /// <summary>Point the coordinator at the current view-models. Called after each BuildUi (startup + live
    /// language rebuild) so the re-apply/blank paths always drive the live pair.</summary>
    public void Attach(MainViewModel vm, LightingViewModel? lighting)
    {
        _vm = vm;
        _lighting = lighting;
    }

    /// <summary>The follows-profile flag was flipped in the Lighting panel: paint now so the lightbar repaints
    /// (ON -> this profile's palette; OFF -> its custom colour) instead of waiting for the next switch, reusing
    /// the cached flash. The toggle itself already rebuilt the panel (OFF) or dropped it (ON) synchronously, so
    /// the paint below is the single palette send for the flip; the burst that follows re-asserts only the
    /// per-zone colours. (Persisting the flag itself stays in AppController.)</summary>
    public void OnFollowsProfileFlipped()
    {
        if (_lighting is { ShowFollowsProfile: true, FollowsProfile: false })
        {
            // Taking the zone over: the panel was just built and already wrote its custom colour, and there is
            // no palette to send — the user's colour is what should show. Only keep the burst running.
            KickReapply();
            return;
        }
        Paint();                          // ON: send this profile's palette once, now
        KickReapply();                    // ...and back it with the per-zone self-heal, no further flash
    }

    // Schedule a bounded re-apply burst (see ReapplyTicks), or re-arm one already running. Restarting the timer
    // coalesces overlapping kicks into one running burst. The burst carries no palette flash — re-sending it on
    // a tick is what turned one action into two blinks (see ReapplyTicks); every path that wants the palette
    // paints it once, at the action instant, before calling this. Runs on the UI thread (all callers are
    // UI-thread), so no synchronisation needed.
    private void KickReapply()
    {
        _lightReapplyLeft = ReapplyTicks;
        _lightReapply.Restart();
    }

    // One re-apply tick of the bounded burst (see ReapplyTicks): re-apply the per-zone keyboard paint and stop
    // the burst on its last tick. No palette — see KickReapply.
    private void ReapplyTick()
    {
        Paint(includeFlash: false);
        if (--_lightReapplyLeft <= 0) _lightReapply.Stop();
    }

    /// <summary>A profile was just applied BY US (user pick, tray, hotkey, Turbo switch) — the caller passes the
    /// profile that actually landed, so nothing has to be read
    /// back out of the hardware. Paint NOW, in the same instant as the firmware's own palette flash, so the two
    /// coincide into one.
    ///
    /// IT REBINDS TO THE LANDED MODE, and that half is not optional (see <c>ApplyProfileApplied</c>): the section
    /// is still bound to the mode we are leaving at this instant, so a plain repaint would put the OLD profile's
    /// brightness back over the flash — a target saved at 0 stayed lit. The landed profile's own door is resolved
    /// here, so the flash is followed by the TARGET mode's saved zones.
    ///
    /// This used to wait for the refresh pass to DISCOVER the change by polling, which is what produced the double
    /// blink: the firmware flashes the new palette the instant the profile byte is written, and our own palette
    /// write then landed ~750 ms later as a second, separate flash cycle. The burst we kick here deliberately
    /// carries NO further palette re-sends, only the per-zone self-heal. See docs/lighting-an18-61.md.
    ///
    /// IT IS THE <see cref="IProfileAnnouncer"/> THE SWITCH USE CASE OWNS (<c>Application/ProfileSwitch.cs</c>),
    /// which is what makes the claim impossible to bypass: the port write and this call are one action, so the
    /// sweep's forced profile and its restore can no longer flash the palette without also suppressing the stale
    /// refresh pass. The use case may run on the POOL (the sweep does), so the body is marshalled to the UI thread;
    /// on the UI thread it runs inline, preserving the exact timing the pick/tray/hotkey paths have always had.</summary>
    public void OnProfileApplied(PerformanceProfile applied)
    {
        _uiPost(() => ApplyProfileApplied(applied));
    }

    private void ApplyProfileApplied(PerformanceProfile applied)
    {
        _pendingId = applied.Id;          // suppress the stale passes still describing the previous profile
        _pendingSince = DateTime.UtcNow;
        _flash = _svc.FlashColorOf(applied);

        // REBIND TO THE TARGET MODE, do not repaint the section's cache. At this instant the section is still
        // bound to the PREVIOUS mode — the refresh poll has not read the new one's door yet — so a plain repaint
        // re-applies the mode we are leaving. That was the owner's report: a target profile saved at brightness 0
        // came back LIT, because the write that followed the palette flash carried the old mode's brightness and
        // the palette-free burst kept re-asserting it until a later pass happened to rebind (if it did at all).
        // The door for the mode that LANDED is resolved here, from `applied` and not from a hardware read
        // (LightsForCurrentMode's overload), so the sequence is exactly the wanted one: the palette flash, then
        // the target mode's saved zones painted over it — 0 for a profile saved dark. The follow-up
        // OnStateChanged pass that reports the same profile is still consumed (it clears _pendingId and rebinds
        // to the same door again, idempotently), and a stale pass describing the previous profile is still
        // dropped by the claim above, so there is no second palette flash. See docs/lighting-an18-61.md.
        Paint(rebind: _svc.LightsForCurrentMode(applied));
        KickReapply();
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
        // the burst re-assert the zones. Everything else — a mode-only change, or the tail of our own switch —
        // just binds the new mode's zones; the profile's colour is either unchanged or already on screen.
        //
        // THE PASS THAT FOLLOWS A WAKE IS THE WAKE'S TAIL, NOT A SECOND ACTION. OnResume painted the profile's
        // palette at the wake instant; the poll then re-reads the same profile and reports it changed. Painting
        // that palette again is the resume half of the double blink — the wake itself, plus the pass that
        // followed it, each sending one. When the pass carries the SAME palette the wake just painted, the wake
        // already did this pass's work: bind the mode's zones and send no palette. A pass carrying a DIFFERENT
        // palette is a real restore (the firmware changed the profile under us) and keeps its one flash.
        var wakesTail = IsWakeTail(_lastResume, DateTime.UtcNow, flash, _flash);
        if (profileChanged && !wakesTail) _flash = flash;
        Paint(includeFlash: profileChanged && !wakesTail, rebind: mode);
        KickReapply();
    }

    /// <summary>Whether a refresh pass is the TAIL of a wake — i.e. it reports a profile change but carries the
    /// very palette <see cref="OnResume"/> already painted within <see cref="WakeTailSeconds"/>. Read as "the wake
    /// already did this pass's work", so the pass must not send the palette a second time (the reported resume
    /// double blink). Pure and static so the rule can be asserted directly without a desktop lifetime, which
    /// <c>LightingCoordinator</c> itself needs (the limit <c>ReconcileScheduleTests</c> records).
    ///
    /// A pass carrying a DIFFERENT palette is not a tail — the firmware really changed the profile, and that
    /// restore keeps its single flash. Nor is one outside the window: the wake's own work is done, and a later
    /// profile change is a new action with its own one flash.</summary>
    internal static bool IsWakeTail(DateTime lastResume, DateTime now, AccentColor? passFlash, AccentColor? cachedFlash)
        => (now - lastResume).TotalSeconds < WakeTailSeconds && Equals(passFlash, cachedFlash);

    /// <summary>Startup / language-rebuild paint: seed the cached flash colour (read by the caller off the UI
    /// thread), paint it ONCE, then re-apply the per-zone colours for a few seconds. Startup is exactly the
    /// boot-with-external-display case where the first apply is most likely to land corrupted on the contended
    /// HID-over-I2C bus, so the burst gives the initial lighting several chances to settle correctly.
    ///
    /// IT TAKES NO LIGHTING: the sections are built with the current mode's values only moments before this runs
    /// (<c>BuildUi</c> hands them over, then <c>Attach</c>, then this), so there is nothing to rebind and the
    /// paint is the panels' own values.</summary>
    public void ApplyFollowLighting(AccentColor? flash)
    {
        _flash = flash;
        Paint();
        KickReapply();
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
    // apply per Paint either way — a rebind already applies, so the two must not both run). It is supplied by
    // both switch-instant paths — the announced landing (ApplyProfileApplied) and the refresh pass
    // (OnStateChanged) — because in both the section is bound to a mode that is no longer the live one.
    private void Paint(bool includeFlash = true, ILightZoneMode? rebind = null)
    {
        if (BacklightHidden) { BlankBacklight(); return; }   // lid shut in clamshell mode -> keep it dark

        if (includeFlash && _lighting is { ShowFollowsProfile: true, FollowsProfile: true } && _flash is { } flash)
        {
            // ARM THE SPURIOUS-ZERO SUSPICION BEFORE THE FLASH LANDS. The OPMODE write below zeroes the EC's
            // keyboard-brightness register while the keyboard stays lit (docs/lighting-an18-61.md), and that
            // register now lies for a short window — so a 0 an Fn-key read reports in it is the flash, not the
            // user. The panels refuse a 0 only while this is fresh (LightViewModel.NoteProfileFlash), which is
            // what lets a genuine Fn dim to 0 land at any other time. It must be armed in the same instant as the
            // write, and the section drives the same panels that will consume the read.
            _lighting.NoteProfileFlash();
            _svc.Device.Lighting?.SetProfileFlash(flash);
        }
        if (rebind is { } mode) _vm.ReloadLighting(mode);
        else _vm.RepaintLighting();
    }

    // Wake from sleep/hibernation: re-establish the RGB the firmware dropped over the suspend — ONE palette
    // send. Windows raises PowerModeChanged(Resume) ~twice per wake (PBT_APMRESUMEAUTOMATIC +
    // PBT_APMRESUMESUSPEND); coalesce within a few seconds so the OS's own double event doesn't paint twice.
    // (The refresh pass that follows the wake is coalesced separately — see OnStateChanged's wake tail.)
    //
    // A WAKE IS ALSO THE MOMENT THE POWER SOURCE MAY HAVE CHANGED, and this handler must let THAT win. Sleep
    // suspends the app's polling, so the power source can move over the sleep (the machine is unplugged, or
    // plugged in, while it sleeps); the refresh pass would only discover it up to a poll later. If we painted
    // the OLD cached palette here, the source restore would then land on its own port write and flash the NEW
    // palette — two flashes, one per profile, which is the owner's report («подсветка моргает дважды ... один
    // раз для старого профиля, другой — для нового»). So the source is re-synced FIRST, through the same
    // Application use cases the refresh pass uses (SyncPowerSource -> ApplyStoredMode): when it changed and the
    // restore announces a landing, OnProfileApplied has already painted the NEW palette and recorded the
    // _pendingId claim, so the wake's own paint carries only the per-zone colours and sends no palette of its
    // own — ONE palette send for the whole wake. When the source did NOT change the restore writes nothing,
    // nothing is announced, and the wake paints its single palette exactly as before.
    //
    // THE RE-SYNC RUNS OFF THE UI THREAD, because ApplyStoredMode may write the EC and the bus can stall right
    // after wake (the same rule the refresh pass follows, and the same reason ApplyModeCpuPower below is
    // deferred). The paint is posted back after it, so the UI-thread ordering this class requires is kept; the
    // restore's announcement is posted from inside the sync, ahead of this paint, which is what makes the
    // _pendingId check below see it. `_lastResume` is stamped BEFORE either path, so the pass that follows
    // still reads as a tail.
    private void OnResume()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastResume).TotalSeconds < 3) return;
        _lastResume = now;   // stamped BEFORE the restore/paint, so a pass that lands during it still reads as a tail

        // Re-sync the power source over the sleep through the SAME use cases the refresh pass drives. The battery
        // read is the cheap OS gauge (GetSystemPowerStatus — no EC transaction); the sync restores the now-live
        // source's remembered mode, whose landing is announced (and thus painted) by the switch use case. An
        // unchanged source is a no-op that announces nothing. The catch is this path's own guarantee, like the
        // deferred re-apply below: the device can be tearing down (an exit racing the wake), and the next pass
        // re-syncs anyway.
        _ = Task.Run(() =>
        {
            try { _syncSource.Run(_readBattery.Run()); } catch { /* the next pass re-syncs */ }
            // Paint ONCE, on the UI thread. If the sync announced a landing it set _pendingId and already sent
            // the profile's palette (OnProfileApplied), so this paint re-establishes only the per-zone colours
            // that sleep clobbered — sending the cached palette here would be the "old profile" flash over the
            // new one the restore just painted. With no landing, _pendingId is null and this is the wake's
            // single palette send.
            //
            // THE CLAIM MUST BE ONE THIS WAKE MADE, not a stale one: a switch in the very last moments before
            // sleep can leave _pendingId set (the poll that would clear it was suspended), and treating that as
            // "the restore painted for us" would drop the wake's palette send entirely when the source did NOT
            // change. The claim's stamp is compared against this wake's, so only an announcement made after
            // _lastResume (i.e. by the restore just above) suppresses the paint.
            Dispatcher.UIThread.Post(() =>
            {
                var restored = _pendingId != null && _pendingSince >= _lastResume;
                Paint(includeFlash: !restored);
            });
        });

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
        _reapply.Run(ReapplyTrigger.Resume);
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
