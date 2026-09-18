using AcerHelper.Domain;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="FanCurveEngine.Step"/> — the HYSTERESIS contract. This is the part that silently rots: the
/// deadband decides whether a duty is written to the EC at all, so an off-by-one here produces fans that
/// hunt (writing every poll) or fans that freeze (never writing). Neither throws, neither logs.
///
/// Source of truth, verbatim from the engine:
///     if (_cpu >= 0 &amp;&amp; Math.Abs(cpu - _cpu) &lt; 4 &amp;&amp; Math.Abs(gpu - _gpu) &lt; 4) return null;
/// so the deadband is STRICTLY less than 4: a delta of 3 is suppressed, a delta of 4 is not.
/// </summary>
public class FanCurveEngineStepTests
{
    // Fixed-speed fans, so the expected duty is exactly the number the test supplies and the deadband is
    // the only thing under test. Temps are irrelevant for these fans and are left unset (-1). Each fan is
    // handed its own settings — curve off, its fixed duty — because that is all a fan is given now.
    //
    // "Curve off" is still a fan WITH a curve: a FanSettings cannot hold an empty one (Domain/Fan.cs), and the
    // default ramp is what a real fixed-speed fan carries in its unused half — the field is simply never read.
    private static (FanSettings cpu, FanSettings gpu) Fixed(int cpu, int gpu) =>
        (new FanSettings(false, Fan.DefaultDuties(), cpu), new FanSettings(false, Fan.DefaultDuties(), gpu));

    private static SensorSnapshot NoTemps { get; } = new();

    private static (FanSettings cpu, FanSettings gpu) Curved(int[] cpuCurve, int gpu) =>
        (new FanSettings(true, cpuCurve, 0), new FanSettings(false, Fan.DefaultDuties(), gpu));

    // ---- no committed state: the first Step after construction or Reset must always apply ----

    [Fact]
    public void FirstStep_OnAFreshEngine_AlwaysReturnsADuty()
    {
        var engine = new FanCurveEngine();
        Assert.Equal((70, 70), engine.Step(Fixed(70, 70), NoTemps));
    }

    [Fact]
    public void Reset_ClearsTheHysteresis_SoTheNextStepAppliesUnconditionally()
    {
        var engine = new FanCurveEngine();
        engine.Commit(50, 50);

        Assert.Null(engine.Step(Fixed(51, 50), NoTemps));   // inside the deadband

        engine.Reset();

        // The whole point of Reset: a mode/preset switch must not have its first legitimate write
        // swallowed by a deadband that refers to the previous mode's duties.
        Assert.Equal((51, 50), engine.Step(Fixed(51, 50), NoTemps));
    }

