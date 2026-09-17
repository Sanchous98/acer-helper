using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The Curve Optimizer write path (`LaptopService.Tuning.cs` `StoredCo`-`ApplyModeCo`) — the one axis where an out-of-range value is
/// not merely cosmetic. The offset is applied to the SMU; a value the user did not ask for is an undervolt
/// they did not ask for, and the failure mode of too aggressive an offset is an instability hours later at
/// idle rather than an error at the call. So the three things pinned here are the ones that decide what the
/// hardware is told: WHERE it is clamped, WHICH rail each number belongs to, and WHICH mode's preset is read.
///
/// <see cref="FakeCurveOptimizer"/> carries the real port's shape — <c>Range = (-30, 0)</c>, undervolt only —
/// and a deliberately MUTABLE <see cref="FakeCurveOptimizer.DomainList"/>, because the store is keyed by
/// <see cref="VoltageDomain.Key"/> and reordering the hardware's rails is the only honest way to prove it.
/// </summary>
public class LaptopServiceCoClampTests
{
    private static LaptopServiceFixture Setup(FakeCurveOptimizer co, PerformanceProfile? current = null)
    {
        var f = LaptopServiceFixture.WithProfiles(current: current ?? TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;
        return f;
    }

    // ---- SetCo: the ALL-CORE path, clamped against the port's range BEFORE it is persisted ----

    /// <summary>Out of range is stored CLAMPED, not raw: the port clamps what it sends to the SMU anyway
    /// (Domain/Ports.cs:196-199), so a raw value on disk would make the app report an undervolt the
    /// hardware never got (`LaptopService.Tuning.cs` `SetCo` — the port-range clamp before persisting).</summary>
    [Theory]
    [InlineData(-100, -30)]      // below the port's Min
    [InlineData(100, 0)]         // above the port's Max
    [InlineData(-12, -12)]       // inside: stored as asked
    [InlineData(-30, -30)]       // exactly Min
    [InlineData(0, 0)]           // exactly Max (stock)
    public void SetCo_ClampsAgainstThePortsRange_BeforePersisting(int requested, int expected)
    {
        var co = new FakeCurveOptimizer();                       // Range (-30, 0)
        var f = Setup(co);

        Assert.True(f.Service.SetCo(requested).ok);

        Assert.Equal(expected, f.Store.Settings.CoPresets["balanced"].AllCore);
        Assert.Equal([expected], co.SetCalls);                   // ...and the same clamped value went to the SMU
        Assert.Equal(1, f.Store.SaveCount);
    }

    /// <summary>...and the bounds come from the PORT, not from a hard-coded 0. A CPU whose range is entirely
    /// negative (<c>(-50, -5)</c>) must clamp to <i>its</i> ceiling, so this would fail against any
    /// implementation that assumed Max = 0.</summary>
    [Theory]
    [InlineData(0, -5)]          // the user asks for stock; this port cannot deliver stock
    [InlineData(-99, -50)]
    [InlineData(-20, -20)]
    public void SetCo_FollowsThePortsRange_NotAHardCodedOne(int requested, int expected)
    {
        var co = new FakeCurveOptimizer { Range = (-50, -5) };
        var f = Setup(co);

        Assert.True(f.Service.SetCo(requested).ok);

        Assert.Equal(expected, f.Store.Settings.CoPresets["balanced"].AllCore);
        Assert.Equal([expected], co.SetCalls);
    }

    /// <summary>NOTE the asymmetry, derived from the code: <c>SetCo</c> persists unconditionally
    /// (`LaptopService.Tuning.cs` `SetCo` — the persist block; the port is only consulted afterwards), so on a machine whose
    /// Curve-Optimizer port was probed away a <c>SetCo</c> still writes a preset and returns false, and the
    /// value is NOT clamped (the clamp in `LaptopService.Tuning.cs` `SetCo` is guarded by <c>co != null</c>). <c>SetCoDomains</c> does the opposite: it
    /// returns before persisting (`LaptopService.Tuning.cs` `SetCoDomains` — the port and count guard). Pinned as shipped; see LaptopServiceCoCountTests.</summary>
    [Fact]
    public void SetCo_WithNoPort_ReturnsFalse_ButStillPersistsTheUnclampedValue()
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);

