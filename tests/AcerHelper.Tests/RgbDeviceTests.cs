using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The RGB framework's zone and controller halves — <see cref="RgbZone"/>'s apply/read semantics, the
/// capability defaults an <see cref="IRgbController"/> inherits, and the aggregation <see cref="RgbDevice"/>
/// performs over several controllers. The file lives with the tests, not with a layer: <see cref="RgbZone"/>
/// is Domain and the two controller types are Infrastructure (Infrastructure/Lighting/RgbController.cs), and
/// the rules below span both.
///
/// It had no coverage at all, and it is worth having because every rule here is a SILENT one on real hardware:
/// a zone that claims sub-zones it cannot address paints the wrong region, a controller that "supports" a profile
/// flash it does not implement paints nothing, and a controller skipped during an aggregate never lights its own
/// indicator. The aggregation is the sharpest of them — see
/// <see cref="EveryControllerIsAskedForTheProfileFlash_EvenAfterOneReportsSuccess"/>.
///
/// All of it is reachable with lambda zones and a hand-written controller: no handle, no HID, no thread.
/// <c>RgbDevice</c> is the only place in the tree that implements <see cref="IRgbDevice"/> on top of
/// <see cref="IRgbController"/>s (the fakes implement the device side directly), so nothing else pins these.
/// </summary>
public class RgbDeviceTests
{
    // ---- helpers ----

    /// <summary>A zone whose apply/read hooks are the caller's to instrument. The sub-zone applier is optional
    /// ON PURPOSE: <see cref="RgbZone.HasSubZones"/> requires one, and several tests vary the count against it.
    /// One effect, because nothing here inspects the list.</summary>
    private static RgbZone Zone(string name, int subZones = 1,
                                Func<int, byte, AccentColor, bool>? applySubZone = null,
                                Func<int?>? readBrightness = null,
                                bool canFollowProfile = false) =>
        new(name, subZones, [new RgbModeInfo("Static", HasColor: true, HasSpeed: false, Handle: 0x02)],
            (_, _, _, _, _) => true, applySubZone, readBrightness, canFollowProfile);

    private static RgbModeInfo Mode(string name = "Wave") => new(name, HasColor: false, HasSpeed: true, Handle: 0x07);

    // ================= RgbZone: sub-zone capability =================

    /// <summary>The two inputs varied INDEPENDENTLY, which is the whole point: <see cref="RgbZone.HasSubZones"/>
    /// is <c>applySubZone != null &amp;&amp; subZones &gt; 1</c>, so a zone advertising four sub-zones with no
    /// applier is NOT sub-zoned (its per-sub-zone writes return false and paint nothing — advertising the split
    /// would hand the UI four swatches of which three are dead), and an applier with a single sub-zone is one
    /// region, not two. A count that cannot describe a split is not one either.</summary>
    [Theory]
    [InlineData(4, true, true)]
    [InlineData(4, false, false)]    // four sub-zones advertised, no applier
    [InlineData(2, true, true)]
    [InlineData(1, true, false)]     // an applier, but nothing to split
    [InlineData(0, true, false)]
    [InlineData(-3, true, false)]
    [InlineData(1, false, false)]
    public void HasSubZonesNeedsBothAMultiZoneCountAndAnApplier(int subZones, bool withApplier, bool expected)
    {
        var zone = Zone("Keyboard", subZones, withApplier ? (_, _, _) => true : null);

        Assert.Equal(expected, zone.HasSubZones);
        Assert.Equal(subZones, zone.SubZones);   // ...and the raw count is still reported as given
    }

    /// <summary>A per-sub-zone write to a zone with no applier is a <c>false</c> no-op, not a throw: the UI and
    /// the lamp-array bridge both address sub-zones by index without consulting <see cref="RgbZone.HasSubZones"/>
    /// first, so a throw here would take down the caller's paint loop instead of quietly doing nothing.</summary>
    [Fact]
    public void ApplySubZoneIsAFalseNoOpWhenTheZoneHasNoApplier()
    {
        var zone = Zone("Keyboard", subZones: 4);   // no applier

        Assert.False(zone.ApplySubZone(2, 50, new AccentColor(255, 0, 0)));
        Assert.False(zone.ApplySubZone(0, 0, new AccentColor(0, 0, 0)));
    }

