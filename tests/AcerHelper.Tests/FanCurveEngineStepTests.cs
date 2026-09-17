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
    private static (FanSettings cpu, FanSettings gpu) Fixed(int cpu, int gpu) =>
        (new FanSettings(false, [], cpu), new FanSettings(false, [], gpu));

    private static SensorSnapshot NoTemps { get; } = new();

    private static (FanSettings cpu, FanSettings gpu) Curved(int[] cpuCurve, int gpu) =>
        (new FanSettings(true, cpuCurve, 0), new FanSettings(false, [], gpu));

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

    [Fact]
    public void CurveFan_WithAnEmptyCurve_FallsBackToTheDefaultRampRatherThanZero()
    {
        var engine = new FanCurveEngine();
        engine.Commit(0, 30);

        var result = engine.Step(Curved([], gpu: 30), new SensorSnapshot { CpuTempC = 70, GpuTempC = 50 });

        Assert.Equal((60, 30), result);   // the default ramp's duty at 70 °C, NOT 0
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

    /// <summary>The returned duty always feeds the EC as a byte, so it must be a duty% even for a curve
    /// whose stored points are out of range (an old settings file, a bad drag).</summary>
    [Fact]
    public void OutOfRangeCurvePoints_StillProduceAByteSafeDuty()
    {
        var engine = new FanCurveEngine();
        var fans = (cpu: new FanSettings(true, [150, -20, 60, 80, 200], 0),
                    gpu: new FanSettings(false, [], 0));

        var result = engine.Step(fans, new SensorSnapshot { CpuTempC = 50, GpuTempC = 50 });

        Assert.Equal((100, 0), result);   // clamp(150) = 100
    }
}
