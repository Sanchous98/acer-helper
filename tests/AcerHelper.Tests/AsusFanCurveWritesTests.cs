using AcerHelper.Infrastructure.Vendors.Asus;

namespace AcerHelper.Tests;

/// <summary>
/// The Phase-3 FanCurves WRITE path: the pure validation/encoding, the exact method argument lists (verbatim),
/// and the consent-gated, single-flight writer over a recorded busctl runner. No asusd, no D-Bus, no hardware.
///
/// The wire shape is asusd's: FanCurvePU is a WORD, the two byte arrays carry their own length, and the whole
/// CurveData is a struct inside the method signature — which is why the signature is
/// <c>&lt;profile&gt;(sayayb)</c> and the values are flattened.
/// </summary>
public class AsusFanCurveWritesTests
{
    private sealed class FakeBus
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], (int code, string output)> Answer { get; set; } = _ => (0, "");
        public (int code, string output) Call(string[] args) { Calls.Add(args); return Answer(args); }
        public string[]? LastCall => Calls.LastOrDefault();
    }

    private static AsusFanCurve Curve(AsusFan fan = AsusFan.Cpu, bool enabled = false)
        => new(fan,
               [new(30, 10), new(40, 20), new(50, 30), new(60, 40),
                new(70, 50), new(80, 60), new(90, 70), new(100, 80)],
               enabled);

    // ---- validation ----

    [Fact]
    public void AEightPointCurveIsValid()
    {
        Assert.True(AsusFanCurveCodec.Validate(Curve()).ok);
        Assert.True(AsusFanCurveCodec.Validate(Curve(enabled: true)).ok);
    }

    [Fact]
    public void ACurveWithTheWrongPointCountIsRefused()
    {
        var curve = new AsusFanCurve(AsusFan.Cpu, [new(30, 10), new(40, 20)], false);
        Assert.Equal(AsusFanCurveMessages.EightPoints, AsusFanCurveCodec.Validate(curve).message);
    }

    [Fact]
    public void ADecreasingTemperatureIsRefused()
    {
        var curve = new AsusFanCurve(AsusFan.Cpu,
            [new(50, 10), new(40, 20), new(50, 30), new(60, 40), new(70, 50), new(80, 60), new(90, 70), new(100, 80)],
            false);
        Assert.Equal(AsusFanCurveMessages.Order, AsusFanCurveCodec.Validate(curve).message);
    }

    [Fact]
    public void ASpeedAboveOneHundredPercentIsRefused()
    {
        var curve = new AsusFanCurve(AsusFan.Cpu,
            [new(30, 10), new(40, 20), new(50, 30), new(60, 40), new(70, 50), new(80, 60), new(90, 70), new(100, 200)],
            false);
        Assert.Equal(AsusFanCurveMessages.Range, AsusFanCurveCodec.Validate(curve).message);
    }

    [Fact]
    public void AnUnknownFanIsRefused()
    {
        Assert.Equal(AsusFanCurveMessages.UnknownFan, AsusFanCurveCodec.Validate(Curve(AsusFan.Unknown)).message);
        Assert.Equal("", AsusFanCurveCodec.WordFor(AsusFan.Unknown));
    }

    // ---- encoding ----

    [Fact]
    public void TheCurveValuesAreTheFlattenedStruct()
    {
        var values = AsusFanCurveCodec.ToValues(Curve());

        Assert.Equal(
            ["cpu", "8",
             "26", "51", "77", "102", "128", "153", "179", "204",   // pwm: percent × 255 / 100, rounded
             "8",
             "30", "40", "50", "60", "70", "80", "90", "100",
             "false"],
            values);
    }

    // ---- the exact argument lists ----

    [Fact]
    public void TheWriteCallsAreTheseExactArgumentLists()
    {
        var setCurve = AsusFanCurveCalls.SetFanCurve("u", "0", Curve());
        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.FanCurves", "set_fan_curve", "u(sayayb)", "0", "cpu", "8"],
            setCurve[..9]);
        Assert.Equal(["false"], setCurve[^1..]);

        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.FanCurves", "set_fan_curves_enabled", "ub", "0", "true"],
            AsusFanCurveCalls.SetFanCurvesEnabled("u", "0", true));

        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.FanCurves", "set_profile_fan_curve_enabled", "ssb", "balanced", "gpu", "false"],
            AsusFanCurveCalls.SetProfileFanCurveEnabled("s", "balanced", AsusFan.Gpu, false));

        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.FanCurves", "set_curves_to_defaults", "u", "3"],
            AsusFanCurveCalls.SetCurvesToDefaults("u", "3"));
    }

    // ---- the writer ----

    [Fact]
    public void TheWriterSendsTheProfileCodeAndTheCurve()
    {
        var bus = new FakeBus();
        string? warning = null;
        var writer = new AsusdFanCurvesWriter(bus.Call, "u", w => { warning = w; return true; });

        var outcome = writer.SetCurve("performance", Curve());

        Assert.True(outcome.Ok);
        Assert.Equal(AsusFanCurveMessages.Warning, warning);
        var call = bus.LastCall;
        Assert.NotNull(call);
        Assert.Equal("set_fan_curve", call![4]);
        Assert.Equal("1", call[6]);              // performance -> numeric code 1 on the u wire
        Assert.Equal("u(sayayb)", call[5]);
    }

    [Fact]
    public void ADeclinedConsentWritesNothing()
    {
        var bus = new FakeBus();
        var writer = new AsusdFanCurvesWriter(bus.Call, "u", _ => false);

        var outcome = writer.SetCurve("balanced", Curve());

        Assert.False(outcome.Ok);
        Assert.Equal(AsusArmouryMessages.Cancelled, outcome.Message);
        Assert.Empty(bus.Calls);
    }

    [Fact]
    public void AnUnknownProfileIsRefusedBeforeTheBus()
    {
        var bus = new FakeBus();
        var writer = new AsusdFanCurvesWriter(bus.Call, "u", _ => true);

        var outcome = writer.SetCurve("not-a-profile", Curve());

        Assert.False(outcome.Ok);
        Assert.Equal(AsusFanCurveMessages.UnknownProfile, outcome.Message);
        Assert.Empty(bus.Calls);
    }

    [Fact]
    public void AnInvalidCurveIsRefusedBeforeTheBus()
    {
        var bus = new FakeBus();
        var writer = new AsusdFanCurvesWriter(bus.Call, "u", _ => true);

        var outcome = writer.SetCurve("balanced", Curve(AsusFan.Unknown));

        Assert.False(outcome.Ok);
        Assert.Equal(AsusFanCurveMessages.UnknownFan, outcome.Message);
        Assert.Empty(bus.Calls);
    }

    [Fact]
    public void ARefusedWriteKeepsTheDaemonsWords()
    {
        var bus = new FakeBus { Answer = _ => (1, "Call failed: not supported") };
        var writer = new AsusdFanCurvesWriter(bus.Call, "u", _ => true);

        var outcome = writer.SetCurve("balanced", Curve());

        Assert.False(outcome.Ok);
        Assert.Equal("Call failed: not supported", outcome.Message);
    }

    [Fact]
    public void AnUnknownFanIsRefusedForTheEnableToggle()
    {
        var bus = new FakeBus();
        var writer = new AsusdFanCurvesWriter(bus.Call, "u", _ => true);

        Assert.Equal(AsusFanCurveMessages.UnknownFan,
                     writer.SetProfileFanCurveEnabled("balanced", AsusFan.Unknown, true).Message);
        Assert.Empty(bus.Calls);
    }

    [Fact]
    public void ResetToDefaultsCarriesItsOwnWarning()
    {
        var bus = new FakeBus();
        string? warning = null;
        var writer = new AsusdFanCurvesWriter(bus.Call, "s", w => { warning = w; return true; });

        Assert.True(writer.ResetToDefaults("quiet").Ok);

        Assert.Equal(AsusFanCurveMessages.ResetWarning, warning);
        Assert.Equal("quiet", bus.LastCall![6]);   // string wire sends the token itself
    }

    /// <summary>Two writes cannot be in flight at once — the same deterministic pattern as the Armoury port, with
    /// the consent delegate blocking inside the lock.</summary>
    [Fact]
    public async Task WritesAreSingleFlight()
    {
        var bus = new FakeBus();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var inFlight = 0;
        var max = 0;

        var writer = new AsusdFanCurvesWriter(bus.Call, "u", _ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            max = Math.Max(max, now);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(2));
            Interlocked.Decrement(ref inFlight);
            return true;
        });

        var first = Task.Run(() => writer.SetCurve("balanced", Curve()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "the first write never entered");

        var second = Task.Run(() => writer.SetCurve("performance", Curve()));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref max));

        release.Set();
        await Task.WhenAll(first, second);
        Assert.Equal(1, Volatile.Read(ref max));
    }
}
