using System.Runtime.CompilerServices;
using AcerHelper.Domain;
using AcerHelper.UI;

namespace AcerHelper.Tests;

/// <summary>
/// ONE ACTION, ONE PALETTE FLASH. The owner's report — the keyboard blinks its colour TWICE on resume and on
/// switching the lightbar mode — was the same defect in two places, both in <c>LightingCoordinator</c>:
///
/// <list type="number">
/// <item><b>Resume.</b> <c>OnResume</c> paints the palette once at the wake instant, and the very refresh pass
/// that follows the wake re-discovers the same profile and paints it again — a second flash ~1 s later. The
/// guard is <c>IsWakeTail</c>: a pass carrying the SAME palette the wake just painted is the tail of the wake,
/// not a second action, and sends no palette.</item>
/// <item><b>Lightbar mode switch.</b> The flip used to paint the palette and then <c>KickReapply(withFlash:
/// true)</c> re-sent it on the burst's first ticks, 400 ms after the first — the same second blink. The burst is
/// now palette-free by construction: every path sends the GLOBAL palette ONCE, at the action instant, and the
/// burst only re-applies the per-zone colours (which is silent when already correct).</item>
/// </list>
///
/// THE ONE THING THE BURST DOES RE-SEND IS THE INDICATOR'S STEADY COLOUR, on a different channel:
/// <c>SetFollowColor</c> paints only the follows-profile lightbar (A4 65), never the keyboard, so repeating it is
/// silent. It has to be repeated, because the global palette is a one-shot write the firmware may overwrite with
/// its amber default and the lightbar has no per-zone panel to self-heal it (the owner's post-sleep report: Eco
/// mode, amber lightbar). That is NOT a second palette flash and must not be folded into the guarded block.
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
/// reddens <see cref="TheBurstReAppliesTheZonesAndNeverReSendsThePalette"/>.
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

    /// <summary>THE BURST MUST RE-ASSERT THE FOLLOWS-PROFILE INDICATOR, and on its own channel. The owner's
    /// report was the post-sleep state the guard above cannot see: the mode was Eco but the lightbar showed
    /// amber (Balanced's colour) — the firmware's default after it repainted its operating-mode slot, with no
    /// per-zone panel to self-heal because the lightbar follows the profile. The fix is an indicator-only write
    /// (<c>SetFollowColor</c>) on every pass, INSIDE the follows-profile branch but OUTSIDE the
    /// <c>includeFlash</c> guard — so a palette-free tick still corrects the lightbar, while the global flash is
    /// still sent only once.
    ///
    /// The indentation is the assertion: moving the call into the <c>includeFlash</c> block (16 spaces) would
    /// make the burst stop correcting it again, which is the regression this pins. MUTATION THAT REDDENS IT:
    /// deleting the call, or moving it one level deeper.</summary>
    [Fact]
    public void TheBurstReAssertsTheFollowIndicator_WithoutTheGlobalFlash()
    {
        var source = Source("UI/LightingCoordinator.cs");

        Assert.Contains("\n            _svc.Device.Lighting?.SetFollowColor(flash);\n",
                        source.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    /// <summary>THE WAKE MUST KICK THE BURST, not paint once and stop. The OS reports resume before the EC has
    /// necessarily finished re-initialising its operating-mode slot, so the firmware can repaint the slot amber
    /// AFTER the wake's single palette write; only a pass that follows can correct it. The wake's paint is posted
    /// asynchronously (after the power-source re-sync), so the burst must be kicked by that same post — a refresh
    /// pass only paints when the profile or mode changed, which is NOT the case for a wake that changed neither
    /// (the owner's Eco-over-sleep case).</summary>
    [Fact]
    public void TheWakeKicksThePaletteFreeBurst_SoALateRepaintIsCorrected()
    {
        var source = Source("UI/LightingCoordinator.cs").Replace("\r\n", "\n");

        var paint = source.IndexOf("Paint(includeFlash: !restored);", StringComparison.Ordinal);
        Assert.True(paint >= 0, "the wake still paints once, with the palette unless the restore already did");
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

    /// <summary>THE BURST IS BOUNDED, SO IT CANNOT BE THE ONLY CORRECTION. A wake that misses the resume event (a
    /// Modern-Standby machine whose profile did not change) never starts the burst at all, and a firmware repaint
    /// that lands after the ~3 s window is likewise missed; either way the Eco lightbar stays amber until the next
    /// profile switch. The long-lived keeper closes that hole on the indicator-only channel: it re-asserts the
    /// steady colour on a slow schedule, with the same follows-profile + clamshell guards as <c>Paint</c>, and
    /// never sends the global palette or repaints the keyboard. A source guard for the same reason as the burst
    /// tests above — the coordinator needs a desktop lifetime and a live refresh loop to drive.
    ///
    /// MUTATION THAT REDDENS IT: removing the schedule/start/dispose, dropping the <c>BacklightHidden</c> or
    /// follows-profile gate (the keeper would relight a hidden bar or override a user-owned lightbar), or adding
    /// <c>SetProfileFlash</c>/a zone repaint to the tick (the palette is the global write that blinks).</summary>
    [Fact]
    public void TheLongLivedKeeperReAssertsTheIndicator_WithoutThePalette()
    {
        var source = Source("UI/LightingCoordinator.cs").Replace("\r\n", "\n");

        Assert.Contains("_followKeeper = new PeriodicSchedule(FollowKeeperTick, FollowKeeperInterval, UiSchedule.Normal);",
                        source, StringComparison.Ordinal);
        Assert.Contains("_followKeeper.Start();", source, StringComparison.Ordinal);
        Assert.Contains("_followKeeper.Dispose();", source, StringComparison.Ordinal);

        var tick = source.IndexOf("private void FollowKeeperTick()", StringComparison.Ordinal);
        Assert.True(tick >= 0, "the keeper's tick must exist");
        var end = source.IndexOf("\n    }", tick, StringComparison.Ordinal);
        Assert.True(end > tick, "the keeper's tick must have a body");
        var body = source[tick..end];

        Assert.Contains("if (BacklightHidden) return;", body, StringComparison.Ordinal);
        Assert.Contains("_lighting is { ShowFollowsProfile: true, FollowsProfile: true }", body, StringComparison.Ordinal);
        Assert.Contains("SetFollowColor(flash)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetProfileFlash", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RepaintLighting", body, StringComparison.Ordinal);
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
