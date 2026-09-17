using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The Curve-Optimizer rules as a pure domain type (<see cref="CoAxis"/>), driven DIRECTLY — a rail list and a
/// preset in, the counts the SMU would receive out. No service, no fake port, no lock: these are the rules
/// <c>LaptopService.Tuning.cs</c> used to restate at every one of its sites.
///
/// WHY DIRECT DRIVING IS THE POINT AND NOT A SHORTCUT. The existing suite pins this axis through the service
/// (<c>LaptopServiceCoTests</c>), and every one of those assertions still passes if the rules are written out at
/// the site instead of asked of this type — the service's behaviour is the same either way. So a wave that only
/// moved the code and kept the old tests would prove nothing about the move. The tests below name the rules
/// themselves, and the two cross-checks at the end tie each answer back to what the port actually received, so
/// the object cannot quietly stop being the one the production path uses.
///
/// WHAT IS NOT CHECKED HERE. Whether a call is attempted at all — a null port, a count mismatch, a port that
/// throws, and the persist-before-look asymmetry between <c>SetCo</c> and <c>SetCoDomains</c>. That is the
/// caller's, is deliberately not unified (see <see cref="CoAxis"/>'s own docstring), and is pinned as shipped in
/// <c>LaptopServiceCoTests</c>.
/// </summary>
public class CoAxisTests
{
    private const int PortMin = -30;

    private static CoAxis Axis(params VoltageDomain[] rails) => new(rails, (PortMin, 0));

    private static VoltageDomain Rail(string label, string key, (int Min, int Max)? own = null)
        => own is { } range ? new VoltageDomain(label, key, range) : new VoltageDomain(label, key);

    // ---- rule: which of the two writes this CPU takes ----

    /// <summary>The fork every value's SHAPE depends on. A CPU exposing rails takes one offset per rail; one
    /// exposing none takes a single all-core value. The service asks this rather than re-deriving it, and the
    /// half below is what makes that a fact: which of the port's two methods was called must be the answer the
    /// domain gives for the port's own rail list.</summary>
    [Fact]
    public void UsesRails_IsWhetherTheCpuExposesAnyRailAtAll_AndItPicksThePortsMethod()
    {
        Assert.False(Axis().UsesRails);
        Assert.True(Axis(Rail("Zen 5", "big")).UsesRails);

        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var domainless = new FakeCurveOptimizer();
        f.Device.CurveOptimizer = domainless;

        Assert.False(Axis([.. domainless.Domains]).UsesRails);
        Assert.True(f.Service.SetCoValues([-7]).ok);
        Assert.Equal([-7], domainless.SetCalls);                 // the all-core path is the one that ran
        Assert.Empty(domainless.SetDomainsCalls);

        var railed = new FakeCurveOptimizer().WithDomains(Rail("Zen 5", "big"));
        f.Device.CurveOptimizer = railed;

        Assert.True(Axis([.. railed.Domains]).UsesRails);
        Assert.True(f.Service.SetCoValues([-7]).ok);
        Assert.Single(railed.SetDomainsCalls);                   // ...and the per-rail path is the one that ran
        Assert.Empty(railed.SetCalls);
    }

    // ---- rule: clamp each rail against its OWN range, the all-core value against the port's ----

    /// <summary>A rail that carries its own range is clamped against IT, not against the port's — here the iGPU
    /// is narrower, so the port-wide range would let through an offset this rail cannot deliver. The value of
    /// the rule is that it is per rail: the two numbers below are the same request and different answers.</summary>
    [Fact]
    public void ClampRail_UsesTheRailsOwnRange_WhereItHasOne()
    {
        var axis = Axis(Rail("Zen 5", "big"), Rail("iGPU", "igpu", (-15, 0)));

        Assert.Equal(-30, axis.ClampRail(-30, 0));               // the port's floor applies to the cores
        Assert.Equal(-15, axis.ClampRail(-30, 1));               // ...and the iGPU's own, narrower one to it
        Assert.Equal(-7, axis.ClampRail(-7, 1));                 // inside: as asked
    }