        var r = f.Service.SetCo(-100);

        Assert.False(r.ok);
        Assert.Null(r.error);                                    // no port -> nothing was attempted, no reason
        Assert.Equal(-100, f.Store.Settings.CoPresets["balanced"].AllCore);   // no port -> no clamp
        Assert.Equal(1, f.Store.SaveCount);
    }

    /// <summary>The reason travels WITH the result rather than being parked in a field for whoever reads it
    /// last, and that is the whole point of the change: the CPU-undervolt path hands its work to a background
    /// task and posts the failure back to the UI, so a shared field read at post time could carry a DIFFERENT
    /// call's error — the one the user would then be shown. A returned value cannot be someone else's.</summary>
    [Fact]
    public void SetCoValues_ReportsThePortsReason_WithTheResult()
    {
        var co = new FakeCurveOptimizer { SetResult = false, LastError = "SMU refused" };
        var f = Setup(co);

        var r = f.Service.SetCoValues([-12]);

        Assert.False(r.ok);
        Assert.Equal("SMU refused", r.error);
    }

    /// <summary>A refused mailbox transaction is reported but is NOT a reason to forget the setting: the
    /// preset is already on disk and the app's contract is that it re-applies on the next mode change.</summary>
    [Fact]
    public void SetCo_WhenTheSmuRefuses_ReturnsFalse_ReportsTheError_AndKeepsThePreset()
    {
        var co = new FakeCurveOptimizer { SetResult = false, LastError = "SMU refused" };
        var f = Setup(co);

        var r = f.Service.SetCo(-12);

        Assert.False(r.ok);
        Assert.Equal("SMU refused", r.error);                    // the reason comes back with the result
        Assert.Equal(-12, f.Store.Settings.CoPresets["balanced"].AllCore);
        Assert.Equal(1, f.Store.SaveCount);
    }

    [Fact]
    public void SetCo_IsPerMode()
    {
        var co = new FakeCurveOptimizer();
        var f = Setup(co);

        f.Service.SetCo(-12);
        Assert.Equal(-12, f.Service.CurrentCo().AllCore);

        f.Pp!.CurrentProfile = TestProfiles.Performance;

        Assert.Equal(0, f.Service.CurrentCo().AllCore);          // a different mode, a different preset
        Assert.Single(f.Store.Settings.CoPresets);

        f.Service.SetCo(-20);

        Assert.Equal(2, f.Store.Settings.CoPresets.Count);
        Assert.Equal(["balanced", "performance"], f.Store.Settings.CoPresets.Keys.Order());
        Assert.Equal(-12, f.Store.Settings.CoPresets["balanced"].AllCore);
    }
}

