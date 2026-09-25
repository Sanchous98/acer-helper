using AcerHelper.Infrastructure.Vendors.Asus;

namespace AcerHelper.Tests;

/// <summary>
/// The asusd battery charge cap: the percentage→toggle mapping, the payload parse (both integer signatures) and
/// the exact writes. The port is built over a recorded busctl runner, so no asusd is needed.
///
/// The mapping is the load-bearing part: asusd holds a PERCENTAGE (20..=100) while the app's port is an on/off
/// <c>BatteryToggle</c>, and the SAME machine can be served by the generic sysfs limiter on another boot. ON
/// therefore writes the same 80% the generic limiter writes, and OFF writes 100 — a different pair here would
/// make the switch mean something else depending on which source won.
/// </summary>
public class AsusChargeLimitTests
{
    private sealed class FakeBus
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], (int code, string output)> Answer { get; set; } = _ => (0, "");
        public (int code, string output) Call(string[] args) { Calls.Add(args); return Answer(args); }

        public string[]? LastSet => Calls.LastOrDefault(a => a.Length > 0 && a[0] == "set-property");
    }

    /// <summary>A daemon holding the given percentage and accepting writes, in the given integer signature.</summary>
    private static FakeBus Charging(int percent, string signature = "y") => new()
    {
        Answer = args => args switch
        {
            ["get-property", _, _, _, "charge_control_end_threshold"] => (0, $"{signature} {percent}"),
            _ => (0, ""),
        },
    };

    // ---- the pure rules ----

    [Theory]
    [InlineData("y 80", 80)]
    [InlineData("u 60", 60)]
    [InlineData("y 100", 100)]
    public void TheThresholdParsesWhateverTheIntegerSignature(string payload, int expected)
        => Assert.Equal(expected, AsusdChargeLimitRules.Parse(payload));

    [Fact]
    public void AnUnreadableThresholdParsesToNothing()
    {
        Assert.Null(AsusdChargeLimitRules.Parse(""));
        Assert.Null(AsusdChargeLimitRules.Parse("s \"80\""));
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(60, true)]
    [InlineData(100, false)]
    [InlineData(0, false)]
    public void AnythingBelowFullReadsAsOn(int threshold, bool on)
        => Assert.Equal(on, AsusdChargeLimitRules.IsOn(threshold));

    [Fact]
    public void OnWritesTheHealthCapAndOffWritesFull()
    {
        Assert.Equal(80, AsusdChargeLimitRules.PercentFor(on: true));
        Assert.Equal(100, AsusdChargeLimitRules.PercentFor(on: false));
    }

    // ---- the port ----

    [Fact]
    public void AReadableThresholdProducesAToggle()
    {
        var bus = Charging(80);
        var toggle = AsusdChargeLimit.TryCreate(bus.Call);

        Assert.NotNull(toggle);
        Assert.True(toggle!.Read());
    }

    [Fact]
    public void AFullThresholdReadsAsOff()
    {
        var bus = Charging(100);
        var toggle = AsusdChargeLimit.TryCreate(bus.Call);

        Assert.NotNull(toggle);
        Assert.False(toggle!.Read());
    }

    /// <summary>The write goes out in the observed signature and the health percentage, and reports success with
    /// no error — the happy path the UI reads.</summary>
    [Fact]
    public void TurningItOnWritesEighty()
    {
        var bus = Charging(100);
        var toggle = AsusdChargeLimit.TryCreate(bus.Call);

        var (ok, error) = toggle!.Write(true);

        Assert.True(ok);
        Assert.Null(error);
        var set = bus.LastSet;
        Assert.NotNull(set);
        Assert.Equal(["y", "80"], set![^2..]);
    }

    [Fact]
    public void TurningItOffWritesOneHundred()
    {
        var bus = Charging(80);
        var toggle = AsusdChargeLimit.TryCreate(bus.Call);

        Assert.True(toggle!.Write(false).ok);

        var set = bus.LastSet;
        Assert.NotNull(set);
        Assert.Equal(["y", "100"], set![^2..]);
    }

    /// <summary>A uint-wire daemon is written with the same signature — the write must match what the property
    /// reported, or the bus refuses it.</summary>
    [Fact]
    public void TheWriteCarriesTheObservedSignature()
    {
        var bus = Charging(80, signature: "u");
        var toggle = AsusdChargeLimit.TryCreate(bus.Call);

        Assert.True(toggle!.Write(true).ok);
        Assert.Equal(["u", "80"], bus.LastSet![^2..]);
    }

    /// <summary>The daemon's refusal is reported as the write's error, in its own words — the same contract the
    /// generic limiter keeps.</summary>
    [Fact]
    public void ARefusedWriteReportsTheDaemonsWords()
    {
        var bus = Charging(80);
        bus.Answer = args => args switch
        {
            ["get-property", _, _, _, "charge_control_end_threshold"] => (0, "y 80"),
            ["set-property", ..] => (1, "Call failed: Access denied"),
            _ => (0, ""),
        };
        var toggle = AsusdChargeLimit.TryCreate(bus.Call);

        var (ok, error) = toggle!.Write(false);

        Assert.False(ok);
        Assert.Equal("Call failed: Access denied", error);
    }

    /// <summary>An unreadable property yields NO port — null, so the generic sysfs limiter the base device wired
    /// stays in place. A toggle whose every write would fail is worse than none.</summary>
    [Fact]
    public void AnUnreadablePropertyYieldsNoPort()
    {
        var bus = new FakeBus { Answer = _ => (1, "not supported") };

        Assert.Null(AsusdChargeLimit.TryCreate(bus.Call));
    }
}