    // ---- the deadband boundary, from BOTH sides, on each fan ----
    // delta is (new duty - last committed duty). Suppressed means Step returns null.

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]     // exactly 4 units away: MUST apply
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(-1, false)]
    [InlineData(-3, false)]
    [InlineData(-4, true)]    // the same boundary on the falling side
    [InlineData(-5, true)]
    [InlineData(-10, true)]
    public void CpuDeadbandBoundary_IsStrictlyLessThanFour(int delta, bool shouldApply)
    {
        var engine = new FanCurveEngine();
        engine.Commit(50, 50);

        var result = engine.Step(Fixed(50 + delta, 50), NoTemps);

        if (shouldApply) Assert.Equal((50 + delta, 50), result);
        else Assert.Null(result);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(-1, false)]
    [InlineData(-3, false)]
    [InlineData(-4, true)]
    [InlineData(-5, true)]
    [InlineData(-10, true)]
    public void GpuDeadbandBoundary_IsStrictlyLessThanFour(int delta, bool shouldApply)
    {
        var engine = new FanCurveEngine();
        engine.Commit(50, 50);

        var result = engine.Step(Fixed(50, 50 + delta), NoTemps);

        if (shouldApply) Assert.Equal((50, 50 + delta), result);
        else Assert.Null(result);
    }

    [Fact]
    public void EitherFanLeavingTheDeadband_AppliesBoth()
    {
        var engine = new FanCurveEngine();
        engine.Commit(50, 50);

        // CPU is inside its deadband (1), GPU is outside (10). The PAIR is returned, and the caller writes
        // both fans — including the one that did not need to move.
        Assert.Equal((51, 60), engine.Step(Fixed(51, 60), NoTemps));
    }

    [Fact]
    public void BothFansInsideTheirDeadbands_IsTheOnlyCaseThatReturnsNull()
    {
        var engine = new FanCurveEngine();
        engine.Commit(50, 50);

        Assert.Null(engine.Step(Fixed(47, 53), NoTemps));   // -3 and +3
        Assert.Equal((46, 60), engine.Step(Fixed(46, 60), NoTemps));   // -4 wins over +0
    }

    // ---- state advances only through Commit: a failed hardware write must be retried, not swallowed ----

    [Fact]
    public void Step_DoesNotAdvanceState_TwoIdenticalStepsReturnTheSameDuty()
    {
        var engine = new FanCurveEngine();
        engine.Commit(50, 50);

        Assert.Equal((60, 50), engine.Step(Fixed(60, 50), NoTemps));
        Assert.Equal((60, 50), engine.Step(Fixed(60, 50), NoTemps));
        Assert.Equal((60, 50), engine.Step(Fixed(60, 50), NoTemps));
    }

    /// <summary>The documented recovery path for a failed EC write: the caller only calls
    /// <see cref="FanCurveEngine.Commit"/> on success, so an uncommitted duty is re-offered on the next poll
    /// instead of being treated as the new baseline.</summary>
    [Fact]
    public void UncommittedDuty_IsReOfferedOnEveryStep_UntilItIsCommitted()
    {
        var engine = new FanCurveEngine();
        engine.Commit(50, 50);

        Assert.Equal((54, 50), engine.Step(Fixed(54, 50), NoTemps));   // write fails; deliberately not committed
        Assert.Equal((54, 50), engine.Step(Fixed(54, 50), NoTemps));   // still offered

        engine.Commit(54, 50);

        Assert.Null(engine.Step(Fixed(54, 50), NoTemps));              // now it is the baseline
    }

    [Fact]
    public void AfterCommit_TheCommittedDutyBecomesTheDeadbandReference()
    {
        var engine = new FanCurveEngine();
        engine.Commit(50, 50);
        engine.Commit(80, 80);        // overwrites, not merges

        Assert.Null(engine.Step(Fixed(83, 83), NoTemps));
        Assert.Equal((84, 84), engine.Step(Fixed(84, 84), NoTemps));
    }

    // ---- off-curve fans: the fixed speed is clamped into the duty range ----

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-100, 0)]
    [InlineData(0, 0)]
    [InlineData(70, 70)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(255, 100)]
    [InlineData(int.MaxValue, 100)]
    public void FixedSpeedIsClampedIntoTheDutyRange(int preset, int expected)
    {
        var engine = new FanCurveEngine();
        var result = engine.Step(Fixed(preset, preset), NoTemps);
        Assert.Equal((expected, expected), result);
    }

    // ---- curve fans through Step: the unknown-temperature fallback is the last COMMITTED duty ----

    [Fact]
    public void CurveFan_WithAnUnreadableTempAndNothingCommitted_UsesTheCurvesIdleDuty()
    {
        var engine = new FanCurveEngine();
        var result = engine.Step(Curved([10, 20, 30, 40, 50], gpu: 30), new SensorSnapshot { CpuTempC = -1, GpuTempC = 50 });
        Assert.Equal((10, 30), result);
    }

    [Fact]
    public void CurveFan_WithAnUnreadableTemp_HoldsTheLastCommittedDuty_AndSuppressesTheStep()
    {
        var engine = new FanCurveEngine();
        engine.Commit(30, 30);

        var result = engine.Step(Curved([10, 20, 30, 40, 50], gpu: 30), new SensorSnapshot { CpuTempC = -1, GpuTempC = 50 });

        // Holding 30 equals the committed 30, so nothing is written. This is the documented behaviour, and
        // it is also the reason a single unreadable sample cannot drop the fans to idle.
        Assert.Null(result);
    }

    [Fact]
    public void CurveFan_AtALiveTemperature_DrivesTheDutyFromTheCurve()
    {
        var engine = new FanCurveEngine();
        engine.Commit(30, 30);

        var result = engine.Step(Curved([10, 20, 30, 40, 50], gpu: 30), new SensorSnapshot { CpuTempC = 55, GpuTempC = 50 });

        Assert.Equal((15, 30), result);   // 10 + 10*5/10
    }

    /// <summary>REWRITTEN, and the assertion it used to make is gone on purpose. This case was
    /// "an empty curve falls back to the default ramp rather than to zero" and it drove the engine with
    /// <c>Curved([], …)</c> — an empty curve, which <see cref="FanSettings"/> now REFUSES. The tolerance it
    /// pinned was a property of the evaluator, and the evaluator's tolerance is not what the model's rule is:
    /// a curve is one duty% per anchor, and a fan whose curve has been wiped by hand is repaired before it gets
    /// here (the load-time sanitiser, Infrastructure/Composition/JsonSettingsStore.cs, which is where an empty
    /// or malformed stored curve becomes the default ramp and the file is rewritten). So the refusal below is
    /// the whole of the old case's subject, and the ramp it produced is asserted where it is now decided — in
    /// the sanitiser's own tests.
    ///
    /// MUTATION THAT REDDENS IT: removing the <c>Fan.IsValidCurve</c> guard from <c>FanSettings</c>'s
    /// constructor, after which nothing throws and every <c>Assert.Throws</c> here fails.</summary>
    [Fact]
    public void AnEmptyOrNullCurve_IsRefusedByTheModel_NotFallenBackFrom()
    {
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [], 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, null!, 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [10, 20, 30], 0));
    }

    [Fact]
    public void GpuCurveIsEvaluatedIndependentlyOfTheCpuCurve()
    {
        var engine = new FanCurveEngine();
        var fans = (cpu: new FanSettings(true, [10, 20, 30, 40, 50], 0),
                    gpu: new FanSettings(true, [90, 80, 70, 60, 50], 0));

        // CPU at 60 °C -> 20; GPU at 60 °C -> 80. The two curves must not share state.
        var result = engine.Step(fans, new SensorSnapshot { CpuTempC = 60, GpuTempC = 60 });
        Assert.Equal((20, 80), result);
    }

    /// <summary>REWRITTEN. This used to build a fan from a curve whose points were out of range
    /// (<c>[150, -20, 60, 80, 200]</c>) and assert that the engine still produced a byte-safe duty — the
    /// evaluator's clamp, standing in for a rule about the curve. The rule is the type's: a duty is 0..100, so a
    /// curve carrying 150 is not a curve, and the model refuses to hold one rather than relying on every reader
    /// of it to clamp. What the reader does is asserted here too, on a curve the model admits, so the clamp is
    /// not lost with the tolerance.</summary>
    [Fact]
    public void OutOfRangeCurvePoints_AreNotACurve_TheModelRefusesThem()
    {
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [150, -20, 60, 80, 200], 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [0, 0, 0, 0, 101], 0));
        Assert.Throws<ArgumentException>(() => new FanSettings(true, [0, 0, 0, 0, -1], 0));

        // ...and the duty the engine hands the EC is still a duty%: the fixed speed is clamped by the same type.
        var engine = new FanCurveEngine();
        var fans = (cpu: new FanSettings(true, [100, 100, 100, 100, 100], 0),
                    gpu: new FanSettings(false, Fan.DefaultDuties(), 300));

        Assert.Equal((100, 100), engine.Step(fans, new SensorSnapshot { CpuTempC = 50, GpuTempC = 50 }));
    }
}