/// <summary>
/// The PER-DOMAIN path: the rail each number is stored under, and the bounds each number is clamped to.
/// Both are the kind of bug that produces a plausible-looking offset on the wrong rail — which is why the
/// clamp reads <c>Domains[i].Range ?? Range</c> per domain (`LaptopService.Tuning.cs` `SetCoDomains` — the per-domain clamp loop) rather than the port's
/// range for the whole array.
/// </summary>
public class LaptopServiceCoDomainClampTests
{
    private static LaptopServiceFixture Setup(FakeCurveOptimizer co)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;
        return f;
    }

    /// <summary>On an APU the cores and the iGPU are separate rails with different bounds, so one shared
    /// range would either widen the iGPU's or narrow the cores'. Each domain's own Range wins.</summary>
    [Fact]
    public void SetCoDomains_ClampsEachDomainAgainstItsOwnRange()
    {
        var co = new FakeCurveOptimizer { Range = (-30, 0) }.WithDomains(
            new VoltageDomain("Zen 5", "big"),
            new VoltageDomain("iGPU", "igpu", Range: (-15, 0)));
        var f = Setup(co);

        Assert.True(f.Service.SetCoDomains([-30, -30]).ok);

        Assert.Equal(new[] { -30, -15 }, co.SetDomainsCalls[0]);
        var stored = f.Store.Settings.CoPresets["balanced"].Domains;
        Assert.Equal(-30, stored["big"]);
        Assert.Equal(-15, stored["igpu"]);                       // the iGPU's own, narrower bound
    }

    /// <summary>The other direction: a domain range WIDER than the port's must not be narrowed by it. If the
    /// clamp used the port's range for everything, both values here would come out at -30.</summary>
    [Fact]
    public void SetCoDomains_ADomainRangeMayBeWiderThanThePorts()
    {
        var co = new FakeCurveOptimizer { Range = (-30, 0) }.WithDomains(
            new VoltageDomain("Zen 5", "big", Range: (-60, 0)),
            new VoltageDomain("Zen 5c", "small"));
        var f = Setup(co);

        Assert.True(f.Service.SetCoDomains([-50, -50]).ok);

        Assert.Equal(new[] { -50, -30 }, co.SetDomainsCalls[0]);
        var stored = f.Store.Settings.CoPresets["balanced"].Domains;
        Assert.Equal(-50, stored["big"]);
        Assert.Equal(-30, stored["small"]);
    }

    /// <summary>A domain with no Range of its own inherits the port's — <c>?? Range</c>, not "no clamp".</summary>
    [Theory]
    [InlineData(-100, -30)]
    [InlineData(50, 0)]
    [InlineData(-7, -7)]
    public void SetCoDomains_WithoutADomainRange_FallsBackToThePortsRange(int requested, int expected)
    {
        var co = new FakeCurveOptimizer().WithDomains(new VoltageDomain("Zen 5", "big"));
        var f = Setup(co);

        Assert.True(f.Service.SetCoDomains([requested]).ok);

        Assert.Equal(new[] { expected }, co.SetDomainsCalls[0]);
        Assert.Equal(expected, f.Store.Settings.CoPresets["balanced"].Domains["big"]);
    }

    /// <summary>The all-core value is NOT touched by the per-domain path — it is a separate field with a
    /// separate meaning (the single-domain fallback, Settings.cs:130-133), and mixing the two would apply
    /// one rail's number to the whole package on a CPU that later loses domain control.</summary>
    [Fact]
    public void SetCoDomains_LeavesTheAllCoreValueAlone()
    {
        var co = new FakeCurveOptimizer().WithDomains(
            new VoltageDomain("Zen 5", "big"),
            new VoltageDomain("Zen 5c", "small"));
        var f = Setup(co);
        f.Service.SetCo(-12);                                    // the user had used the all-core path before

        f.Service.SetCoDomains([-5, -6]);

        var stored = f.Store.Settings.CoPresets["balanced"];
        Assert.Equal(-12, stored.AllCore);                       // preserved, not overwritten
        Assert.Equal(-5, stored.Domains["big"]);
        Assert.Equal(-6, stored.Domains["small"]);
    }

    [Fact]
    public void SetCoValues_WithoutDomains_LeavesTheDomainMapEmpty()
    {
        var co = new FakeCurveOptimizer();                       // no domains
        var f = Setup(co);

        Assert.True(f.Service.SetCoValues([-12, -20]).ok);

        var stored = f.Store.Settings.CoPresets["balanced"];
        Assert.Equal(-12, stored.AllCore);
        Assert.Empty(stored.Domains);                            // the extras are dropped, not stored per key
    }
}

