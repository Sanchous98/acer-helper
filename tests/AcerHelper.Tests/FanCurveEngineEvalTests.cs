using AcerHelper.Domain;

namespace AcerHelper.Tests;

/// <summary>
/// Pins the fan curve's declared SHAPE. <see cref="Fan.Anchors"/> and
/// <see cref="Fan.DefaultCurve"/> are the single source of truth the UI graph also reads, so a
/// drift between the documented spec and the array is exactly the kind of change that produces wrong fan
/// speeds with no error anywhere. These assertions are the spec, not a restatement of the code.
/// </summary>
public class FanCurveSpecTests
{
    [Fact]
    public void Anchors_AreTheDocumentedFivePointScale() =>
        Assert.Equal(new[] { 50, 60, 70, 80, 90 }, Fan.Anchors);

    [Fact]
    public void DefaultCurve_IsTheDocumentedRamp() =>
        Assert.Equal(new[] { 30, 45, 60, 80, 100 }, Fan.DefaultCurve);

    [Fact]
    public void DefaultCurve_HasOneDutyPerAnchor_AndIsMonotonic()
    {
        Assert.Equal(Fan.Anchors.Length, Fan.DefaultCurve.Length);
        for (var i = 1; i < Fan.DefaultCurve.Length; i++)
            Assert.True(Fan.DefaultCurve[i] > Fan.DefaultCurve[i - 1],
                $"default ramp must rise strictly: index {i}");
    }

    [Fact]
    public void DefaultCurve_StaysInsideTheDutyRange() =>
        Assert.All(Fan.DefaultCurve, d => Assert.InRange(d, 0, 100));
}

/// <summary>
/// <see cref="Fan.Duty"/> — the curve evaluation behind every Custom-mode fan write. The interpolation itself
/// is private to <see cref="Fan"/>, so these cases drive it the only way production does: a preset whose CPU
/// half holds the curve, plus this fan's own committed memory. The arithmetic pinned below is unchanged by
/// that re-pointing; only the door in.
/// Silent-failure territory: a wrong value here is a plausible-looking duty% written to the EC, with no
/// exception and no log line. Everything below is derived from the source, not from the prose doc.
/// </summary>
public class FanCurveEvalCurveTests
{
    // A curve whose duties are all distinct from the built-in default, so a test that accidentally falls
    // back to DefaultCurve fails instead of coincidentally passing.
    private static readonly int[] Ramp = [10, 20, 30, 40, 50];

    // The stored curve is declared non-nullable (FanPreset.CpuCurve), but Fan itself tolerates null (and a
    // too-short array), so the tests hand it one through `duties!` rather than pretending it cannot happen —
    // a deserialised or hand-edited settings file can produce exactly this.
    //
    // The commit is what a caller does after a successful write, and the curve is stored the way
    // LaptopService.SetFanCurve stores it: verbatim, with no length validation, which is what keeps the
    // over-long cases below reachable. `fallback` is the model's `LastApplied`: a negative one means nothing
    // was ever committed, so nothing is committed here either — Fan's own sentinel for that state is -1.
    private static int Eval(int[]? duties, int temp, int fallback = -1)
    {
        var fan = new Fan(gpu: false);
        if (fallback >= 0) fan.Commit(fallback);
        return fan.Duty(new FanPreset { CpuUseCurve = true, CpuCurve = duties! }, temp);
    }

    // ---- the anchors themselves: a curve must reproduce its own points exactly ----

    [Theory]
    [InlineData(50, 30)]
    [InlineData(60, 45)]
    [InlineData(70, 60)]
    [InlineData(80, 80)]
    [InlineData(90, 100)]
    public void Anchors_EvaluateToTheDefaultCurveDuty(int temp, int expected) =>
        Assert.Equal(expected, Eval(null, temp));

    [Theory]
    [InlineData(50, 10)]
    [InlineData(60, 20)]
    [InlineData(70, 30)]
    [InlineData(80, 40)]
    [InlineData(90, 50)]
    public void Anchors_EvaluateToTheSuppliedCurveDuty(int temp, int expected) =>
        Assert.Equal(expected, Eval(Ramp, temp));

    // ---- interpolation between anchors, on the default ramp ----
    // The expression is (d1 - d0) * (temp - a0) / (a1 - a0) in int arithmetic, so it truncates toward zero.
    // Every expected value below is that arithmetic done by hand.