    /// <summary>The apply hooks are the ONLY channel between the neutral zone and the vendor encoder, so the
    /// arguments have to arrive unmoved. <c>brightness</c>/<c>speed</c>/<c>direction</c> are all bytes, so a
    /// transposition compiles and silently sends another field's value — and the zone's own effect instance must
    /// arrive as the SAME object, because that is how <c>EneHidController</c> recovers the mode byte
    /// (<c>(RgbEffect)effect.Handle</c>) instead of a copy that lost it.</summary>
    [Fact]
    public void TheApplyHooksReceiveEveryArgumentInOrder()
    {
        var effectCalls = new List<(RgbModeInfo Effect, byte Brightness, byte Speed, byte Direction, AccentColor Color)>();
        var subZoneCalls = new List<(int Index, byte Brightness, AccentColor Color)>();
        var mode = Mode();
        var zone = new RgbZone("Keyboard", 4, [mode],
            (e, b, s, d, c) => { effectCalls.Add((e, b, s, d, c)); return true; },
            (i, b, c) => { subZoneCalls.Add((i, b, c)); return true; });

        Assert.True(zone.ApplyEffect(mode, 40, 3, 0x02, new AccentColor(10, 20, 30)));
        Assert.True(zone.ApplySubZone(3, 70, new AccentColor(1, 2, 3)));

        var effect = Assert.Single(effectCalls);
        Assert.Same(mode, effect.Effect);
        Assert.Equal(40, effect.Brightness);
        Assert.Equal(3, effect.Speed);
        Assert.Equal(0x02, effect.Direction);
        Assert.Equal(new AccentColor(10, 20, 30), effect.Color);

        var sub = Assert.Single(subZoneCalls);
        Assert.Equal(3, sub.Index);
        Assert.Equal(70, sub.Brightness);
        Assert.Equal(new AccentColor(1, 2, 3), sub.Color);
    }

    // ================= RgbZone: brightness read-back =================

    /// <summary>The Fn-key sync path: a zone without a brightness reader reads <c>null</c>, which is how the UI
    /// knows to leave its slider alone rather than snap it to zero. A reader that has nothing to report is the
    /// same <c>null</c>, and a genuine 0 must NOT collapse into it — 0 is "the backlight is off", a real state
    /// the UI has to show.</summary>
    [Fact]
    public void ReadBrightnessIsNullWithoutAReader_AndPassesAReaderThroughWhenThereIsOne()
    {
        Assert.Null(Zone("Keyboard", 4).ReadBrightness());                                    // no reader at all
        Assert.Null(Zone("Keyboard", 4, readBrightness: () => null).ReadBrightness());        // a reader with no answer
        Assert.Equal(75, Zone("Keyboard", 4, readBrightness: () => 75).ReadBrightness());
        Assert.Equal(0, Zone("Keyboard", 4, readBrightness: () => 0).ReadBrightness());       // off is a reading
        Assert.Equal(100, Zone("Keyboard", 4, readBrightness: () => 100).ReadBrightness());
    }

    /// <summary>The zone carries its constructor arguments verbatim — including <see cref="RgbZone.CanFollowProfile"/>,
    /// which is what tells the UI the firmware owns this region and is the flag <c>EneHidController.ProfileFollowKey</c>
    /// switches on. Losing it builds a panel for a lightbar the app must not drive.</summary>
    [Fact]
    public void AZoneCarriesItsNameSubZoneCountEffectsAndFollowProfileFlag()
    {
        var effects = new List<RgbModeInfo> { Mode("Static"), Mode("Breathing") };
        var zone = new RgbZone("Lightbar", 1, effects, (_, _, _, _, _) => false, canFollowProfile: true);

        Assert.Equal("Lightbar", zone.Name);
        Assert.Equal(1, zone.SubZones);
        Assert.Same(effects, zone.Effects);
        Assert.True(zone.CanFollowProfile);
        Assert.False(Zone("Keyboard").CanFollowProfile);
    }