/// <summary>
/// The keying rule: a per-domain offset is stored under <see cref="VoltageDomain.Key"/>, never under its
/// position in the list (`LaptopService.Tuning.cs` `CurrentCoDomains`, `SetCoDomains`, `ApplyModeCo`). The two are indistinguishable until the list
/// moves — which it does whenever a backend enumerates its rails in another order, renames a label, or the
/// machine's firmware reports the domains differently after an update. Getting this wrong silently applies
/// the cores' undervolt to the iGPU.
/// </summary>
public class LaptopServiceCoKeyingTests
{
    private static LaptopServiceFixture Setup(FakeCurveOptimizer co)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;
        return f;
    }

    private static FakeCurveOptimizer TwoDomains() => new FakeCurveOptimizer().WithDomains(
        new VoltageDomain("Zen 5", "big"),
        new VoltageDomain("Zen 5c", "small"));

    /// <summary>Write per-domain values, then REORDER the hardware's domain list: the store must not move,
    /// and every read/write afterwards must follow the new order while still naming the same rails.</summary>
    [Fact]
    public void PerDomainValues_SurviveAReorderOfTheHardwareDomains()
    {
        var co = TwoDomains();
        var f = Setup(co);

        Assert.True(f.Service.SetCoDomains([-10, -20]).ok);
        var stored = f.Store.Settings.CoPresets["balanced"].Domains;
        Assert.Equal(-10, stored["big"]);
        Assert.Equal(-20, stored["small"]);
        Assert.Equal(new[] { -10, -20 }, co.SetDomainsCalls[0]);

        co.DomainList.Reverse();                                 // now: [small, big]

        Assert.Equal(new[] { -20, -10 }, f.Service.CurrentCoDomains());   // index-aligned with the NEW list
        Assert.Equal(-10, stored["big"]);                        // ...and the store itself did not move
        Assert.Equal(-20, stored["small"]);
        Assert.Equal(2, stored.Count);                           // nothing was written by the read

        f.Service.ApplyModeCo();

        Assert.Equal(2, co.SetDomainsCalls.Count);
        Assert.Equal(new[] { -20, -10 }, co.SetDomainsCalls[1]); // the SMU is told the reordered values too
    }

    /// <summary>Relabelling a domain (Label is display-only, Domain/Ports.cs:205-208) must keep its value:
    /// the key is what the preset is filed under.</summary>
    [Fact]
    public void PerDomainValues_SurviveARelabelledDomain()
    {
        var co = TwoDomains();
        var f = Setup(co);
        f.Service.SetCoDomains([-10, -20]);

        co.DomainList[0] = new VoltageDomain("Zen 5 (renamed)", "big");

        Assert.Equal(new[] { -10, -20 }, f.Service.CurrentCoDomains());
    }

    /// <summary>The counter-proof: a domain whose KEY changes is a different rail as far as the store is
    /// concerned, so its value reads as stock. A position-keyed implementation would still report -10 here —
    /// that is exactly the bug this test exists to catch.</summary>
    [Fact]
    public void ADomainWhoseKeyChanges_ReadsAsStock_NotAsTheOldValue()
    {
        var co = TwoDomains();
        var f = Setup(co);
        f.Service.SetCoDomains([-10, -20]);

        co.DomainList[0] = new VoltageDomain("Zen 5", "big-v2");

        Assert.Equal(new[] { 0, -20 }, f.Service.CurrentCoDomains());
        Assert.Equal(-10, f.Store.Settings.CoPresets["balanced"].Domains["big"]);   // still filed under the old key
    }

    /// <summary>A domain the preset has no entry for is stock for THAT rail only — never the all-core value,
    /// and never a neighbour's (Settings.cs:137-138).</summary>
    [Fact]
    public void AMissingDomainKey_ReadsAsStock_NotAsTheAllCoreValue()
    {
        var co = TwoDomains();
        var f = Setup(co);
        f.Service.SetCo(-25);                                    // all-core only: no per-domain entry at all

        Assert.Equal(new[] { 0, 0 }, f.Service.CurrentCoDomains());
        Assert.Equal(-25, f.Store.Settings.CoPresets["balanced"].AllCore);
    }

    [Fact]
    public void PerDomainPresets_ArePerMode()
    {
        var co = TwoDomains();
        var f = Setup(co);
        f.Service.SetCoDomains([-10, -20]);

        f.Pp!.CurrentProfile = TestProfiles.Performance;

        Assert.Equal(new[] { 0, 0 }, f.Service.CurrentCoDomains());
        Assert.Single(f.Store.Settings.CoPresets);       // the read created nothing

        f.Service.SetCoDomains([-1, -2]);

        Assert.Equal(2, f.Store.Settings.CoPresets.Count);
        Assert.Equal(-10, f.Store.Settings.CoPresets["balanced"].Domains["big"]);
        Assert.Equal(-1, f.Store.Settings.CoPresets["performance"].Domains["big"]);
    }
}

