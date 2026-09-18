using System.Collections.Immutable;
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

    /// <summary>The two arrays are IMMUTABLE to callers, which is what makes the "single source of truth" above
    /// hold: they used to be <c>public static readonly int[]</c>, so the reference was frozen and the ELEMENTS
    /// were not — <c>Fan.Anchors[0] = -999</c> compiled and ran, and UI/ViewModels/FansViewModel.cs held these
    /// very instances, so one write would have moved the graph the user drags and the anchors the controller
    /// interpolates together. Nothing wrote to them, which is why this is a rule and not a bug report, and why it
    /// is asserted rather than assumed.
    ///
    /// MUTATION THAT REDDENS IT: declaring either field <c>int[]</c> again — this line then does not compile, and
    /// the mutation a compiler cannot stop (a write through the array) is back.</summary>
    [Fact]
    public void TheAnchorsAndTheDefaultRamp_AreImmutableAndIndexable()
    {
        Assert.Equal(typeof(ImmutableArray<int>), typeof(Fan).GetField(nameof(Fan.Anchors))!.FieldType);
        Assert.Equal(typeof(ImmutableArray<int>), typeof(Fan).GetField(nameof(Fan.DefaultCurve))!.FieldType);

        Assert.Equal(50, Fan.Anchors[0]);                    // still indexable by the readers on both sides
        Assert.Equal(90, Fan.Anchors[Fan.Anchors.Length - 1]);
        Assert.Equal(Fan.Anchors.Length, Fan.DefaultDuties().Length);
    }

    /// <summary><see cref="Fan.DefaultDuties"/> hands out a MUTABLE copy, which is what a stored preset and the
    /// sanitiser need — a preset's curve field is an array a later edit writes into, and it must not be the shared
    /// ramp. Two calls are two arrays, so one holder cannot write into another's.</summary>
    [Fact]
    public void DefaultDuties_IsAFreshMutableCopyEachTime()
    {
        var first = Fan.DefaultDuties();
        var second = Fan.DefaultDuties();

        Assert.Equal(new[] { 30, 45, 60, 80, 100 }, first);
        Assert.NotSame(first, second);

        first[0] = 7;
        Assert.Equal(30, second[0]);
        Assert.Equal(30, Fan.DefaultCurve[0]);   // ...and the ramp itself is untouched
    }

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

    // The built-in ramp, as the fan that is not on a custom one actually carries it. Every case below that used
    // to read `Eval(null, …)` — "a fan with no curve, so the default ramp" — now says so by handing the ramp over:
    // the array is not optional data, because a FanSettings holds a real curve or it does not exist
    // (Domain/Fan.cs). The tolerance that used to live in the evaluator is now the load-time sanitiser's, where
    // a malformed stored curve is replaced and the file rewritten, and it is refused here.
    private static int[] Default => Fan.DefaultDuties();

    // The commit is what a caller does after a successful write. `fallback` is the model's `LastApplied`: a
    // negative one means nothing was ever committed, so nothing is committed here either — Fan's own sentinel for
    // that state is -1. The fixed duty is 0 because the curve is on: Fan reads it only when the curve is off.
    private static int Eval(int[] duties, int temp, int fallback = -1)
    {
        var fan = new Fan(new FanSettings(true, duties, 0));
        if (fallback >= 0) fan.Commit(fallback);
        return fan.Duty(temp);
    }

    // ---- the anchors themselves: a curve must reproduce its own points exactly ----

    [Theory]
    [InlineData(50, 30)]
    [InlineData(60, 45)]
    [InlineData(70, 60)]
    [InlineData(80, 80)]
    [InlineData(90, 100)]
    public void Anchors_EvaluateToTheDefaultCurveDuty(int temp, int expected) =>
        Assert.Equal(expected, Eval(Default, temp));

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
        Assert.Equal(expected, Eval(Default, temp));

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
        Assert.Equal(expected, Eval(Default, temp));

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
        Assert.Equal(30, Eval(Default, -1, fallback: -1));

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

    // ---- a curve that is not a curve is REFUSED, not tolerated ----
    //
    // THIS SECTION REPLACES FOUR CASES AND AN OPEN QUESTION. It used to hold "a null curve falls back to the
    // default ramp", "a shorter-than-the-anchors curve falls back to the default ramp", and — as the open
    // question docs/open-decisions.md recorded as item 2 — "an OVER-LONG curve is accepted, and above the last
    // anchor its LAST ARRAY ENTRY is used as the flat top", which was `duties[^1]` on an array whose length no
    // longer matched the anchors. All four pinned the evaluator's tolerance, and the tolerance is no longer a
    // property of the evaluator: a curve is one duty% per anchor, the type refuses anything else at
    // construction, and a malformed curve ALREADY ON DISK is repaired by the load-time sanitiser
    // (Infrastructure/Composition/JsonSettingsStore.cs) before the model is built. The over-long case in
    // particular does not survive as a behaviour at all — its whole subject was a curve the model now cannot
    // hold — so the assertion that replaced it is the refusal.

    /// <summary>A curve that is null, or is not exactly one duty% per anchor, is refused by the model. This is
    /// the case that makes <c>EvalCurve</c>'s fallback line unreachable, and it is deliberately the ONLY place
    /// the old tolerance's subjects appear: the repair that keeps a user's bad file from reaching here is the
    /// sanitiser's, and it is asserted there, against the file it rewrites.</summary>
    [Fact]
    public void ACurveThatIsNotOneDutyPerAnchor_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new FanSettings(true, null!, 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [], 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, new int[4], 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [10, 20, 30, 40, 50, 999], 0));
    }

    [Fact]
    public void CurveOfExactlyAnchorLength_IsUsed()
    {
        Assert.Equal(10, Eval(Ramp, 50));
        Assert.Equal(50, Eval(Ramp, 90));
    }

    /// <summary>The flat top of a well-formed curve is the duty at the LAST ANCHOR — the property the over-long
    /// case above used to violate. With the array exactly as long as the anchors there is no index at which the
    /// "last array entry" and the "duty at the last anchor" can differ, and this states it on a curve whose end
    /// duty is not the ramp's, so a read of the wrong element cannot coincide.</summary>
    [Theory]
    [InlineData(90, 50)]     // exactly the last anchor
    [InlineData(120, 50)]    // ...and everything above it
    [InlineData(1000, 50)]
    public void AboveTheLastAnchor_TheDutyIsTheLastAnchors(int temp, int expected) =>
        Assert.Equal(expected, Eval(Ramp, temp));

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

    // ---- a duty outside 0..100 is not a duty, so it is refused rather than clamped on the way out ----

    /// <summary>REWRITTEN. This used to build the evaluator's input as <c>[150, -20, 60, 80, 200]</c> and assert
    /// that the duties it produced were clamped into range — a tolerance of values that are not duties. The
    /// clamp still exists in <see cref="Fan.Duty"/> as a last resort (the EC takes a byte, and that must not
    /// depend on one guard holding), but the values it used to be handed cannot reach it: the curve is refused,
    /// so no reader has to clamp what it reads. The clamp itself is asserted on a curve that IS a curve, below.</summary>
    [Fact]
    public void OutOfRangeDuties_AreNotACurve_TheyAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [150, -20, 60, 80, 200], 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [30, 45, 60, 80, 101], 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [30, 45, 60, 80, -1], 0));
    }

    /// <summary>The clamp that protects the EC, on the two edges a curve can legitimately have. A curve of all
    /// zeroes holds the fans still; a curve of all hundreds runs them flat out; neither is out of range, and the
    /// value written is a byte either way.</summary>
    [Fact]
    public void TheDutyWritten_IsAlwaysInsideTheRange_ForEveryCurveACurveCanBe()
    {
        int[][] curves = [[50, 53, 0, 0, 0], [0, 0, 0, 0, 0], [100, 100, 100, 100, 100],
                          [42, 42, 42, 42, 42], Ramp, Default];
        foreach (var curve in curves)
            for (var temp = -10; temp <= 130; temp++)
                Assert.InRange(Eval(curve, temp), 0, 100);
    }
}
