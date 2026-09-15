using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="OffsetCounts"/> — the one value type whose invariant the PORT ITSELF declares ("Min &lt; 0,
/// Max = 0 — undervolt only", <c>Domain/Ports.cs</c>) and which until now was enforced only by clamps at the
/// call sites. The cases below are chosen so that the value type and the plain <c>Math.Clamp(raw, range.Min,
/// range.Max)</c> it replaced AGREE on every range that exists today, and disagree exactly on the one that
/// matters: a port advertising positive headroom, where the type refuses the overvolt instead of believing it.
///
/// The `(-50, -5)` cases are the other half, and they came from a REGRESSION this file originally encoded: the
/// first version hard-coded the ceiling at 0, and <c>SetCo_FollowsThePortsRange_NotAHardCodedOne</c> — an
/// existing test — went red, because a rail whose ceiling is itself below stock must still be obeyed. The
/// implementation now takes <c>min(range.Max, 0)</c>, which refuses an overvolt without pretending every rail
/// can reach zero.
/// </summary>
public class OffsetCountsTests
{
    [Theory]
    [InlineData(5, -40, 0, 0)]        // a positive request is not representable: it becomes stock
    [InlineData(1, -40, 0, 0)]
    [InlineData(0, -40, 0, 0)]        // stock
    [InlineData(-12, -40, 0, -12)]    // inside: stored as asked
    [InlineData(-40, -40, 0, -40)]    // exactly the rail's floor
    [InlineData(-100, -40, 0, -40)]   // below the floor
    [InlineData(-50, -50, -5, -50)]   // a rail that cannot even reach stock (a purely negative range)
    [InlineData(-4, -50, -5, -5)]     // ...and its ceiling is obeyed
    [InlineData(5, -30, 10, 0)]       // a port asking for overvolt headroom is NARROWED, not believed
    [InlineData(-99, -30, 10, -30)]   // ...and its floor still applies
    [InlineData(7, 3, 9, 0)]          // a nonsensical port cannot produce a positive offset either
    public void NeverRepresentsAnOvervolt(int raw, int min, int max, int expected)
        => Assert.Equal(expected, OffsetCounts.Clamp(raw, (min, max)).Counts);

    /// <summary>The value a mode with no preset is put back to.</summary>
    [Fact]
    public void StockIsZero() => Assert.Equal(0, OffsetCounts.Stock.Counts);

    /// <summary>And the SERVICE uses it — the type is only worth having if the clamp it replaced is gone. A
    /// positive offset asked of the real write path comes out as stock in BOTH places that matter: what gets
    /// persisted (which the app reports to the user) and what is sent to the SMU (which is what actually
    /// happens to the voltage).</summary>
    [Fact]
    public void TheServiceCannotPersistAnOvervolt()
    {
        var co = new FakeCurveOptimizer();                       // (-30, 0), the real port's shape
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;

        Assert.True(f.Service.SetCo(20).ok);

        Assert.Equal(0, f.Store.Settings.CoPresets["balanced"].AllCore);
        Assert.Equal([0], co.SetCalls);
    }

    /// <summary>The same claim on the per-domain path, which clamps each rail against its OWN range.</summary>
    [Fact]
    public void ThePerDomainPathCannotPersistAnOvervoltEither()
    {
        var co = new FakeCurveOptimizer { Range = (-30, 0) }.WithDomains(
            new VoltageDomain("Zen 5", "big", Range: (-60, 0)),
            new VoltageDomain("Zen 5c", "small"));
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;

        Assert.True(f.Service.SetCoDomains([20, 20]).ok);

        Assert.Equal(new[] { 0, 0 }, co.SetDomainsCalls[0]);
        Assert.Equal(0, f.Store.Settings.CoPresets["balanced"].Domains["big"]);
        Assert.Equal(0, f.Store.Settings.CoPresets["balanced"].Domains["small"]);
    }
}
