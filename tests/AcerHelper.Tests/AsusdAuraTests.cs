using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Asus;

namespace AcerHelper.Tests;

/// <summary>
/// The Aura side of Phase 2: the per-device path discovery, the mode vocabulary, the wire encoders and the
/// AuraEffect argument list, and the controller driven over a recorded busctl runner. All of it is pure or
/// delegate-driven, so it runs without asusd, a D-Bus daemon or ASUS hardware.
///
/// THE RUNTIME IS UNVERIFIED and the file says so where it matters: the effect of a real <c>led_mode_data</c>
/// write has not been measured (see AsusdAura.cs). What IS pinned here is the wire form — the exact struct
/// signature <c>(uu(yyy)(yyy)ss)</c> and the flattened value order — and the mapping decisions, so a later
/// hardware pass is checking behaviour against a written-down shape rather than re-deriving it.
/// </summary>
public class AsusdAuraTests
{
    private sealed class FakeBus
    {
        public List<string[]> Calls { get; } = [];
        public Func<string[], (int code, string output)> Answer { get; set; } = _ => (0, "");
        public (int code, string output) Call(string[] args) { Calls.Add(args); return Answer(args); }
        public string[]? LastSet => Calls.LastOrDefault(a => a.Length > 0 && a[0] == "set-property");
        public string[]? SetFor(string property) => Calls.LastOrDefault(a => a.Length > 0 && a[0] == "set-property" && a[4] == property);
    }

    private const string Tuf = "/xyz/ljones/Aura/tuf";

    private static FakeBus AuraBus() => new()
    {
        Answer = args => args switch
        {
            ["get-property", _, _, "xyz.ljones.Aura", "supported_basic_modes"] => (0, "au 3 0 1 3"),
            ["get-property", _, _, "xyz.ljones.Aura", "supported_basic_zones"] => (0, "au 4 1 2 3 4"),
            ["get-property", _, _, "xyz.ljones.Aura", "device_type"] => (0, "u 0"),
            ["get-property", _, _, "xyz.ljones.Aura", "brightness"] => (0, "u 2"),
            _ => (0, ""),
        },
    };

    // ---- path discovery ----

    /// <summary>Only the per-device children are devices; the bare root and every other object are ignored. The
    /// two real shapes asusd registers (the TUF sysfs path and the USB idProduct path) both come through.</summary>
    [Fact]
    public void AuraDevicePathsAreTheChildrenOfTheRoot()
    {
        const string tree =
            "xyz.ljones.Asusd\n" +
            "└─/xyz/ljones\n" +
            "  ├─/xyz/ljones/Aura\n" +
            "  │ ├─/xyz/ljones/Aura/tuf\n" +
            "  │ └─/xyz/ljones/Aura/19b6_3_1\n" +
            "  ├─/xyz/ljones/FanCurves\n" +
            "  └─/xyz/ljones/Platform\n";

        Assert.Equal(["/xyz/ljones/Aura/tuf", "/xyz/ljones/Aura/19b6_3_1"], AsusdAura.DevicePaths(tree));
    }

    [Fact]
    public void ATreeWithNoAuraDevicesYieldsNothing()
    {
        Assert.Empty(AsusdAura.DevicePaths("xyz.ljones.Asusd\n└─/xyz/ljones\n  └─/xyz/ljones/Platform\n"));
        Assert.Empty(AsusdAura.DevicePaths(""));
    }

    // ---- introspection presence ----

    [Theory]
    [InlineData("xyz.ljones.Aura                interface -         -            -\n.Brightness  property  u  -  emits-change writable", true)]
    [InlineData("xyz.ljones.AuraExtra           interface -         -            -", false)]
    [InlineData("xyz.ljones.Backlight           interface -         -            -", false)]
    [InlineData("", false)]
    public void InterfacePresenceIsAnExactRowMatch(string introspect, bool present)
        => Assert.Equal(present, Asusd.HasInterface(introspect, "xyz.ljones.Aura"));

    // ---- the mode vocabulary ----

    [Fact]
    public void TheSupportedModesBecomeTheZoneEffects()
    {
        var controller = AsusdAuraController.TryCreate(AuraBus().Call, Tuf);

        Assert.NotNull(controller);
        var zone = Assert.Single(controller!.Zones);
        Assert.Equal("Keyboard", zone.Name);
        Assert.Equal(4, zone.SubZones);
        Assert.Equal(["Static", "Breathe", "RainbowWave"], zone.Effects.Select(e => e.Name));

        var breathe = zone.Effects.Single(e => e.Name == "Breathe");
        Assert.True(breathe.HasColor);
        Assert.True(breathe.HasSpeed);
        Assert.False(breathe.HasDirection);
        Assert.Equal(1, breathe.Handle);

        var wave = zone.Effects.Single(e => e.Name == "RainbowWave");
        Assert.False(wave.HasColor);
        Assert.True(wave.HasDirection);
    }

    /// <summary>A device that offers no mode this table knows is not a device the app can drive — the controller
    /// is refused rather than exposing an empty effect list.</summary>
    [Fact]
    public void ADeviceWithNoKnownModeIsRefused()
    {
        var bus = AuraBus();
        bus.Answer = args => args switch
        {
            ["get-property", _, _, "xyz.ljones.Aura", "supported_basic_modes"] => (0, "au 1 99"),
            _ => (0, ""),
        };

        Assert.Null(AsusdAuraController.TryCreate(bus.Call, Tuf));
    }