    // ================= IRgbController: the inherited defaults =================

    /// <summary>A controller that overrides nothing must declare NOTHING: no profile indicator, no blanking, no
    /// settings key. These are the interface's default implementations, and they are what keeps a keyboard-only
    /// model from advertising a lightbar feature — a controller that silently answered <c>true</c> would make the
    /// UI offer a "follows profile" switch that writes to a region the machine does not have.</summary>
    [Fact]
    public void AControllerThatOverridesNothingSupportsNoneOfTheOptionalCapabilities()
    {
        using var device = new RgbDevice(new BareController());

        Assert.Empty(device.Zones);
        Assert.False(device.SetProfileFlash(new AccentColor(255, 0, 0)));
        Assert.False(device.Blank());
        Assert.Null(device.ProfileFollowKey);
    }

    /// <summary>Only the zones a controller actually advertises are aggregated; a controller with none
    /// contributes none (and does not, say, make the device null — <c>IDevice.Lighting</c> is the nullable one).</summary>
    [Fact]
    public void AControllerWithNoZonesContributesNoZones()
    {
        using var device = new RgbDevice(
            new FakeRgbController { Zones = [] },
            FakeRgbController.WithZone("Keyboard"));

        Assert.Equal(["Keyboard"], device.Zones.Select(z => z.Name));
    }

    // ================= RgbDevice: zone aggregation =================

    /// <summary>The composition order is the transport's, not the alphabet's: the UI renders one panel per zone in
    /// this order, and the lamp-array bridge indexes into it, so reordering silently repoints every lamp and every
    /// UI panel at a different physical region. The names below are deliberately neither sorted nor reversed, so
    /// the assertion cannot be satisfied by accident.</summary>
    [Fact]
    public void ZonesFromSeveralControllersKeepTheCompositionOrder()
    {
        using var device = new RgbDevice(
            FakeRgbController.WithZone("Lightbar"),
            FakeRgbController.WithZone("Keyboard"),
            FakeRgbController.WithZone("NumPad"));

        Assert.Equal(["Lightbar", "Keyboard", "NumPad"], device.Zones.Select(z => z.Name));
    }

    /// <summary>ALL of a controller's zones, not just its first: the ENE keyboard advertises one zone today, but
    /// the port is "produces the zones it can drive", and a controller that exposed several would lose every one
    /// after the first — invisible, because the device still looks populated.</summary>
    [Fact]
    public void EveryZoneOfAControllerIsAggregated()
    {
        using var device = new RgbDevice(
            new FakeRgbController { Zones = [FakeRgbController.Zone("Kbd1"), FakeRgbController.Zone("Kbd2")] },
            new FakeRgbController { Zones = [FakeRgbController.Zone("Lightbar")] });

        Assert.Equal(["Kbd1", "Kbd2", "Lightbar"], device.Zones.Select(z => z.Name));
    }

    // ================= RgbDevice: the aggregate itself =================

    /// <summary>The load-bearing one. <c>SetProfileFlash</c> folds the controllers with a bitwise <c>|</c>, so
    /// every controller is asked even after one has already reported success. With <c>||</c> the fold
    /// short-circuits and stops asking the moment the first controller answers true — the return value is
    /// IDENTICAL, so nothing but the call record can see it, and on a machine whose keyboard answers first the
    /// lightbar's indicator would silently never light again.
    ///
    /// Three controllers all reporting success, so the early one cannot be an accident of ordering.</summary>
    [Fact]
    public void EveryControllerIsAskedForTheProfileFlash_EvenAfterOneReportsSuccess()
    {
        var first = new FakeRgbController { SetProfileFlashResult = true };
        var second = new FakeRgbController { SetProfileFlashResult = true };
        var third = new FakeRgbController { SetProfileFlashResult = true };
        using var device = new RgbDevice(first, second, third);

        Assert.True(device.SetProfileFlash(new AccentColor(255, 0, 0)));

        Assert.Single(first.FlashCalls);
        Assert.Single(second.FlashCalls);   // <- skipped entirely under `||`
        Assert.Single(third.FlashCalls);
        Assert.Equal(new AccentColor(255, 0, 0), second.FlashCalls[0]);   // ...and with the same colour
        Assert.Equal(new AccentColor(255, 0, 0), third.FlashCalls[0]);
    }

