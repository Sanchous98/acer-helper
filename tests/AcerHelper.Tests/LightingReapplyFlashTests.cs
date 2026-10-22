using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.Infrastructure;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.UI;

namespace AcerHelper.Tests;

/// <summary>
/// ONE ACTION, ONE PALETTE FLASH. The owner's report — the keyboard blinks its colour TWICE on resume and on
/// switching the lightbar mode — was the same defect in two places, both in <c>LightingCoordinator</c>:
///
/// <list type="number">
/// <item><b>Resume.</b> The wake sends the palette once, and the very refresh pass that follows the wake
/// re-discovers the same profile and paints it again — a second flash ~1 s later. The guard is
/// <c>IsWakeTail</c>: a pass carrying the SAME palette the wake just painted is the tail of the wake, not a
/// second action, and sends no palette.</item>
/// <item><b>Lightbar mode switch.</b> The flip used to paint the palette and then <c>KickReapply(withFlash:
/// true)</c> re-sent it on the burst's first ticks, 400 ms after the first — the same second blink. The burst is
/// now palette-free by construction: every path sends the GLOBAL palette ONCE, at the action instant, and the
/// burst only re-applies the per-zone colours (which is silent when already correct).</item>
/// </list>
///
/// THE FOLLOWS-PROFILE LIGHTBAR IS NOT WRITTEN AT ALL. It used to be re-asserted on a second channel
/// (<c>SetFollowColor</c>, an <c>A4 65</c> zone write) on every pass, and a long-lived keeper repeated it every
/// five seconds. That was wrong twice over: a zone the firmware paints per profile is the firmware's to keep
/// (<c>RgbZone.CanFollowProfile</c>), and the zone-write path is a DIFFERENT firmware path — it renders a
/// slightly different shade of the same RGB (the owner saw the lightbar change colour right after a profile
/// switch) and it disturbs the EC's brightness state (the owner's brightness regression). The lightbar's colour
/// is therefore the operating-mode palette and nothing else, and the zone has no write channel in the port at
/// all. What replaces the keeper is an EVENT, not a loop: a wake is detected (the OS's resume event, or the gap
/// the app's own poll sees) and the palette is sent once, after the EC has settled.
///
/// WHY BOTH SHAPES. <c>IsWakeTail</c> is asserted behaviourally — it is a pure function, so the rule that
/// decides the resume case is checked directly rather than by reading the source. The palette-free burst is
/// pinned by a SOURCE guard, because the coordinator needs a desktop lifetime and a live refresh loop to drive
/// (the limit <c>ReconcileScheduleTests</c> and <c>HardwareReconcilerTests</c> already record), so nothing in
/// this suite can observe a burst tick. Re-sending the palette on a tick requires exactly one of two named
/// mechanisms — a <c>withFlash</c> parameter or a <c>FlashTicks</c> count — and the guard forbids both, so the
/// regression cannot come back without reddening this file.
///
/// MUTATION-VERIFIED. Reverting <c>IsWakeTail</c> to <c>false</c> reddens the two positive rows; making it
/// ignore the palette (always true inside the window) reddens the different-palette row; deleting the <c>Paint(
/// includeFlash: false)</c> from <c>ReapplyTick</c> — or reintroducing <c>withFlash</c>/<c>FlashTicks</c> —
/// reddens <see cref="TheBurstReAppliesTheZonesAndNeverReSendsThePalette"/>; restoring the zone write or the
/// keeper reddens <see cref="TheFollowsProfileZoneIsNeverWritten"/>.
/// </summary>
public class LightingReapplyFlashTests
{
    private static readonly AccentColor Amber = new(199, 174, 0);
    private static readonly AccentColor Red   = new(199, 9, 46);

    // ---------------------------------------------------------------- the resume tail (behavioural)

    /// <summary>The reported resume double blink, at the rule that causes it: the wake paints the palette and
    /// stamps the time, and a refresh pass moments later carrying the same palette is its tail — the pass must
    /// not send the palette again.</summary>
    [Fact]
    public void APassCarryingTheWakePalette_IsTheWakesTail()
    {
        var woke = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(LightingCoordinator.IsWakeTail(woke, woke.AddSeconds(1), Amber, Amber));
    }

