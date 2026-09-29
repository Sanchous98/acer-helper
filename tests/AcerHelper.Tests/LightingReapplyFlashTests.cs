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
/// now palette-free by construction: every path sends the palette ONCE, at the action instant, and the burst
/// only re-applies the per-zone colours (which is silent when already correct).</item>
/// </list>
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
