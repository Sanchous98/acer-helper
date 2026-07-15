using Avalonia.Threading;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.UI;

/// <summary>Owns the lighting re-apply state machine that used to live inline in <see cref="AppController"/>:
/// the post-switch re-apply timer, the sleep/resume re-paint, and the clamshell lid blank/restore. Built with
/// the <see cref="LaptopService"/> BEFORE any UI exists (so the follows-profile toggle can reach it), then
/// pointed at the current view-models via <see cref="Attach"/> after each <c>BuildUi</c> (startup + live
/// language rebuild). AppController drives it from its refresh loop (<see cref="OnProfileChanged"/> /
/// <see cref="OnModeChanged"/>) and forwards the startup/rebuild repaint (<see cref="ApplyFollowLighting"/>)
/// and the follows-profile flip (<see cref="OnFollowsProfileFlipped"/>).</summary>
internal sealed class LightingCoordinator : IDisposable
{
    private readonly LaptopService _svc;
    private readonly DispatcherTimer _lightReapply;   // re-applies lighting for a while after a profile switch
    private int _lightReapplyLeft;                     // remaining re-apply ticks
    private readonly ResumeWatcher _resume;            // re-applies lighting on wake (firmware drops it over sleep)
    private readonly LidWatcher _lid;                  // blanks/restores the RGB as the lid shuts/opens in clamshell mode
    private bool _lidShut;           // last lid state from the LidWatcher (Windows); true = shut. Drives blanking.
    private bool _blankedByLid;      // WE blanked the backlight under a shut lid; restore on the next open (see OnLidChanged)

    // The current (rebuildable) view-models. Reassigned by Attach after each BuildUi; a re-apply/blank always
    // drives the live pair. Non-null before any callback can run (Attach is called synchronously right after
    // BuildUi, and every watcher callback is posted to the UI thread — so it can't run mid-construction).
    private MainViewModel _vm = null!;
    private LightingViewModel? _lighting;

    public LightingCoordinator(LaptopService svc)
    {
        _svc = svc;

        // Acer firmware repaints the lit zones with the profile's palette colour a moment AFTER our WMI profile
        // set. A single re-apply can land too early (before that repaint), so we re-apply the mode's lighting a
        // couple of times right after the switch — whichever tick lands after the repaint overrides it and it
        // then stays. (Only user-driven zones are re-applied; a "follows profile" lightbar has no panel and is
        // left as the firmware's palette. The palette flash itself is firmware and can't be suppressed.)
        _lightReapply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _lightReapply.Tick += (_, _) =>
        {
            ApplyFollowLighting();
            if (--_lightReapplyLeft <= 0) _lightReapply.Stop();
        };

        // Sleep/hibernate clears the EC's RGB state; re-apply the current mode's lighting on wake.
        _resume = new ResumeWatcher(() => Dispatcher.UIThread.Post(ReapplyLighting));
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
    /// switch. (Persisting the flag itself stays in AppController, which owns the vendor key.)</summary>
    public void OnFollowsProfileFlipped()
    {
        _lightReapplyLeft = 2;
        _lightReapply.Stop(); _lightReapply.Start();
    }

    /// <summary>A HARDWARE profile change (incl. base&lt;-&gt;Turbo, which shares its base's preset KEY). Repaint
    /// the lighting IMMEDIATELY — a following lightbar must show the NEW profile's palette at once, otherwise the
    /// previous colour lingers for a beat and looks like a stray flash of the old colour (NitroSense flips it the
    /// instant the profile changes). Then repeat a couple of times to catch any late firmware repaint.</summary>
    public void OnProfileChanged()
    {
        ApplyFollowLighting();
        _lightReapplyLeft = 2;
        _lightReapply.Stop(); _lightReapply.Start();
    }

    /// <summary>The performance MODE changed (its preset key): reflect the mode's saved lighting, or keep the
    /// backlight dark if it's hidden under a shut lid (don't light a hidden keyboard on a mode change).</summary>
    public void OnModeChanged()
    {
        if (BacklightHidden) BlankBacklight();
        else _vm.ReloadLighting(_svc.LightsForCurrentMode());
    }

    /// <summary>Repaint after a profile change/toggle. First paint the new profile's palette on a follow-lightbar
    /// (a GLOBAL write that also flashes the keyboard), then re-apply the per-zone colours so the keyboard settles
    /// back to its own custom colour on top. Called immediately on a switch (so the lightbar flips at once, no
    /// lingering old colour) and repeated by _lightReapply as a safety net against a late firmware repaint. Also
    /// the startup/rebuild paint (AppController calls it once the view-models are attached).</summary>
    public void ApplyFollowLighting()
    {
        if (BacklightHidden) { BlankBacklight(); return; }   // lid shut in clamshell mode -> keep it dark
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

    // Lid opened/closed: shut while clamshell keep-awake is enabled -> blank the (now hidden) backlight without
    // touching the app's stored per-zone state; opened -> restore the current mode's lighting. The blank is
    // gated on clamshell (a lid-close otherwise sleeps the machine and resume re-applies), but the RESTORE is
    // gated on _blankedByLid — the remembered fact that we blanked — NOT on clamshell still being enabled at
    // open time: the user can flip clamshell off from the external screen while the lid is shut, and the
    // EC-latched blank would otherwise stick until the next profile switch / resume / restart.
    // Posted to the UI thread by the lid watcher, so all HID writes stay serialized with the rest of the app.
    private void OnLidChanged(bool open)
    {
        _lidShut = !open;   // remembered so a repaint (profile/mode/power change) under a shut lid re-blanks (see BacklightHidden)
        if (open)
        {
            if (_blankedByLid) { _blankedByLid = false; ReapplyLighting(); }
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
