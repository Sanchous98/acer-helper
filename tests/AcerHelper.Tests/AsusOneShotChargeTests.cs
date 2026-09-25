using AcerHelper.Infrastructure.Vendors.Asus;

namespace AcerHelper.Tests;

/// <summary>
/// asusd's <c>one_shot_full_charge()</c> — implemented as a distinct momentary action and deliberately NOT
/// mapped to <c>Battery.Calibration</c>. The tests pin the call and the refusal handling; the docstring on
/// <see cref="AsusdOneShotCharge"/> carries the mapping decision (a one-shot command is not a latched toggle,
/// and it is not the persistent charge limit either).
/// </summary>
public class AsusOneShotChargeTests
{
    private sealed class FakeBus
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], (int code, string output)> Answer { get; set; } = _ => (0, "");
        public (int code, string output) Call(string[] args) { Calls.Add(args); return Answer(args); }
    }

    [Fact]
    public void TheCallIsThisExactArgumentList()
    {
        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.Platform", "one_shot_full_charge"],
            AsusdOneShotCharge.Arguments());
    }

    [Fact]
    public void ASuccessIsReported()
    {
        var bus = new FakeBus();

        var (ok, error) = AsusdOneShotCharge.Run(bus.Call);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Single(bus.Calls);
    }

    [Fact]
    public void ARefusalKeepsTheDaemonsWords()
    {
        var bus = new FakeBus { Answer = _ => (1, "Call failed: not supported") };

        var (ok, error) = AsusdOneShotCharge.Run(bus.Call);

        Assert.False(ok);
        Assert.Equal("Call failed: not supported", error);
    }
}
