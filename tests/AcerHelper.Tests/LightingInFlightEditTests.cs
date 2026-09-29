using System.Reflection;
using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Tests.Fakes;
using AcerHelper.UI.ViewModels;

namespace AcerHelper.Tests;

/// <summary>
/// THE OWNER'S REPORT, reproduced at the section level: «настройки яркости не пишутся» — moving the
/// keyboard-zone brightness slider did not persist to settings.json, while the profile-switch memory DID. A
/// live watch of <c>%APPDATA%\AcerHelper\settings.json</c> showed only the source slot changing, never a
/// <c>LightPresets</c> brightness (the log is in the task that produced this file: <c>OnAc=1/False k1=100</c>
/// after moving the Balanced slider to 0).
///
/// THE MECHANISM, and it is a RACE the burst wins. <c>LightViewModel.OnBrightnessChanged</c> calls
/// <c>Schedule()</c>, which only <b>arms a 120 ms debounce</b> — the edit is applied and saved on the tick
/// (<c>ApplyDebounced → ApplyNow + SaveState</c>). The post-switch re-apply burst
/// (<c>LightingCoordinator.ReapplyTick</c>) runs every 400 ms for ~3 s after every profile switch, and the
/// owner was switching profiles right before editing, so a burst tick lands inside the 120 ms window. The old
/// <c>LightingViewModel.Repaint</c> called <c>LightViewModel.Rebind(_lights[panel.Title])</c> — the STALE
/// committed value — which overwrote the slider (the user's 0) and set <c>_loading = true</c> so the edit's own
/// <c>Schedule()</c> was suppressed. The pending tick then applied and PERSISTED the stale 100: the user's 0
/// was reverted before it was ever written.
///
/// <c>_lights[zone]</c> is only updated by <c>Store</c> when the debounce tick fires (or by a mode reload), so
/// a Repaint that lands during the debounce window rebinds from the last COMMITTED value, not the edit in
/// flight. That is why <c>Repaint</c>'s old comment ("reads nothing, so the burst is safe to run mid-edit") was
/// false: it does not READ the graph, but it REBINDS from the section's own copy, which is behind the user.
///
/// WHAT THE FIX IS. <c>LightViewModel.Repaint</c> is the SAME-MODE re-apply path: while a user edit is pending
/// it re-applies the panel's OWN current values (<c>ApplyNow</c>) and never touches the controls, so the edit
/// survives and the pending tick commits it. A GENUINE mode change is <c>LightingViewModel.Reload</c> →
/// <c>LightViewModel.Rebind</c>, which still ADOPTS the target mode's stored values (and stops the superseded
/// edit's debounce), because a mode change is a new intent. So "mode changed, adopt stored" versus "re-apply
/// the same mode, do not touch the pending edit" is distinguished by the CALL: Reload/Rebind is a mode change,
/// Repaint is a same-mode re-apply.
///
/// THE DIFFERENTIAL BELOW pins both halves, or the guard could be "never rebind on a burst" — which would lose
/// a genuine mode change the burst happens to coincide with.
/// </summary>
public class LightingInFlightEditTests
{
    private const string ZoneName = "Keyboard";

    /// <summary>
    /// THE REPRO. A committed brightness of 100, then the user drags to 0 (the debounce is now pending), then a
    /// burst tick lands inside the window. Before the fix the repaint rebinds the slider to 100, the pending tick
    /// applies and saves 100, and the 0 is gone — the owner's log. After the fix the edit survives and the tick
    /// commits it: slider 0, stored 0, and the device was told 0.
    ///
    /// MUTATIONS THAT REDDEN IT: restoring <c>Repaint</c>'s unconditional <c>Rebind</c> (slider back to 100,
    /// stored 100, last write 100); or moving the guard onto the wrong side (a Rebind that skips adopting when
    /// no edit is pending — caught by the differential below).
    /// </summary>
    [Fact]
    public void ABurstRepaintDuringAPendingBrightnessEdit_DoesNotRevertTheEdit()
    {
        var written = new List<byte>();
        var mode = Configured(100);
        var vm = Section(mode, written);
        var panel = Assert.Single(vm.Panels);

        // Control: the construction re-apply really sent the stored 100, so a later 0 is the user's edit.
        Assert.Equal(100, panel.Brightness);
        Assert.Equal([100], written);

        panel.Brightness = 0;   // the user drags to off: the 120 ms debounce is now pending

        vm.Repaint();           // the post-switch burst tick lands inside that debounce window

        // Before the fix this is 100: the repaint overwrote the user's control value with the last committed one.
        Assert.Equal(0, panel.Brightness);

        // The tick the UI produces (PeriodicSchedule.TriggerNow — the manual seam for a headless test).
        TriggerDebounce(panel);

        // The user's edit was applied and persisted; the stale 100 was never written over it.
        Assert.Equal(0, panel.Brightness);
        Assert.Equal(0, mode[ZoneName].Brightness);
        Assert.Equal(0, written[^1]);
        Assert.Equal(1, mode.Persists);   // the edit was saved exactly once, not clobbered and re-saved
    }