    /// <summary>The other direction, and the reason for <c>?? portRange</c> rather than "no clamp": a rail whose
    /// range is WIDER than the port's must not be narrowed by it. An implementation that used the port's range
    /// for everything would answer -30 to both.</summary>
    [Fact]
    public void ClampRail_ARailsRangeMayBeWiderThanThePorts()
    {
        var axis = Axis(Rail("Zen 5", "big", (-60, 0)), Rail("Zen 5c", "small"));

        Assert.Equal(-50, axis.ClampRail(-50, 0));               // its own, wider bound
        Assert.Equal(-30, axis.ClampRail(-50, 1));               // a rail without one inherits the port's
    }

    /// <summary>A rail with no range of its own falls back to the port's — <c>?? portRange</c>, not "unclamped".</summary>
    [Fact]
    public void ClampRail_WithoutARailsOwnRange_FallsBackToThePorts()
    {
        var axis = Axis(Rail("Zen 5", "big"));

        Assert.Equal(-30, axis.ClampRail(-100, 0));
        Assert.Equal(-12, axis.ClampRail(-12, 0));
    }

    /// <summary>The ceiling is <see cref="OffsetCounts"/>'s and is never positive, on a rail as much as on the
    /// port: a rail advertising overvolt headroom is narrowed rather than believed, and a rail whose whole range
    /// sits below stock is still obeyed — its ceiling is what applies, not zero.</summary>
    [Fact]
    public void ClampRail_NeverRepresentsAnOvervolt_AndObeysARailThatCannotReachStock()
    {
        var headroom = Axis(Rail("Zen 5", "big", (-30, 10)));
        Assert.Equal(0, headroom.ClampRail(5, 0));
        Assert.Equal(-30, headroom.ClampRail(-99, 0));

        var belowStock = Axis(Rail("Zen 5", "big", (-50, -5)));
        Assert.Equal(-5, belowStock.ClampRail(0, 0));            // this rail cannot deliver stock
    }

    /// <summary>The all-core value is bounded by the PORT's range and by nothing narrower: on a CPU without
    /// rails there is no rail to carry a bound of its own, and on one with them this number still means "the
    /// single-domain fallback" rather than any particular rail.</summary>
    [Fact]
    public void ClampAllCore_UsesThePortsRange_NotARails()
    {
        var withRailBounds = Axis(Rail("iGPU", "igpu", (-15, 0)));

        Assert.Equal(PortMin, withRailBounds.ClampAllCore(-100));    // the port's floor, not the rail's -15
        Assert.Equal(0, withRailBounds.ClampAllCore(20));            // and the ceiling is never positive

        var belowStock = new CoAxis([Rail("Zen 5", "big", (-50, -5))], (-50, -5));
        Assert.Equal(-5, belowStock.ClampAllCore(0));                // a port that cannot deliver stock is obeyed
    }

    /// <summary>The array a per-rail write sends: every rail clamped, index-aligned with the port's list.</summary>
    [Fact]
    public void ClampEach_IsIndexAligned_AndClampsEachRailAgainstItsOwn()
    {
        var axis = Axis(Rail("Zen 5", "big", (-60, 0)), Rail("iGPU", "igpu", (-15, 0)), Rail("Zen 5c", "small"));

        Assert.Equal([-50, -15, -30], axis.ClampEach([-50, -50, -50]));
    }

    // ---- rule: a rail's slot is named by its KEY, never by its position ----

    /// <summary>Rows are index-aligned with the ports's list, and a key the preset has no entry for is stock for
    /// that rail ALONE — not a neighbour's value, and not the all-core one.</summary>
    [Fact]
    public void Rows_AreIndexAligned_AndAMissingKeyIsStockForThatRailOnly()
    {
        var axis = Axis(Rail("Zen 5", "big"), Rail("Zen 5c", "small"), Rail("iGPU", "igpu"));
        var preset = new CoPreset { AllCore = -25, Domains = { ["small"] = -20, ["igpu"] = -5 } };

        Assert.Equal([0, -20, -5], axis.Rows(preset));
        Assert.Equal(-25, preset.AllCore);                        // the fallback is NOT what a missing rail reads
    }