    // ---- the wire encoders ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 1)]
    [InlineData(50, 2)]
    [InlineData(100, 3)]
    public void BrightnessPercentMapsToLedLevel(byte percent, int led)
        => Assert.Equal(led, AsusAuraValues.LedFor(percent));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 33)]
    [InlineData(2, 66)]
    [InlineData(3, 100)]
    public void LedLevelMapsBackToPercent(int led, int percent)
        => Assert.Equal(percent, AsusAuraValues.PercentFor(led));

    [Theory]
    [InlineData(0, "Low")]
    [InlineData(50, "Med")]
    [InlineData(100, "High")]
    public void SpeedPercentMapsToTheSpeedWord(byte percent, string word)
        => Assert.Equal(word, AsusAuraValues.SpeedFor(percent));

    [Theory]
    [InlineData(1, "Right")]
    [InlineData(2, "Left")]
    public void TheAppDirectionByteMapsToTheDirectionWord(byte direction, string word)
        => Assert.Equal(word, AsusAuraValues.DirectionFor(direction));

    /// <summary>THE ONE PLACE THE EFFECT WIRE ORDER IS WRITTEN DOWN: the struct's fields, with each nested
    /// colour struct's three bytes flattened in. A swapped colour or a missing zero is a keyboard painted the
    /// wrong colour by a call that still succeeds.</summary>
    [Fact]
    public void TheEffectValuesAreTheFlattenedStruct()
    {
        var values = AsusAuraValues.EffectValues(
            mode: 1, zone: 0,
            colour1: new AccentColor(255, 0, 16),
            colour2: new AccentColor(0, 0, 0),
            speed: "High", direction: "Left");

        Assert.Equal(["1", "0", "255", "0", "16", "0", "0", "0", "High", "Left"], values);
        Assert.Equal("(uu(yyy)(yyy)ss)", AsusdAura.EffectSignature);
    }

    // ---- the apply path ----

    /// <summary>Applying an effect writes the effect struct AND then the brightness (asusd's own order: the
    /// effect write re-applies the stored brightness, so the explicit brightness is the one the UI asked for).</summary>
    [Fact]
    public void ApplyingAnEffectWritesTheStructThenTheBrightness()
    {
        var bus = AuraBus();
        var controller = AsusdAuraController.TryCreate(bus.Call, Tuf)!;
        var zone = controller.Zones.Single();
        var breathe = zone.Effects.Single(e => e.Name == "Breathe");

        Assert.True(zone.ApplyEffect(breathe, brightness: 100, speed: 100, direction: 1, color: new AccentColor(255, 0, 16)));

        var modeData = bus.SetFor("led_mode_data");
        Assert.NotNull(modeData);
        Assert.Equal(
            ["set-property", "xyz.ljones.Asusd", Tuf, "xyz.ljones.Aura", "led_mode_data", "(uu(yyy)(yyy)ss)",
             "1", "0", "255", "0", "16", "0", "0", "0", "High", "Right"],
            modeData);

        var brightness = bus.SetFor("brightness");
        Assert.NotNull(brightness);
        Assert.Equal(["u", "3"], brightness![^2..]);
    }

    /// <summary>A sub-zone paint is a STATIC effect scoped to that key zone (AuraZone Key1..Key4), so the
    /// existing per-zone UI needs no new write path.</summary>
    [Fact]
    public void ApplyingASubZoneScopesStaticToTheKeyZone()
    {
        var bus = AuraBus();
        var zone = AsusdAuraController.TryCreate(bus.Call, Tuf)!.Zones.Single();

        Assert.True(zone.ApplySubZone(index: 2, brightness: 0, color: new AccentColor(0, 255, 0)));

        var modeData = bus.SetFor("led_mode_data");
        Assert.NotNull(modeData);
        Assert.Equal(["0", "3", "0", "255", "0", "0", "0", "0", "Low", "Right"], modeData![^10..]);
    }

    [Fact]
    public void TheZoneBrightnessIsReadFromTheDevice()
    {
        var bus = AuraBus();
        var zone = AsusdAuraController.TryCreate(bus.Call, Tuf)!.Zones.Single();

        Assert.Equal(66, zone.ReadBrightness());
    }

    [Fact]
    public void BlankTurnsTheDeviceOff()
    {
        var bus = AuraBus();
        var controller = AsusdAuraController.TryCreate(bus.Call, Tuf)!;

        Assert.True(controller.Blank());

        var brightness = bus.SetFor("brightness");
        Assert.NotNull(brightness);
        Assert.Equal(["u", "0"], brightness![^2..]);
    }

    // ---- the exact argument lists for the path discovery ----

    [Fact]
    public void TheDiscoveryCallsAreTheseExactArgumentLists()
    {
        Assert.Equal(["tree", "xyz.ljones.Asusd"], Asusd.TreeArguments());
        Assert.Equal(["introspect", "xyz.ljones.Asusd", Tuf], Asusd.IntrospectArguments(Tuf));
        Assert.Equal(
            ["get-property", "xyz.ljones.Asusd", Tuf, "xyz.ljones.Aura", "brightness"],
            Asusd.GetPropertyArgumentsAt(Tuf, "xyz.ljones.Aura", "brightness"));
        Assert.Equal(
            ["set-property", "xyz.ljones.Asusd", Tuf, "xyz.ljones.Aura", "brightness", "u", "2"],
            Asusd.SetPropertyArgumentsAt(Tuf, "xyz.ljones.Aura", "brightness", "u", "2"));
        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.FanCurves", "fan_curve_data", "u", "0"],
            Asusd.CallArgumentsAt("/xyz/ljones", "xyz.ljones.FanCurves", "fan_curve_data", "u", "0"));
    }
}