    /// <summary>
    /// THE DIFFERENTIAL, so the guard cannot be "a Repaint never rebinds": a GENUINE mode change
    /// (<c>LightingViewModel.Reload</c> over a different mode's door, which is what
    /// <c>LightingCoordinator.ApplyProfileApplied</c> / <c>OnStateChanged</c> hand in) must still ADOPT the
    /// target mode's stored values — even with an edit in flight — because a mode change is a new intent that
    /// supersedes the edit. Without this half the fix could silently stop a profile switch from applying the
    /// target's saved lighting (the bug <c>ProfileSwitchBrightnessTests</c> exists to pin).
    ///
    /// MUTATION THAT REDDENS IT: making <c>Rebind</c> re-apply the panel's current values (like the Repaint
    /// guard) instead of adopting the new state — the slider would stay 0 and the target's saved 42 never
    /// reach the device.
    /// </summary>
    [Fact]
    public void AGenuineModeChangeDuringAPendingEdit_AdoptsTheTargetModesStoredBrightness()
    {
        var written = new List<byte>();
        var modeA = Configured(100);
        var modeB = Configured(42);
        var vm = Section(modeA, written);
        var panel = Assert.Single(vm.Panels);

        panel.Brightness = 0;   // an edit is in flight when the mode changes
        vm.Reload(modeB);       // the mode change: OnStateChanged/announced landing rebinds to the target's door

        Assert.Equal(42, panel.Brightness);                       // the target's stored value is adopted
        Assert.Equal(42, modeB[ZoneName].Brightness);             // and it is what the target mode now holds
        Assert.Equal(42, written[^1]);                            // and it is what the device was told

        // A tick that still arrives (the manual seam bypasses the timer, but the debounce was stopped on the
        // adopt) re-applies the ADOPTED value, never the superseded edit.
        TriggerDebounce(panel);
        Assert.Equal(42, panel.Brightness);
        Assert.Equal(42, modeB[ZoneName].Brightness);
        Assert.Equal(42, written[^1]);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A mode whose one zone the user has configured at the given brightness — the shape a profile
    /// saved from the UI has, and the one whose stored value is authoritative.</summary>
    private static FakeLightZones Configured(int brightness)
        => FakeLightZones.Of(ZoneName, new LightZoneState(true, 0, brightness, 5, 1, 0xFF0000, []));

    /// <summary>The section over one static zone that records every brightness it is sent, built the way
    /// <c>AppController.BuildUi</c> builds it: no injected poster (nothing here reaches the event path), the
    /// real debounce and the real <c>Store</c> → <c>ApplyLightZone</c> path.</summary>
    private static LightingViewModel Section(FakeLightZones mode, List<byte> written)
    {
        var zone = new RgbZone(ZoneName, 1,
            [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: new object())],
            (_, brightness, _, _, _) => { written.Add(brightness); return true; });
        return new LightingViewModel(new RgbDevice(new FakeRgbController { Zones = [zone] }),
                                     mode, followsProfile: false, _ => { });
    }

    /// <summary>Produce the debounce tick the 120 ms UI schedule would. The real timer needs a dispatcher loop a
    /// headless test cannot pump; this is the manual seam <c>PeriodicSchedule.TriggerNow</c> exists for, the same
    /// one <c>LightingStoredBrightnessAuthorityTests</c> uses.</summary>
    private static void TriggerDebounce(LightViewModel panel)
    {
        var schedule = (PeriodicSchedule)typeof(LightViewModel)
            .GetField("_debounce", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        schedule.TriggerNow();
    }
}