    /// <summary>THE RULE, stated as the thing it must survive: the port's list is reordered under a preset that
    /// did not move. A position-keyed implementation answers the same array before and after — which is exactly
    /// the bug, because after a reorder that array applies the cores' undervolt to the iGPU.</summary>
    [Fact]
    public void Rows_FollowAReorderOfTheRails_WithoutMovingAStoredValue()
    {
        var preset = new CoPreset { Domains = { ["big"] = -10, ["small"] = -20 } };
        var rails = new List<VoltageDomain> { Rail("Zen 5", "big"), Rail("Zen 5c", "small") };

        Assert.Equal([-10, -20], new CoAxis(rails, (PortMin, 0)).Rows(preset));

        rails.Reverse();

        Assert.Equal([-20, -10], new CoAxis(rails, (PortMin, 0)).Rows(preset));
        Assert.Equal(-10, preset.Domains["big"]);                 // ...and the store itself did not move
        Assert.Equal(-20, preset.Domains["small"]);
        Assert.Equal(2, preset.Domains.Count);                    // the read created nothing
    }

    /// <summary>A relabelled rail is the same rail — the label is display-only — while a rail whose KEY changed
    /// is a different rail to the store and reads as stock. The two halves are the same rule seen from each side,
    /// which is why they are one test.</summary>
    [Fact]
    public void Rows_FollowALabelButNotAKey()
    {
        var preset = new CoPreset { Domains = { ["big"] = -10, ["small"] = -20 } };
        var rails = new List<VoltageDomain> { Rail("Zen 5", "big"), Rail("Zen 5c", "small") };

        rails[1] = Rail("Zen 5c (renamed)", "small");
        Assert.Equal([-10, -20], new CoAxis(rails, (PortMin, 0)).Rows(preset));

        rails[0] = Rail("Zen 5", "big-v2");
        Assert.Equal([0, -20], new CoAxis(rails, (PortMin, 0)).Rows(preset));
        Assert.Equal(-10, preset.Domains["big"]);                 // still filed under the old key
    }

    /// <summary>A CPU with no rails renders ONE row — the all-core value — so the UI draws either shape the same
    /// way. Stock when the mode has no preset.</summary>
    [Fact]
    public void Rows_WithoutRails_IsTheSingleAllCoreValue()
    {
        Assert.Equal([-18], Axis().Rows(new CoPreset { AllCore = -18 }));
        Assert.Equal([0], Axis().Rows(new CoPreset()));
    }

    /// <summary>The same rule on the way in: each count is filed under its own rail's KEY, so the value goes to
    /// the rail it was clamped for rather than staying at the index it happened to be written at.</summary>
    [Fact]
    public void File_NamesEachSlotByItsRailKey_NotByItsPosition()
    {
        var rails = new List<VoltageDomain> { Rail("Zen 5", "big"), Rail("iGPU", "igpu") };
        var preset = new CoPreset();

        new CoAxis(rails, (PortMin, 0)).File(preset, [-10, -5]);

        Assert.Equal(["big", "igpu"], preset.Domains.Keys.Order());
        Assert.Equal(-10, preset.Domains["big"]);
        Assert.Equal(-5, preset.Domains["igpu"]);

        // Filed by key, so the values survive the list moving under them — the read half of the same rule.
        rails.Reverse();
        Assert.Equal([-5, -10], new CoAxis(rails, (PortMin, 0)).Rows(preset));
    }

    // ---- rule: the never-configured guard, which on this axis means "no message at all" ----