/// <summary>
/// The guards that decide whether a call reaches the graph and the port at all: a null port, a count
/// mismatch, and the routing between the all-core and per-domain paths.
/// </summary>
public class LaptopServiceCoCountTests
{
    private static LaptopServiceFixture Setup(FakeCurveOptimizer? co)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;
        return f;
    }

    private static FakeCurveOptimizer TwoDomains() => new FakeCurveOptimizer().WithDomains(
        new VoltageDomain("Zen 5", "big"),
        new VoltageDomain("Zen 5c", "small"));

    /// <summary>The counts are index-aligned with <c>co.Domains</c>, so a length disagreement has no correct
    /// interpretation — and the call is refused BEFORE anything is stored or sent (`LaptopService.Tuning.cs` `SetCoDomains` — the port and count guard).</summary>
    [Theory]
    [InlineData(1)]              // one short
    [InlineData(3)]              // one too many
    [InlineData(0)]              // empty
    public void SetCoDomains_WithACountMismatch_ReturnsFalse_AndPersistsNothing(int count)
    {
        var co = TwoDomains();
        var f = Setup(co);
        var counts = Enumerable.Range(0, count).Select(i => -i - 1).ToArray();

        Assert.False(f.Service.SetCoDomains(counts).ok);

        Assert.Empty(co.SetDomainsCalls);
        Assert.Empty(f.Store.Settings.CoPresets);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>...and with no port the guard fires first too — the exact opposite of <c>SetCo</c>, which
    /// persists before it looks (`LaptopService.Tuning.cs` `SetCoDomains` vs `SetCo`). Pinned as shipped: the two halves of the
    /// same feature disagree about what an unwritable install should remember.</summary>
    [Fact]
    public void SetCoDomains_WithNoPort_ReturnsFalse_AndPersistsNothing()
    {
        var f = Setup(null);

        Assert.False(f.Service.SetCoDomains([-10, -20]).ok);

        Assert.Empty(f.Store.Settings.CoPresets);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>NOTE the one shape that slips PAST the mismatch guard: a domainless CPU with an empty count
    /// array agrees length-wise (<c>0 != 0</c> is false), so the call is accepted — it inserts an empty preset
    /// and sends the SMU a <c>SetDomains</c> carrying nothing. The consequence is larger than the message: the
    /// store is no longer empty, which permanently disarms the never-configured guard in
    /// <c>ApplyModeCo</c> (`LaptopService.Tuning.cs` `ApplyModeCo`), so every later mode switch, startup and resume now talks to
    /// the SMU on an install whose user never configured an undervolt. Pinned as shipped; unreachable from the
    /// shipped UI, which only ever calls <c>SetCoValues</c> (UI/AppController.cs:372) and that refuses an
    /// empty array (`LaptopService.Tuning.cs` `SetCoValues` — the empty-count refusal).</summary>
    [Fact]
    public void SetCoDomains_WithADomainlessCpuAndNoCounts_IsAccepted_AndDisarmsTheNeverConfiguredGuard()
    {
        var co = new FakeCurveOptimizer();                       // no domains
        var f = Setup(co);

        Assert.True(f.Service.SetCoDomains([]).ok);

        Assert.Single(co.SetDomainsCalls);
        Assert.Empty(co.SetDomainsCalls[0]);                     // a mailbox message that carries nothing
        Assert.Single(f.Store.Settings.CoPresets);               // ...but the install now reads as "configured"
        Assert.Empty(f.Store.Settings.CoPresets["balanced"].Domains);   // and holds no rail values at all
        Assert.Equal(1, f.Store.SaveCount);
    }

    /// <summary>SetCoValues is the single entry point the UI uses, and it picks the path from the PORT:
    /// domains present -> per-domain, none -> all-core (`LaptopService.Tuning.cs` `SetCoValues`).</summary>
    [Fact]
    public void SetCoValues_WithoutDomains_RoutesToTheAllCoreSet()
    {
        var co = new FakeCurveOptimizer();                       // empty Domains == one offset for everything
        var f = Setup(co);

        Assert.True(f.Service.SetCoValues([-12, -20, -30]).ok);

        Assert.Equal([-12], co.SetCalls);                        // ONLY counts[0] — the rest have no rail
        Assert.Empty(co.SetDomainsCalls);
        Assert.Equal(-12, f.Store.Settings.CoPresets["balanced"].AllCore);
    }

    [Fact]
    public void SetCoValues_WithDomains_RoutesToSetDomains()
    {
        var co = TwoDomains();
        var f = Setup(co);

        Assert.True(f.Service.SetCoValues([-5, -6]).ok);

        Assert.Empty(co.SetCalls);
        Assert.Single(co.SetDomainsCalls);
        Assert.Equal(new[] { -5, -6 }, co.SetDomainsCalls[0]);
        var stored = f.Store.Settings.CoPresets["balanced"].Domains;
        Assert.Equal(-5, stored["big"]);
        Assert.Equal(-6, stored["small"]);
    }

    [Theory]
    [InlineData("no-port")]
    [InlineData("no-counts")]
    public void SetCoValues_WithNoPortOrNoCounts_ReturnsFalse_AndTouchesNothing(string which)
    {
        var co = which == "no-port" ? null : new FakeCurveOptimizer();
        var f = Setup(co);
        int[] counts = which == "no-port" ? [-5] : [];

        Assert.False(f.Service.SetCoValues(counts).ok);

        Assert.Empty(f.Store.Settings.CoPresets);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>A mismatch is caught by the per-domain path it routes to, so SetCoValues inherits the
    /// refusal — and, again, nothing is stored.</summary>
    [Fact]
    public void SetCoValues_WithACountMismatch_ReturnsFalse_AndPersistsNothing()
    {
        var co = TwoDomains();
        var f = Setup(co);

        Assert.False(f.Service.SetCoValues([-5]).ok);

        Assert.Empty(co.SetDomainsCalls);
        Assert.Empty(f.Store.Settings.CoPresets);
        Assert.Equal(0, f.Store.SaveCount);
    }
}

/// <summary>
/// <see cref="LaptopService.CurrentCoDomains"/> — the read the UI renders its rows from. It must answer in
/// the port's own shape (Domain/Ports.cs:183-190) without ever creating a preset, and it must not confuse
/// "no Curve-Optimizer port at all" (nothing to show) with "a CPU that takes one offset" (one row).
/// </summary>
public class LaptopServiceCurrentCoDomainsTests
{
    private static LaptopServiceFixture Setup(FakeCurveOptimizer? co)
    {
        var f = LaptopServiceFixture.WithProfiles(current: TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;
        return f;
    }

    [Fact]
    public void WithNoPort_IsEmpty()
    {
        var f = Setup(null);

        Assert.Empty(f.Service.CurrentCoDomains());
        Assert.Empty(f.Store.Settings.CoPresets);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>A CPU with no domain control reports ONE row — the all-core value — so the UI can render it
    /// without knowing which path is in play. The value is the stored one, or stock when unconfigured.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-18)]
    public void WithADomainlessCpu_IsASingleAllCoreValue(int allCore)
    {
        var co = new FakeCurveOptimizer();
        var f = Setup(co);
        if (allCore != 0) f.Service.SetCo(allCore);
        var before = f.Store.Settings.CoPresets.Count;

        Assert.Equal(new[] { allCore }, f.Service.CurrentCoDomains());
        Assert.Equal(before, f.Store.Settings.CoPresets.Count);   // the read created nothing
    }

    /// <summary>Otherwise the array has one entry per domain, IN THE PORT'S ORDER, with a missing key read
    /// as stock — index-aligned so the UI can zip it against <c>Domains</c>.</summary>
    [Fact]
    public void WithDomains_IsIndexAligned_MissingKeysReadAsZero()
    {
        var co = new FakeCurveOptimizer().WithDomains(
            new VoltageDomain("Zen 5", "big"),
            new VoltageDomain("Zen 5c", "small"),
            new VoltageDomain("iGPU", "igpu"));
        var f = Setup(co);
        f.Store.Settings.CoPresets["balanced"] = new CoPreset { Domains = { ["small"] = -20, ["igpu"] = -5 } };

        Assert.Equal(new[] { 0, -20, -5 }, f.Service.CurrentCoDomains());
    }

    /// <summary>The array is a fresh one per call: the caller cannot corrupt the store by mutating what it
    /// was handed.</summary>
    [Fact]
    public void ReturnsAFreshArray_EachCall()
    {
        var co = new FakeCurveOptimizer().WithDomains(new VoltageDomain("Zen 5", "big"));
        var f = Setup(co);
        f.Service.SetCoDomains([-10]);

        var first = f.Service.CurrentCoDomains();
        var second = f.Service.CurrentCoDomains();

        Assert.NotSame(first, second);
        Assert.Equal(new[] { -10 }, first);
        Assert.Equal(new[] { -10 }, second);
    }
}

/// <summary>
/// <see cref="LaptopService.ApplyModeCo"/> — the mode-change / startup / resume re-apply. Two contracts live
/// here and they pull in opposite directions, which is why both are pinned:
///
/// (a) an install that has NEVER configured a Curve Optimizer must not talk to the SMU at all — the guard in
///     `LaptopService.Tuning.cs` `ApplyModeCo`, so a user who never opted into undervolting never gets an unconfirmed mailbox
///     message on their CPU; and
/// (b) once anything IS configured, a mode with no preset is definitely stock and MUST be actively cleared,
///     because the offset is SMU-resident and a stale one carried into a profile the user never configured
///     is how an unexplained instability happens (Settings.cs:49-55).
/// </summary>
public class LaptopServiceApplyModeCoTests
{
    private static LaptopServiceFixture Setup(FakeCurveOptimizer co, PerformanceProfile? current = null)
    {
        var f = LaptopServiceFixture.WithProfiles(current: current ?? TestProfiles.Balanced);
        f.Device.CurveOptimizer = co;
        return f;
    }

    private static FakeCurveOptimizer TwoDomains() => new FakeCurveOptimizer().WithDomains(
        new VoltageDomain("Zen 5", "big"),
        new VoltageDomain("Zen 5c", "small"));

    // ---- (a) the never-configured guard ----

    [Theory]
    [InlineData("all-core")]
    [InlineData("per-domain")]
    public void WithAnEmptyStore_NeverTalksToTheSmu_AndInsertsNothing(string cpu)
    {
        var co = cpu == "per-domain" ? TwoDomains() : new FakeCurveOptimizer();
        var f = Setup(co);

        var applied = f.Service.ApplyModeCo();

        Assert.Equal(0, applied.AllCore);
        Assert.Empty(co.SetCalls);                               // no mailbox message at all...
        Assert.Empty(co.SetDomainsCalls);
        Assert.Empty(f.Store.Settings.CoPresets);                // ...and no preset minted by the read
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>And the returned default is not filed anywhere — a caller that reflects it in the UI must
    /// not thereby create the entry the guard exists to avoid.</summary>
    [Fact]
    public void WithAnEmptyStore_TheReturnedPresetIsNotStored()
    {
        var co = new FakeCurveOptimizer();
        var f = Setup(co);

        var applied = f.Service.ApplyModeCo();

        Assert.DoesNotContain(applied, f.Store.Settings.CoPresets.Values);
        Assert.Empty(f.Store.Settings.CoPresets);
    }

    // ---- (b) the actively-cleared contract ----

    [Fact]
    public void AfterSetCo_ReappliesTheStoredOffset()
    {
        var co = new FakeCurveOptimizer();
        var f = Setup(co);
        f.Service.SetCo(-12);
        Assert.Equal([-12], co.SetCalls);                        // the write from SetCo

        var applied = f.Service.ApplyModeCo();

        Assert.Equal([-12, -12], co.SetCalls);                   // re-applied, same value
        Assert.NotSame(f.Store.Settings.CoPresets["balanced"], applied);
        Assert.Equal(f.Store.Settings.CoPresets["balanced"].AllCore, applied.AllCore);
        Assert.Equal(1, f.Store.SaveCount);                      // the re-apply persisted nothing
    }

    [Fact]
    public void AfterSetCoDomains_ReappliesTheStoredCounts()
    {
        var co = TwoDomains();
        var f = Setup(co);
        f.Service.SetCoDomains([-10, -20]);

        var applied = f.Service.ApplyModeCo();

        Assert.Equal(2, co.SetDomainsCalls.Count);
        Assert.Equal(new[] { -10, -20 }, co.SetDomainsCalls[1]);
        Assert.Empty(co.SetCalls);                               // the domain path is the one in play
        Assert.NotSame(f.Store.Settings.CoPresets["balanced"], applied);
        Assert.Equal(f.Store.Settings.CoPresets["balanced"].AllCore, applied.AllCore);
        Assert.Equal(1, f.Store.SaveCount);
    }

    /// <summary>The clearing case: another mode IS configured, this one is not — so this one is stock, and
    /// the SMU is told so. A never-configured mode must not inherit the previous profile's undervolt.</summary>
    [Fact]
    public void OnAConfiguredInstall_AnUnconfiguredMode_ClearsWithStock()
    {
        var co = new FakeCurveOptimizer();
        var f = Setup(co);
        f.Service.SetCo(-12);

        f.Pp!.CurrentProfile = TestProfiles.Performance;
        var applied = f.Service.ApplyModeCo();

        Assert.Equal([-12, 0], co.SetCalls);                     // the second call is the clear
        Assert.Equal(0, applied.AllCore);
        Assert.Equal(["balanced"], f.Store.Settings.CoPresets.Keys);   // ...and no entry was created for it
        Assert.Equal(1, f.Store.SaveCount);
    }

    /// <summary>...and the same on a per-domain CPU: the unconfigured mode sends a full array of zeros, one
    /// per rail, rather than skipping the rails it has no value for.</summary>
    [Fact]
    public void OnAConfiguredInstall_AnUnconfiguredMode_ClearsEveryDomain()
    {
        var co = TwoDomains();
        var f = Setup(co);
        f.Service.SetCoDomains([-10, -20]);

        f.Pp!.CurrentProfile = TestProfiles.Quiet;
        f.Service.ApplyModeCo();

        Assert.Equal(2, co.SetDomainsCalls.Count);
        Assert.Equal(new[] { 0, 0 }, co.SetDomainsCalls[1]);
        Assert.Single(f.Store.Settings.CoPresets);
    }

    /// <summary>A single-domain CPU takes the all-core path even after the per-domain API was used on some
    /// other machine's preset — the path is chosen from the PORT's current shape, not from the preset.</summary>
    [Fact]
    public void WithADomainlessCpu_FallsBackToTheAllCoreValue()
    {
        var co = new FakeCurveOptimizer();                       // a CPU that takes one offset
        var f = Setup(co);
        f.Store.Settings.CoPresets["balanced"] = new CoPreset { AllCore = -9, Domains = { ["big"] = -20 } };

        var applied = f.Service.ApplyModeCo();

        Assert.Equal([-9], co.SetCalls);
        Assert.Empty(co.SetDomainsCalls);
        Assert.Equal(-9, applied.AllCore);
    }

    // ---- degenerate installs ----

    /// <summary>With the port gone there is nothing to apply to, but the preset the user configured is
    /// still what the UI should reflect — so it is returned rather than dropped.</summary>
    [Fact]
    public void WithNoPort_ReturnsTheStoredPreset_WithoutWriting()
    {
        var settings = new Settings();
        settings.CoPresets["balanced"] = new CoPreset { AllCore = -12 };
        var f = LaptopServiceFixture.WithProfiles(settings, current: TestProfiles.Balanced);

        var applied = f.Service.ApplyModeCo();

        // The subject is the preset the MODEL holds — the one a later read would hand out — rather than the
        // instance this test seeded, which the model took a copy of at construction (Settings' constructor).
        Assert.NotSame(f.Store.Settings.CoPresets["balanced"], applied);
        Assert.Equal(-12, applied.AllCore);
        Assert.Equal(0, f.Store.SaveCount);
    }

    /// <summary>The SMU refusing is swallowed, not thrown, and does not stop the preset being reported — the
    /// caller is the refresh/startup path and has nowhere to put an exception.
    ///
    /// The reason is NOT recorded anywhere, and that is deliberate: <c>ApplyModeCo</c> has no reader of its
    /// outcome (both of its call sites are fire-and-forget), so giving it a channel would mean starting to show
    /// the user a failure message the app has never shown. The write path the user actually drives —
    /// <c>SetCo</c>/<c>SetCoValues</c> — does report it, which the test above pins.</summary>
    [Fact]
    public void WhenTheSmuRefuses_TheReApplyIsSwallowed_AndStillReturnsThePreset()
    {
        var co = new FakeCurveOptimizer { SetDomainsResult = false, LastError = "SMU refused" };
        var f = Setup(co.WithDomains(new VoltageDomain("Zen 5", "big")));
        f.Service.SetCoDomains([-10]);

        var applied = f.Service.ApplyModeCo();

        Assert.NotSame(f.Store.Settings.CoPresets["balanced"], applied);
        Assert.Equal(f.Store.Settings.CoPresets["balanced"].AllCore, applied.AllCore);
        Assert.Equal(2, co.SetDomainsCalls.Count);
    }
}