    [Theory]
    [InlineData(51, 31)]   // 30 + 15*1/10 = 30 + 1
    [InlineData(52, 33)]   // 30 + 15*2/10 = 30 + 3
    [InlineData(53, 34)]   // 30 + 15*3/10 = 30 + 4
    [InlineData(54, 36)]   // 30 + 15*4/10 = 30 + 6
    [InlineData(55, 37)]   // 30 + 15*5/10 = 30 + 7   (midpoint, and NOT 37.5)
    [InlineData(59, 43)]   // 30 + 15*9/10 = 30 + 13
    [InlineData(61, 46)]
    [InlineData(65, 52)]   // 45 + 15*5/10 = 45 + 7
    [InlineData(66, 54)]
    [InlineData(69, 58)]   // 45 + 15*9/10 = 45 + 13
    [InlineData(71, 62)]   // 60 + 20*1/10
    [InlineData(75, 70)]   // 60 + 20*5/10 = 60 + 10
    [InlineData(79, 78)]   // 60 + 20*9/10 = 60 + 18
    [InlineData(81, 82)]
    [InlineData(85, 90)]   // 80 + 20*5/10 = 80 + 10
    [InlineData(89, 98)]   // 80 + 20*9/10 = 80 + 18
    public void InterpolatesLinearlyBetweenAnchors(int temp, int expected) =>
        Assert.Equal(expected, Eval(null, temp));

    [Theory]
    [InlineData(51, 11)]
    [InlineData(55, 15)]   // 10 + 10*5/10
    [InlineData(59, 19)]
    [InlineData(65, 25)]
    [InlineData(74, 34)]   // 30 + 10*4/10
    [InlineData(89, 49)]   // 40 + 10*9/10
    public void InterpolatesTheSuppliedCurve_NotTheDefault(int temp, int expected) =>
        Assert.Equal(expected, Eval(Ramp, temp));

    /// <summary>Integer truncation can make a segment that spans fewer units than its span produce NO change
    /// at some temperatures — a silently flat stretch inside a rising curve. Documented, not asserted as
    /// desirable: the +3 span over 10 °C only reaches +1 at the midpoint and +0 one step in.</summary>
    [Theory]
    [InlineData(53, 50)]   // 50 + 3*3/10 = 50 + 0
    [InlineData(55, 51)]   // 50 + 3*5/10 = 50 + 1
    [InlineData(59, 52)]   // 50 + 3*9/10 = 50 + 2
    public void TruncatingDivision_FlattensAShallowSegment(int temp, int expected) =>
        Assert.Equal(expected, Eval([50, 53, 0, 0, 0], temp));

