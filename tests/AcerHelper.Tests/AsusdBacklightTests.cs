using AcerHelper.Infrastructure.Vendors.Asus;

namespace AcerHelper.Tests;

/// <summary>
/// The asusd Backlight port, over a recorded busctl runner. The port reads and writes
/// <c>xyz.ljones.Backlight.primary_brightness</c>, a 0..100 percentage with D-Bus signature <c>i</c>.
///
/// NOTE ON WHAT THIS PORT IS, because the tests below do not assert it: asusd's Backlight interface is the
/// DISPLAY panel (rog-platform binds Primary to <c>intel_backlight</c>), not the keyboard backlight the app's
/// <see cref="AcerHelper.Domain.IKeyboardBrightness"/> slot models. The port is correct as an asusd reader; it is
/// deliberately not wired to the keyboard slot (see AsusdBacklight.cs).
/// </summary>
public class AsusdBacklightTests
{
    private sealed class FakeBus
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], (int code, string output)> Answer { get; set; } = _ => (0, "");
        public (int code, string output) Call(string[] args) { Calls.Add(args); return Answer(args); }
        public string[]? LastSet => Calls.LastOrDefault(a => a.Length > 0 && a[0] == "set-property");
    }

    private static FakeBus Bus(int percent) => new()
    {
        Answer = args => args switch
        {
            ["get-property", _, _, "xyz.ljones.Backlight", "primary_brightness"] => (0, $"i {percent}"),
            _ => (0, ""),
        },
    };

    [Fact]
    public void TheLevelIsAPercentage()
    {
        Assert.Equal(100, new AsusdBacklightPort(Bus(50).Call).MaxLevel);
        Assert.Equal(75, new AsusdBacklightPort(Bus(75).Call).Get());
    }

    /// <summary>An out-of-range daemon reading is clamped rather than surfaced (the panel max is asusd's to
    /// scale; the app only speaks 0..100).</summary>
    [Fact]
    public void AnOutOfRangeReadIsClamped()
    {
        Assert.Equal(100, new AsusdBacklightPort(Bus(150).Call).Get());
        Assert.Equal(0, new AsusdBacklightPort(Bus(-5).Call).Get());
    }

    /// <summary>An unreadable property reads as off — the same "unreadable reads as 0" rule the other backlight
    /// ports keep.</summary>
    [Fact]
    public void AnUnreadablePropertyReadsAsOff()
    {
        var bus = new FakeBus { Answer = _ => (1, "not supported") };

        Assert.Equal(0, new AsusdBacklightPort(bus.Call).Get());
    }

    [Fact]
    public void SetWritesTheClampedPercentageWithTheObservedSignature()
    {
        var bus = Bus(50);
        var port = new AsusdBacklightPort(bus.Call);

        Assert.True(port.Set(120));

        var set = bus.LastSet;
        Assert.NotNull(set);
        Assert.Equal(["i", "100"], set![^2..]);
        Assert.Null(port.LastError);
    }

    [Fact]
    public void ARefusedWriteKeepsTheDaemonsWords()
    {
        var bus = Bus(50);
        bus.Answer = args => args switch
        {
            ["get-property", ..] => (0, "i 50"),
            ["set-property", ..] => (1, "Call failed: Access denied"),
            _ => (0, ""),
        };
        var port = new AsusdBacklightPort(bus.Call);

        Assert.False(port.Set(40));
        Assert.Equal("Call failed: Access denied", port.LastError);
    }

    /// <summary>The get/set calls go to the DEFAULT asusd path (/xyz/ljones), where the Backlight interface is
    /// registered — not to a per-device path.</summary>
    [Fact]
    public void TheCallsAddressTheBacklightObject()
    {
        var bus = Bus(50);
        new AsusdBacklightPort(bus.Call).Set(10);

        var set = bus.LastSet;
        Assert.NotNull(set);
        Assert.Equal(
            ["set-property", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.Backlight", "primary_brightness", "i", "10"],
            set![^7..]);
    }
}