    /// <summary>The differential control: a pass carrying a DIFFERENT palette inside the same window is not the
    /// tail. That is a profile the firmware really moved under us (another tool, the firmware Turbo key) — a
    /// genuine restore, which keeps its own single flash rather than being swallowed by the wake's.</summary>
    [Fact]
    public void APassCarryingADifferentPalette_IsNotTheWakesTail()
    {
        var woke = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LightingCoordinator.IsWakeTail(woke, woke.AddSeconds(1), Red, Amber));
    }

    /// <summary>Outside the window the wake's work is done and a later profile change is a new action with its
    /// own one flash — the window is what makes "the tail" a fact about the wake rather than about all time.</summary>
    [Fact]
    public void APassAfterTheWindow_IsNotTheWakesTail()
    {
        var woke = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(LightingCoordinator.IsWakeTail(woke, woke.AddSeconds(30), Amber, Amber));
    }

    // ---------------------------------------------------------------- the palette-free burst (source guard)

    /// <summary>The re-apply burst re-asserts the per-zone colours and NEVER the palette. The palette is the
    /// GLOBAL write that blinks; the per-zone paint is silent when already correct, which is exactly why the
    /// safety net keeps one and drops the other. Both halves are pinned so the guard cannot pass on a
    /// half-removed mechanism: the tick paints with the palette off, and no <c>withFlash</c> parameter or
    /// <c>FlashTicks</c> count exists for a tick to consult.</summary>
    [Fact]
    public void TheBurstReAppliesTheZonesAndNeverReSendsThePalette()
    {
        var source = Source("UI/LightingCoordinator.cs");

        Assert.Contains("Paint(includeFlash: false);", source, StringComparison.Ordinal);   // the tick's shape
        Assert.DoesNotContain("withFlash", source, StringComparison.Ordinal);               // no per-burst flash switch
        Assert.DoesNotContain("FlashTicks", source, StringComparison.Ordinal);              // no per-burst flash count
    }

    /// <summary>THE FOLLOWS-PROFILE ZONE IS NEVER WRITTEN. The lightbar that follows the performance profile is
    /// painted by the firmware's own operating-mode slot, and the app's only channel for it is the global palette
    /// (<c>SetProfileFlash</c>). A direct zone write is a DIFFERENT firmware path: it renders a slightly different
    /// shade of the same RGB — the owner saw the lightbar change colour the moment a profile was switched — and it
    /// disturbs the EC's brightness state, which is the brightness regression the app shipped in 0.39.4.
    ///
    /// So the zone is left to the firmware, the port has no member that could write it, and NOTHING in the app
    /// writes it in a loop either: the 0.39.4 keeper (a five-second timer re-asserting the zone) is gone, because
    /// a timer that keeps writing a zone the firmware owns is the same mistake in a loop.
    ///
    /// The guard reads the sources, because "no member" and "no call" are absences: the port is checked by
    /// reflection (there is no <c>SetFollowColor</c> to call) and the tree by source (there is no call and no
    /// keeper). MUTATION THAT REDDENS IT: restoring the port member, a call to it, or the keeper's schedule.</summary>
    [Fact]
    public void TheFollowsProfileZoneIsNeverWritten()
    {
        Assert.DoesNotContain("SetFollowColor", typeof(IRgbDevice).GetMembers().Select(m => m.Name));
        Assert.DoesNotContain("SetFollowColor", typeof(IRgbController).GetMembers().Select(m => m.Name));

        var coordinator = Source("UI/LightingCoordinator.cs");
        Assert.DoesNotContain("SetFollowColor", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("_followKeeper", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("FollowKeeperTick", coordinator, StringComparison.Ordinal);

        // The ENE transport must not carry an A4 65 steady-colour write either: the zone is addressed by the
        // operating-mode slot only, and the panel's own apply op is the follows-profile-OFF path.
        Assert.DoesNotContain("SetFollowColor", Source("Infrastructure/Vendors/Acer/EneHidController.cs"),
                              StringComparison.Ordinal);
    }

    /// <summary>THE WAKE IS AN EVENT, AND IT SENDS THE PALETTE ONCE. The owner's recurrence: after a sleep the
    /// mode was Eco but the lightbar was amber, and only the next profile switch fixed it — because the wake was
    /// either not reported (a Modern-Standby session does not always deliver the OS's resume event) or was
    /// reported before the EC had finished re-initialising its operating-mode slot, so the palette landed in a
    /// window the firmware then repainted.
    ///
    /// So the wake is detected by the app itself as well (the gap in its own poll — see
    /// <see cref="AcerHelper.Infrastructure.PollSchedule"/>), and the palette goes out ONCE, after the EC has
    /// settled, not at the wake instant. The zones (the keyboard's colours and its SAVED brightness) come back at
    /// once instead: a wake that left the keyboard's brightness wrong is the owner's other regression.
    ///
    /// MUTATION THAT REDDENS IT: dropping the delayed palette (the lightbar would stay amber again), sending the
    /// palette at the wake instant instead (the firmware would repaint the slot), or dropping the zone paint (the
    /// keyboard's saved brightness would not come back).</summary>
    [Fact]
    public void TheWakeSendsTheZonesAtOnceAndThePaletteOnce_AfterTheEcHasSettled()
    {
        var source = Source("UI/LightingCoordinator.cs").Replace("\r\n", "\n");

        Assert.Contains("public void OnWake()", source, StringComparison.Ordinal);
        Assert.Contains("WakePaletteDelay", source, StringComparison.Ordinal);
        Assert.Contains("_wakePalette.Restart();", source, StringComparison.Ordinal);
        Assert.Contains("private void WakePaletteTick()", source, StringComparison.Ordinal);
        // the tick stops the schedule: ONE palette per wake, not a series
        Assert.Contains("_wakePalette.Stop();", source, StringComparison.Ordinal);

        var wake = source.IndexOf("private void Wake()", StringComparison.Ordinal);
        Assert.True(wake >= 0, "the wake must have a body");
        var end = source.IndexOf("\n    }", wake, StringComparison.Ordinal);
        var body = source[wake..end];
        // the zones come back at once, and the palette is NOT sent at the wake instant
        Assert.Contains("Paint(includeFlash: false);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Paint(includeFlash: true);", body, StringComparison.Ordinal);
        Assert.Contains("KickReapply();", body, StringComparison.Ordinal);
    }

    /// <summary>THE APP DETECTS THE WAKE ITSELF, because the OS's event is not always delivered. The full pass
    /// runs every three seconds on a pool timer, and a timer does not fire while the machine is asleep — so a tick
    /// that arrives far later than the period IS the evidence of a wake. The poll is the app's own clock, and the
    /// lighting is re-applied from it.
    ///
    /// THE THRESHOLD LIVES WITH THE WAKE, not with the cadence: the poll reports how late it came (a fact about
    /// a schedule) and the wake decides what a late tick means. The rule itself is pure and asserted
    /// behaviourally; the wiring (the gap reaches the coordinator's wake) is a source guard, because the wiring
    /// is what the app does and nothing else can observe it.</summary>
    [Fact]
    public void ThePollsGapIsTheAppsOwnEvidenceOfAWake()
    {
        Assert.True(LightingCoordinator.IsWakeGap(LightingCoordinator.WakeGap));
        Assert.True(LightingCoordinator.IsWakeGap(LightingCoordinator.WakeGap + TimeSpan.FromMinutes(5)));
        Assert.False(LightingCoordinator.IsWakeGap(LightingCoordinator.WakeGap - TimeSpan.FromSeconds(1)));
        Assert.False(LightingCoordinator.IsWakeGap(TimeSpan.Zero));

        var controller = Source("UI/AppController.cs");
        Assert.Contains("onGap: _lightingCoord.OnPollGap", controller, StringComparison.Ordinal);
    }

    /// <summary>THE WAKE MUST KICK THE BURST, not paint once and stop. The wake re-applies the mode's zones, and
    /// the per-zone self-heal follows it: the firmware's own repaint can land after the wake's write, and a burst
    /// that re-asserts the zones a few times is what covers that. The burst carries NO palette (the palette is the
    /// wake's own single send), so the two never add up to a second flash.</summary>
    [Fact]
    public void TheWakeKicksThePaletteFreeBurst_SoALateRepaintIsCorrected()
    {
        var source = Source("UI/LightingCoordinator.cs").Replace("\r\n", "\n");

        var paint = source.IndexOf("Paint(includeFlash: false);", source.IndexOf("private void Wake()", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(paint >= 0, "the wake re-applies the zones");
        var kick = source.IndexOf("KickReapply();", paint, StringComparison.Ordinal);
        Assert.True(kick > paint, "the wake's paint must be followed by the self-heal burst");
    }

    /// <summary>Each action still sends its palette ONCE, at the action instant, before the palette-free burst
    /// backs it: the restore/flip paints, then kicks. A refactor that dropped the <c>Paint()</c> and left the
    /// palette to the burst would make these actions stop flashing at all — the opposite failure — so the send
    /// sites are named here as well as the burst's silence.</summary>
    [Fact]
    public void TheRestoreAndFlipPathsEachPaintThePaletteOnce()
    {
        var source = Source("UI/LightingCoordinator.cs");

        Assert.Contains("public void OnFollowsProfileFlipped()", source, StringComparison.Ordinal);
        Assert.Contains("public void ApplyFollowLighting(AccentColor? flash)", source, StringComparison.Ordinal);
        // Both restore paths paint directly; the palette send itself lives in Paint.
        Assert.Contains("_svc.Device.Lighting?.SetProfileFlash(flash);", source, StringComparison.Ordinal);
    }

    /// <summary>THE FLASH ARMS THE SPURIOUS-ZERO SUSPICION, and it must arm it BEFORE the palette write lands.
    /// The OPMODE write zeroes the EC's keyboard-brightness register while the keyboard stays lit; the panels
    /// refuse a read-back 0 only while that suspicion is fresh (<c>LightViewModel.NoteProfileFlash</c>), which is
    /// what lets a genuine Fn dim to 0 land the rest of the time. The ordering is load-bearing: arming after the
    /// write would leave a read that lands in between believing the lie; arming never (the pre-fix static
    /// predicate removed without this) would refuse nothing and reintroduce the documented spurious-0 bug.
    ///
    /// A source guard because the coordinator needs a desktop lifetime and a refresh loop to drive, the same limit
    /// the rest of this file records. MUTATION THAT REDDENS IT: dropping the <c>_lighting.NoteProfileFlash()</c>
    /// call, or moving it after <c>SetProfileFlash</c>.</summary>
    [Fact]
    public void TheFlashArmsTheSpuriousZeroSuspicion_BeforeThePaletteLands()
    {
        var source = Source("UI/LightingCoordinator.cs");

        var arm = source.IndexOf("_lighting.NoteProfileFlash();", StringComparison.Ordinal);
        var send = source.IndexOf("_svc.Device.Lighting?.SetProfileFlash(flash);", StringComparison.Ordinal);
        Assert.True(arm >= 0, "Paint must arm the flash suspicion at the flash instant");
        Assert.True(send >= 0, "Paint must still send the palette");
        Assert.True(arm < send, "the suspicion must be armed BEFORE the palette write, so an in-between read is covered");
    }

    /// <summary>THE SUPPRESSION CLAIM IS STILL MADE BY <c>OnProfileApplied</c>, and it is still the use case's
    /// announcement (not a separate step a caller can skip). The 2–3× sweep flash came from a switch that wrote
    /// the port without ever reaching this method, so two things must hold together: the coordinator implements
    /// <c>IProfileAnnouncer</c>, and the claim (<c>_pendingId</c>, which suppresses the stale refresh pass that
    /// would repaint the palette) is set inside it. A source guard, because the coordinator needs a desktop
    /// lifetime and a live refresh loop to drive — the same limit the class remarks record.
    ///
    /// MUTATION THAT REDDENS IT: dropping <c>_pendingId = applied.Id</c> from the announced path, or unhooking the
    /// interface.</summary>
    [Fact]
    public void TheAnnouncedProfileStillRecordsTheSuppressionClaim()
    {
        var source = Source("UI/LightingCoordinator.cs");

        Assert.Contains(": IDisposable, IProfileAnnouncer", source, StringComparison.Ordinal);
        Assert.Contains("public void OnProfileApplied(PerformanceProfile applied)", source, StringComparison.Ordinal);
        Assert.Contains("_pendingId = applied.Id;", source, StringComparison.Ordinal);
    }

    /// <summary>NO PERPETUAL WRITER. The 0.39.4 keeper was a five-second timer in the UI that re-asserted the
    /// follows-profile lightbar for the life of the process. It was a symptom patch in the wrong place: a timer
    /// that keeps writing a zone the firmware owns, from the layer that owns no hardware schedule, and it brought
    /// two regressions with it (the shade the owner saw change right after a profile switch, and the EC's
    /// brightness state drifting away from the saved value). A wake is an EVENT, and the event is handled once.
    ///
    /// MUTATION THAT REDDENS IT: any <c>PeriodicSchedule</c> in the coordinator that is not the bounded re-apply
    /// burst or the wake's one-shot palette.</summary>
    [Fact]
    public void TheCoordinatorHasNoPerpetualHardwareWriter()
    {
        var source = Source("UI/LightingCoordinator.cs");

        var schedules = System.Text.RegularExpressions.Regex.Matches(source, "new PeriodicSchedule\\(")
                          .Select(m => m.Value).ToList();
        // exactly two: the bounded re-apply burst and the wake's one-shot palette
        Assert.Equal(2, schedules.Count);
        Assert.Contains("_lightReapply = new PeriodicSchedule(", source, StringComparison.Ordinal);
        Assert.Contains("_wakePalette = new PeriodicSchedule(", source, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The repository root, from the COMPILER's path: the test host's working directory is its output
    /// folder, where a relative path finds nothing (the same helper the other source guards use).</summary>
    private static string Root([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Source(string relative)
    {
        var path = Path.Combine(Root(), relative);
        Assert.True(File.Exists(path), $"expected a source file at {relative} — a guard that reads nothing passes for the wrong reason");
        return File.ReadAllText(path);
    }
}