    /// <summary>THE SECOND ANSWER of this axis, and the one a three-valued policy would have lost: an install
    /// where no mode ever had a preset does not talk to the SMU at all, while a mode that merely lacks one gets
    /// stock written actively. The two are asserted here against the DOMAIN TABLE that states the policy, so the
    /// words (<c>LeaveUntouched</c> / <c>ForceStock</c>) and the counts cannot drift apart.</summary>
    [Fact]
    public void Reapply_OnAnEmptyStore_WritesNothing_AndOnAnUnconfiguredModeWritesStock()
    {
        var axis = Axis(Rail("Zen 5", "big"), Rail("iGPU", "igpu", (-15, 0)));
        var preset = new CoPreset { AllCore = -12, Domains = { ["big"] = -20 } };

        Assert.Equal(EmptyAxisPolicy.LeaveUntouched, ModeAxisTable.PolicyWhenNoModeWasEverConfigured(ModeAxis.Co));
        Assert.Null(axis.Reapply(preset, storeIsEmpty: true));    // "leave untouched" IS no message at all

        Assert.Equal(EmptyAxisPolicy.ForceStock, ModeAxisTable.Policy(ModeAxis.Co));
        Assert.Equal([0, 0], Axis(Rail("Zen 5", "big"), Rail("iGPU", "igpu")).Reapply(new CoPreset(), false)!);
    }

    /// <summary>The ordinary case: what a mode change, a startup and a resume put on the SMU is the mode's stored
    /// offsets, index-aligned with the rails — NOT clamped again, because a stored value was clamped when it was
    /// written and re-clamping would quietly repair a preset edited by hand.</summary>
    [Fact]
    public void Reapply_IsTheModesStoredCounts_IndexAligned_AndNotClampedAgain()
    {
        var axis = Axis(Rail("Zen 5", "big", (-60, 0)), Rail("Zen 5c", "small"));
        var byHand = new CoPreset { Domains = { ["big"] = -999 } };   // a preset edited outside the app

        Assert.Equal([-999, 0], axis.Reapply(byHand, storeIsEmpty: false)!);
    }

    // ---- cross-checks: the answer the axis states is the one the port receives ----

    private static LaptopServiceFixture Setup(FakeCurveOptimizer co)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;
        return f;
    }

    /// <summary>THE CROSS-CHECK the whole file rests on. The rails the fake carries are handed to the domain
    /// type, and its answers must be the numbers the port actually received AND the ones the graph actually
    /// stored. Without this the object could be a parallel statement of the rules that production ignores —
    /// which is the failure mode this wave exists to avoid, and the one a direct-only test cannot see.</summary>
    [Fact]
    public void TheClampAndTheRowsTheAxisStatesAreTheOnesThePortAndTheGraphGet()
    {
        var co = new FakeCurveOptimizer { Range = (PortMin, 0) }.WithDomains(
            Rail("Zen 5", "big", (-60, 0)),
            Rail("iGPU", "igpu", (-15, 0)));
        var f = Setup(co);
        var requested = new[] { -50, -50 };

        Assert.True(f.Service.SetCoDomains(requested).ok);

        var axis = new CoAxis(co.Domains, co.Range);
        Assert.Equal([-50, -15], co.SetDomainsCalls[0]);                     // the answer is not vacuous
        Assert.Equal(axis.ClampEach(requested), co.SetDomainsCalls[0]);
        Assert.Equal(axis.Rows(f.Store.Settings.CoPresets["balanced"]), co.SetDomainsCalls[0]);
    }

    /// <summary>...and the same for the re-apply, on a rail list REORDERED after the write — so the cross-check
    /// covers the keying rule and not only the clamp.</summary>
    [Fact]
    public void TheReapplyTheAxisStatesIsTheOneTheSmuGets_OnAReorderedRailList()
    {
        var co = new FakeCurveOptimizer { Range = (PortMin, 0) }.WithDomains(
            Rail("Zen 5", "big"), Rail("Zen 5c", "small"));
        var f = Setup(co);
        f.Service.SetCoDomains([-10, -20]);

        co.DomainList.Reverse();

        f.Service.ApplyModeCo();

        var axis = new CoAxis(co.Domains, co.Range);
        Assert.Equal([-20, -10], co.SetDomainsCalls[^1]);                    // the port got the reordered values
        Assert.Equal<int[]>(axis.Reapply(f.Store.Settings.CoPresets["balanced"], storeIsEmpty: false)!, co.SetDomainsCalls[^1]);
    }
}