    // ---- flat beyond the ends: out of range inputs must clamp to the end duty, never extrapolate ----

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(0, 30)]      // cold start; the first anchor is the floor
    [InlineData(25, 30)]
    [InlineData(49, 30)]
    [InlineData(90, 100)]
    [InlineData(91, 100)]
    [InlineData(100, 100)]
    [InlineData(1000, 100)]
    [InlineData(int.MaxValue, 100)]
    public void FlatBeyondTheEnds_OnTheDefaultCurve(int temp, int expected) =>
        Assert.Equal(expected, Eval(null, temp));

    [Theory]
    [InlineData(49, 10)]
    [InlineData(50, 10)]
    [InlineData(90, 50)]
    [InlineData(200, 50)]
    public void FlatBeyondTheEnds_OnTheSuppliedCurve(int temp, int expected) =>
        Assert.Equal(expected, Eval(Ramp, temp));

    // ---- unknown temperature (-1 and below): hold the last committed duty, else the idle duty ----

    [Fact]
    public void NegativeTemperature_WithNoCommittedDuty_FallsBackToTheFirstAnchorDuty() =>
        Assert.Equal(30, Eval(null, -1, fallback: -1));

    [Fact]
    public void NegativeTemperature_OnASuppliedCurve_FallsBackToThatCurvesFirstDuty() =>
        Assert.Equal(10, Eval(Ramp, -1, fallback: -1));

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(100)]
    public void NegativeTemperature_WithACommittedDuty_HoldsThatDuty(int fallback) =>
        Assert.Equal(fallback, Eval(Ramp, -1, fallback));

    [Fact]
    public void NegativeTemperature_FallbackWinsOverTheCurve_EvenWhenTheCurveWouldSayOtherwise() =>
        // The curve's first duty is 10; the committed duty is 88. The committed duty must win: reporting 10
        // would drop the fans to idle for a single unreadable sample.
        Assert.Equal(88, Eval(Ramp, -1, fallback: 88));

    [Theory]
    [InlineData(-2)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void AnyNegativeTemperature_IsTreatedAsUnknown(int temp) =>
        Assert.Equal(77, Eval(Ramp, temp, fallback: 77));

    // ---- degenerate / malformed curves fall back to the built-in default rather than reading garbage ----

    [Fact]
    public void NullCurve_UsesTheDefaultRamp()
    {
        Assert.Equal(30, Eval(null, 50));
        Assert.Equal(37, Eval(null, 55));
        Assert.Equal(100, Eval(null, 90));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void CurveShorterThanTheAnchors_UsesTheDefaultRamp(int length)
    {
        var shortCurve = new int[length];
        for (var i = 0; i < length; i++) shortCurve[i] = 99;   // must never be read
        Assert.Equal(30, Eval(shortCurve, 50));
        Assert.Equal(37, Eval(shortCurve, 55));
        Assert.Equal(100, Eval(shortCurve, 90));
    }

    [Fact]
    public void CurveOfExactlyAnchorLength_IsUsed()
    {
        Assert.Equal(10, Eval(Ramp, 50));
        Assert.Equal(50, Eval(Ramp, 90));
    }

    /// <summary>An over-long curve is ACCEPTED — only <c>Length &lt; Anchors.Length</c> falls back to the
    /// default — so every anchor-indexed read must stay inside the first <c>Anchors.Length</c> entries.
    /// The interpolation loop and the low-end clamp do. The high-end clamp does not (next test).</summary>
    [Fact]
    public void CurveLongerThanTheAnchors_InterpolationStillUsesTheFirstAnchorCountEntries()
    {
        Assert.Equal(10, Eval([10, 20, 30, 40, 50, 999], 50));   // temp <= a[0] -> duties[0]
        Assert.Equal(15, Eval([10, 20, 30, 40, 50, 999], 55));   // loop reads duties[0], duties[1]
        Assert.Equal(25, Eval([10, 20, 30, 40, 50, 999], 65));
        Assert.Equal(49, Eval([10, 20, 30, 40, 50, 999], 89));
    }

    /// <summary>OBSERVED CURRENT behaviour — an open question, NOT a spec and NOT intended behaviour.
    ///
    /// What the code does right now (the flat-top branch of <see cref="Fan.Duty"/>'s curve evaluation):
    ///
    ///     if (temp &gt;= a[^1]) return Math.Clamp(duties[^1], 0, 100);
    ///
    /// Every other anchor-indexed read in that evaluation uses an ANCHOR's index: <c>duties[0]</c> for
    /// the cold clamp (twice), <c>duties[i - 1]</c>/<c>duties[i]</c> in the loop. The "at or above the
    /// last anchor" branch instead reads <c>duties[^1]</c> — the last element of the ARRAY. The guard
    /// <c>duties.Length &lt; Anchors.Length</c> rejects only a SHORT curve, so a curve longer than the five
    /// anchors takes the flat top of the fan curve from an entry that belongs to no anchor:
    ///
    ///   input:   duties = [10,20,30,40,50,999],  temp = 90 °C (exactly the last anchor)
    ///   current: 100 — clamp(duties[5] = 999)
    ///   the value every other read in the method implies: 50 — the duty at the last anchor
    ///
    /// Invisible for a well-formed five-long curve, because the array index and the anchor index coincide
    /// there. Reachable only from a hand-edited settings.json: the UI always writes five entries
    /// (FansViewModel.Duties). A latent trap, not a live defect.
    ///
    /// STILL REACHABLE from the public surface, which is why this pin survived <c>EvalCurve</c> becoming
    /// private: <see cref="FanPreset.CpuCurve"/> is a public settable array with no length validation, and
    /// <c>LaptopService.SetFanCurve</c> stores whatever it is handed, so a six-entry curve is a preset's
    /// ordinary contents rather than a private call. The case below is unreachable only from the UI's own
    /// editor, never from the model.
    ///
    /// The production/test mismatch is an OPEN DECISION recorded in docs/open-decisions.md, section
    /// "Известные особенности (решение ожидается)", item 2. This case is deliberately left live — not
    /// skipped, not deleted — so whoever fixes the index, or instead tightens the guard to fall back
    /// to <see cref="Fan.DefaultCurve"/>, gets an immediate signal here.
    /// </summary>
    [Fact]
    public void CurveLongerThanTheAnchors_AboveTheLastAnchor_CurrentlyUsesTheLastArrayEntry_OpenQuestion()
    {
        Assert.Equal(100, Eval([10, 20, 30, 40, 50, 999], 90));
        Assert.Equal(100, Eval([10, 20, 30, 40, 50, 999], 120));
        Assert.Equal(100, Eval([10, 20, 30, 40, 50, 999], 1000));
    }

    /// <summary>The same over-long case for the unknown-temperature fallback, which is unaffected because it
    /// reads <c>duties[0]</c> and never <c>duties[^1]</c> — included so the asymmetry is on the record.</summary>
    [Fact]
    public void CurveLongerThanTheAnchors_UnknownTemperatureUsesTheFirstEntry()
    {
        Assert.Equal(10, Eval([10, 20, 30, 40, 50, 999], -1, fallback: -1));
    }

    /// <summary>A curve the user flattened to one number everywhere. Every temperature, including the
    /// unknown one with nothing committed, must return that number.</summary>
    [Theory]
    [InlineData(-1, 42)]
    [InlineData(0, 42)]
    [InlineData(50, 42)]
    [InlineData(55, 42)]
    [InlineData(70, 42)]
    [InlineData(89, 42)]
    [InlineData(90, 42)]
    [InlineData(120, 42)]
    public void AllEqualCurve_ReturnsThatDutyEverywhere(int temp, int expected) =>
        Assert.Equal(expected, Eval([42, 42, 42, 42, 42], temp));

    [Fact]
    public void AllZeroCurve_ReturnsZeroEverywhere()
    {
        Assert.Equal(0, Eval([0, 0, 0, 0, 0], 50));
        Assert.Equal(0, Eval([0, 0, 0, 0, 0], 70));
        Assert.Equal(0, Eval([0, 0, 0, 0, 0], 200));
    }

    /// <summary>A non-monotonic curve (the user's drag can produce one). The engine does not smooth or
    /// reorder it: each segment is interpolated on its own terms, so a falling segment falls. That is the
    /// intended behaviour — the values below are the arithmetic, hand-checked.</summary>
    [Theory]
    [InlineData(50, 100)]
    [InlineData(55, 60)]    // 100 + (20-100)*5/10 = 100 - 40
    [InlineData(60, 20)]
    [InlineData(65, 55)]    // 20 + (90-20)*5/10 = 20 + 35
    [InlineData(70, 90)]
    [InlineData(75, 50)]    // 90 + (10-90)*5/10 = 90 - 40
    [InlineData(80, 10)]
    [InlineData(85, 35)]    // 10 + (60-10)*5/10 = 10 + 25
    [InlineData(90, 60)]
    [InlineData(100, 60)]
    public void NonMonotonicCurve_InterpolatesEachSegmentIndependently(int temp, int expected) =>
        Assert.Equal(expected, Eval([100, 20, 90, 10, 60], temp));

    // ---- the result is a duty%, so it is always clamped into 0..100 ----

    [Theory]
    [InlineData(50, 100)]   // first duty 150 -> 100
    [InlineData(45, 100)]
    [InlineData(60, 0)]     // second duty -20 -> 0
    [InlineData(90, 100)]   // last duty 200 -> 100
    [InlineData(400, 100)]
    public void OutOfRangeDuties_AreClampedIntoTheDutyRange(int temp, int expected) =>
        Assert.Equal(expected, Eval([150, -20, 60, 80, 200], temp));

    [Fact]
    public void OutOfRangeFirstDuty_IsClampedOnTheUnknownTemperatureFallbackToo() =>
        Assert.Equal(100, Eval([150, -20, 60, 80, 200], -1, fallback: -1));

    [Fact]
    public void EveryResultIsInsideTheDutyRange_AcrossTheWholeTemperatureSweep()
    {
        int[]?[] curves = [null, [], [50, 53, 0, 0, 0], [150, -20, 60, 80, 200], [0, 0, 0, 0, 0],
                           [100, 100, 100, 100, 100], [42, 42, 42, 42, 42], Ramp];
        foreach (var curve in curves)
            for (var temp = -10; temp <= 130; temp++)
                Assert.InRange(Eval(curve, temp), 0, 100);
    }
}