    /// <summary>The same short-circuit risk in <c>Blank</c> — and here it is worse than a missing indicator: a
    /// controller skipped after an earlier success keeps its zones LIT while the app believes the backlight is
    /// off, which is exactly the clamshell case the method exists for.</summary>
    [Fact]
    public void EveryControllerIsAskedToBlank_EvenAfterOneReportsSuccess()
    {
        var first = new FakeRgbController { BlankResult = true };
        var second = new FakeRgbController { BlankResult = true };
        using var device = new RgbDevice(first, second);

        Assert.True(device.Blank());

        Assert.Equal(1, first.BlankCalls);
        Assert.Equal(1, second.BlankCalls);   // <- skipped entirely under `||`
    }

    /// <summary>The aggregate's RESULT is "did anybody take it", not "did the last one" and not "did they all":
    /// the caller uses it to decide whether the machine has a profile indicator at all, so a controller that
    /// declines must not veto one that succeeded, and a false must still be reported when nobody could.</summary>
    [Fact]
    public void TheAggregateReportsWhetherAnyControllerTookTheCall()
    {
        using var mixed = new RgbDevice(
            new FakeRgbController(),
            new FakeRgbController { SetProfileFlashResult = true, BlankResult = true },
            new FakeRgbController());
        Assert.True(mixed.SetProfileFlash(new AccentColor(1, 2, 3)));
        Assert.True(mixed.Blank());

        using var none = new RgbDevice(new FakeRgbController(), new FakeRgbController());
        Assert.False(none.SetProfileFlash(new AccentColor(1, 2, 3)));
        Assert.False(none.Blank());
    }

    // ================= RgbDevice: the profile-follow key =================

    /// <summary>The device's answer is the FIRST controller that has a key, because the preference is stored once
    /// per machine under a vendor-owned string. A controller with none must be passed over rather than answering
    /// for the device (the keyboard is first and has no key, which is the shipped arrangement), and the null case
    /// is what hides the "follows performance profile" switch entirely.</summary>
    [Fact]
    public void TheProfileFollowKeyIsTheFirstControllerThatHasOne_AndNullWhenNoneDoes()
    {
        using var keyboardOnly = new RgbDevice(new FakeRgbController(), new FakeRgbController());
        Assert.Null(keyboardOnly.ProfileFollowKey);

        using var withLightbar = new RgbDevice(
            new FakeRgbController(),                                                    // the keyboard: no key
            new FakeRgbController { ProfileFollowKey = "acer.lightbarFollowsProfile" },  // the lightbar
            new FakeRgbController { ProfileFollowKey = "someone.elses.key" });           // a second one must not win
        Assert.Equal("acer.lightbarFollowsProfile", withLightbar.ProfileFollowKey);
    }

    // ================= RgbDevice: teardown =================

    /// <summary>Teardown is best-effort per controller: the device owns handles (a HID stream, a sysfs fd), so a
    /// controller whose dispose throws must not strand the ones after it — leaked handles are what keeps a
    /// keyboard lit and a bus busy after the app has exited. It must not propagate either, or a shutdown path
    /// dies halfway through.</summary>
    [Fact]
    public void DisposeDisposesEveryController_EvenWhenOneOfThemThrows()
    {
        var first = new FakeRgbController();
        var exploding = new FakeRgbController { ThrowOnDispose = true };
        var last = new FakeRgbController();

        new RgbDevice(first, exploding, last).Dispose();   // must not throw

        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, exploding.DisposeCalls);
        Assert.Equal(1, last.DisposeCalls);
    }

    /// <summary>A minimal <see cref="IRgbController"/> that overrides NOTHING — the only way to exercise the
    /// interface's own default implementations rather than a fake's copy of them.</summary>
    private sealed class BareController : IRgbController
    {
        public IReadOnlyList<RgbZone> Zones => [];
        public void Dispose() { }
    }
}
