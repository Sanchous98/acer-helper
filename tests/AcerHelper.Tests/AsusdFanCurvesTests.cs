using AcerHelper.Infrastructure.Vendors.Asus;

namespace AcerHelper.Tests;

/// <summary>
/// The read-only FanCurves plumbing: the exact read call, the tolerant parse of asusd's CurveData array, and the
/// reader over a recorded busctl runner. No write is tested here because Phase 2 makes none — the write path and
/// the IFanControl takeover are Phase 3 (see AsusdFanCurves.cs for why the curve model does not fit the app's
/// mode/speed port without a design decision).
///
/// The payload shape below is asusctl's own: FanCurvePU is D-Bus <c>s</c> ("cpu"/"gpu"/"mid"), the two byte
/// arrays are <c>ay</c> (their length prefixes are what the parser reads, never a guess), and the struct is
/// closed by a bool.
/// </summary>
public class AsusdFanCurvesTests
{
    private const string Payload =
        "a(sayayb) 2 " +
        "\"cpu\" 8 3 5 8 10 79 125 143 148 8 30 49 59 69 79 89 99 109 true " +
        "\"gpu\" 8 1 2 3 4 5 6 7 8 8 30 40 50 60 70 80 90 100 false";

    private sealed class FakeBus
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], (int code, string output)> Answer { get; set; } = _ => (0, "");
        public (int code, string output) Call(string[] args) { Calls.Add(args); return Answer(args); }
        public string[]? LastCall => Calls.LastOrDefault();
    }

    [Theory]
    [InlineData("cpu", "Cpu")]
    [InlineData("GPU", "Gpu")]
    [InlineData("mid", "Mid")]
    [InlineData("fan4", "Unknown")]
    public void TheFanWordMapsToTheFan(string word, string fan)
        => Assert.Equal(fan, AsusFanCurveValues.FanFor(word).ToString());

    [Fact]
    public void TheCurveArrayParsesToThePureModel()
    {
        var curves = AsusFanCurveValues.Parse(Payload);

        Assert.NotNull(curves);
        Assert.Equal(2, curves!.Count);

        var cpu = curves[0];
        Assert.Equal(AsusFan.Cpu, cpu.Fan);
        Assert.True(cpu.Enabled);
        Assert.Equal([30, 49, 59, 69, 79, 89, 99, 109], cpu.Points.Select(p => (int)p.TempC));
        // pwm 0..255 → percent 0..100 (rog-profiles' own conversion), integer division.
        Assert.Equal([1, 1, 3, 3, 30, 49, 56, 58], cpu.Points.Select(p => (int)p.Percent));

        var gpu = curves[1];
        Assert.Equal(AsusFan.Gpu, gpu.Fan);
        Assert.False(gpu.Enabled);
        Assert.Equal(8, gpu.Points.Count);
    }

    [Fact]
    public void AnEmptyCurveArrayIsValidAndEmpty()
        => Assert.Empty(AsusFanCurveValues.Parse("a(sayayb) 0")!);

    [Theory]
    [InlineData("")]
    [InlineData("a(sayayb) not-a-count")]
    [InlineData("a(sayayb) 1 \"cpu\" 8 3 5")]   // truncated array
    public void AMalformedPayloadIsNull(string payload)
        => Assert.Null(AsusFanCurveValues.Parse(payload));

    [Fact]
    public void TheExactReadCallCarriesTheProfileInItsObservedForm()
    {
        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.FanCurves", "fan_curve_data", "u", "0"],
            AsusdFanCurves.FanCurveDataArguments("u", "0"));
        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.FanCurves", "fan_curve_data", "s", "balanced"],
            AsusdFanCurves.FanCurveDataArguments("s", "balanced"));
    }

    [Fact]
    public void TheReaderAsksForTheProfileAndParsesTheReply()
    {
        var bus = new FakeBus { Answer = _ => (0, Payload) };
        var reader = new AsusdFanCurvesReader(bus.Call, "u");

        Assert.True(reader.TryRead("balanced", out var curves));

        Assert.Equal(2, curves.Count);
        Assert.Equal(["u", "0"], bus.LastCall![^2..]);
    }

    /// <summary>On an older string-wire asusd the profile argument is the token itself, not a code.</summary>
    [Fact]
    public void AStringWireReaderSendsTheToken()
    {
        var bus = new FakeBus { Answer = _ => (0, Payload) };
        var reader = new AsusdFanCurvesReader(bus.Call, "s");

        Assert.True(reader.TryRead("performance", out _));
        Assert.Equal(["s", "performance"], bus.LastCall![^2..]);
    }

    [Fact]
    public void ARefusedReadIsFalseAndEmpty()
    {
        var bus = new FakeBus { Answer = _ => (1, "not supported") };
        var reader = new AsusdFanCurvesReader(bus.Call, "u");

        Assert.False(reader.TryRead("balanced", out var curves));
        Assert.Empty(curves);
    }
}
