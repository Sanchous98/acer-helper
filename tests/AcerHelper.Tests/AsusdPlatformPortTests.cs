using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Asus;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The asusd-backed profiles port, driven over a RECORDED busctl runner — no asusd, no D-Bus, no Linux box. This
/// is the shape CardwireGpuAccessPort established, and it is what lets the port's probing, both wire forms, its
/// writes and its refusals be asserted exactly.
///
/// TWO WIRE FORMS ARE TESTED ON PURPOSE. Current asusd declares its PlatformProfile enum with
/// <c>#[zvariant(signature = "u")]</c>, so the property is a uint and the choices an <c>au</c>; older builds
/// exposed the same enum as strings. The port reads the live signature and must WRITE in the form it observed,
/// so a test for each direction is not belt-and-braces — without both, one asusd generation silently stops being
/// settable.
/// </summary>
public class AsusdPlatformPortTests
{
    /// <summary>A recorder standing in for <c>Busctl.Call</c>. <see cref="Answer"/> maps an argument list to the
    /// process's (exit code, output); every call is kept so a test can assert what was and was not sent.</summary>
    private sealed class FakeBus
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], (int code, string output)> Answer { get; set; } = _ => (0, "");
        public (int code, string output) Call(string[] args) { Calls.Add(args); return Answer(args); }

        public string[]? LastSet => Calls.LastOrDefault(a => a.Length > 0 && a[0] == "set-property");
        public bool CalledNext => Calls.Any(a => a.Length > 0 && a[0] == "call");
    }

    /// <summary>An older-asusd bus: choices as <c>as</c>, active value as <c>s</c>.</summary>
    private static FakeBus StringBus() => new()
    {
        Answer = args => args switch
        {
            ["get-property", _, _, _, "platform_profile_choices"] => (0, "as 3 \"quiet\" \"balanced\" \"performance\""),
            ["get-property", _, _, _, "platform_profile"] => (0, "s \"balanced\""),
            _ => (0, ""),
        },
    };

    /// <summary>A current-asusd bus: choices as <c>au</c>, active value as <c>u</c>.</summary>
    private static FakeBus NumericBus() => new()
    {
        Answer = args => args switch
        {
            ["get-property", _, _, _, "platform_profile_choices"] => (0, "au 3 2 0 1"),
            ["get-property", _, _, _, "platform_profile"] => (0, "u 0"),
            _ => (0, ""),
        },
    };

    private static AsusdPlatformPort Port(FakeBus bus) => new(bus.Call);

    // ---- probing ----

    [Fact]
    public void ThePortOffersTheKernelsChoices()
    {
        var port = Port(StringBus());

        Assert.True(port.Available);
        Assert.Equal(["quiet", "balanced", "performance"], port.All.Select(p => p.Id));
        Assert.Equal(port.All.Select(p => p.Id), port.Selectable().Select(p => p.Id));
    }

    [Fact]
    public void TheNumericChoicesProduceTheSameTokens()
    {
        var port = Port(NumericBus());

        Assert.True(port.Available);
        Assert.Equal(["quiet", "balanced", "performance"], port.All.Select(p => p.Id));
    }

    /// <summary>When the choices cannot be read — the daemon answered an error, or the property is unsupported —
    /// the port reports itself unavailable and offers nothing. That is what lets the Linux wiring keep the
    /// generic port instead of standing an empty vendor port in front of a working one.</summary>
    [Fact]
    public void ARejectedChoicesReadLeavesThePortUnavailable()
    {
        var bus = new FakeBus { Answer = _ => (1, "Unknown property") };

        var port = Port(bus);

        Assert.False(port.Available);
        Assert.Empty(port.All);
    }

    // ---- reading the active profile ----

    [Theory]
    [InlineData("s \"quiet\"", "quiet")]
    [InlineData("s \"performance\"", "performance")]
    [InlineData("u 2", "quiet")]
    [InlineData("u 1", "performance")]
    public void CurrentIsTheMachineProfileThePayloadNames(string payload, string token)
    {
        var bus = new FakeBus
        {
            Answer = args => args switch
            {
                ["get-property", _, _, _, "platform_profile_choices"] => (0, "as 3 \"quiet\" \"balanced\" \"performance\""),
                ["get-property", _, _, _, "platform_profile"] => (0, payload),
                _ => (0, ""),
            },
        };

        Assert.Equal(token, Port(bus).Current()!.Id);
    }

    /// <summary>A payload the parser cannot name is null — never a nearby profile. This is the guard against a
    /// machine reporting "custom" (or anything else outside the choice list) silently being read as Balanced and
    /// having Balanced's presets applied to it.</summary>
    [Fact]
    public void AnUnreadableCurrentIsNull()
    {
        var bus = new FakeBus
        {
            Answer = args => args switch
            {
                ["get-property", _, _, _, "platform_profile_choices"] => (0, "as 3 \"quiet\" \"balanced\" \"performance\""),
                ["get-property", _, _, _, "platform_profile"] => (1, "Failed"),
                _ => (0, ""),
            },
        };

        Assert.Null(Port(bus).Current());
    }

    // ---- writing ----

    /// <summary>On a string-wire asusd the write carries the token with the <c>s</c> signature it observed.</summary>
    [Fact]
    public void SetWritesTheTokenWhenTheWireIsStrings()
    {
        var bus = StringBus();
        var port = Port(bus);

        Assert.True(port.Set(AsusProfiles.ToProfile("performance")));

        var set = bus.LastSet;
        Assert.NotNull(set);
        Assert.Equal(["s", "performance"], set![^2..]);
        Assert.Null(port.LastError);
    }

    /// <summary>On a numeric-wire asusd the SAME user action writes the enum NUMBER — writing the word would be
    /// refused by the bus, so this is the direction that keeps current asusd settable at all.</summary>
    [Fact]
    public void SetWritesTheNumericCodeWhenTheWireIsNumeric()
    {
        var bus = NumericBus();
        var port = Port(bus);

        Assert.True(port.Set(AsusProfiles.ToProfile("performance")));

        var set = bus.LastSet;
        Assert.NotNull(set);
        Assert.Equal(["u", "1"], set![^2..]);
    }

    /// <summary>A profile this machine does not offer is REFUSED, and NOTHING is sent: the choice list is the
    /// authority and mapping onto a nearby token is how a user clicks Performance and gets quiet.</summary>
    [Fact]
    public void SetRefusesAProfileTheMachineDoesNotOffer()
    {
        var bus = StringBus();
        var port = Port(bus);

        Assert.False(port.Set(AsusProfiles.ToProfile("low-power")));   // not in this choice list

        Assert.Null(bus.LastSet);
        Assert.Contains("Low power", port.LastError);

        // A refusal must not stick: the next successful write clears it.
        Assert.True(port.Set(AsusProfiles.ToProfile("balanced")));
        Assert.Null(port.LastError);
    }

    [Fact]
    public void ARefusedWriteKeepsTheDaemonsWords()
    {
        var bus = StringBus();
        bus.Answer = args => args switch
        {
            ["get-property", _, _, _, "platform_profile_choices"] => (0, "as 3 \"quiet\" \"balanced\" \"performance\""),
            ["get-property", _, _, _, "platform_profile"] => (0, "s \"balanced\""),
            ["set-property", ..] => (1, "Call failed: Access denied\n"),
            _ => (0, ""),
        };

        var port = Port(bus);

        Assert.False(port.Set(AsusProfiles.ToProfile("quiet")));
        Assert.Equal("Call failed: Access denied", port.LastError);
    }

    // ---- asusd's own cycle ----

    [Fact]
    public void NextCallsTheDaemonsOwnCycle()
    {
        var bus = StringBus();
        var port = Port(bus);

        Assert.True(port.Next());

        Assert.True(bus.CalledNext);
        Assert.Contains(bus.Calls, a => a[^1] == "next_platform_profile" && a[0] == "call");
    }

    [Fact]
    public void NextReportsAFailure()
    {
        var bus = StringBus();
        bus.Answer = args => args switch
        {
            ["get-property", _, _, _, "platform_profile_choices"] => (0, "as 3 \"quiet\" \"balanced\" \"performance\""),
            ["get-property", _, _, _, "platform_profile"] => (0, "s \"balanced\""),
            ["call", ..] => (1, "not supported"),
            _ => (0, ""),
        };

        var port = Port(bus);

        Assert.False(port.Next());
        Assert.Equal("not supported", port.LastError);
    }

    // ---- traits ----

    [Fact]
    public void TheClassComesOffTheSameTableThatOfferedTheProfile()
    {
        var port = Port(StringBus());

        Assert.Equal(ProfileKind.Performance, port.Traits(AsusProfiles.ToProfile("performance")).Kind);
        Assert.Equal(ProfileKind.Quiet, port.Traits(AsusProfiles.ToProfile("quiet")).Kind);
        Assert.Equal(ProfileKind.Other, port.Traits(AsusProfiles.ToProfile("turbo")).Kind);
    }
}
