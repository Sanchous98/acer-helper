using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The HID LampArray translation layer's geometry half — what Windows Dynamic Lighting is told this laptop's
/// keyboard looks like, and which zone/sub-zone each lamp it paints actually lands on.
///
/// It had no coverage at all, and it is a good target precisely because it is PURE: no handle, no WMI, no
/// file, no thread — <see cref="LampArrayLayout.Build"/> takes an <see cref="IRgbDevice"/> and returns a
/// description, so every rule below is reachable with hand-built zones and a fake device.
///
/// Why it matters more than its size suggests: a wrong answer here is SILENT. The host paints lamps by index
/// and the bridge forwards them by <see cref="LampTarget"/>, so an off-by-one in the zone index paints the
/// wrong surface, a reversed X order makes a "wave" sweep backwards, and an intensity mistake lights a lamp
/// the host asked to be dark. None of those throw; they just look wrong on a keyboard the test suite cannot
/// see. The two rules the source comments call out as load-bearing — the left-to-right PROPORTION guarantee
/// and the once-only mm→µm conversion — are pinned directly.
///
/// Nothing here touches production code: <see cref="LampArrayLayout.Build"/> is public and the zones it walks
/// are constructible from lambdas. <see cref="FakeRgbDevice.Zone"/> is NOT usable for these tests despite
/// being built for "the lamp-array bridge": it creates zones with an EMPTY effect list, and
/// <see cref="LampArrayLayout.Build"/> skips those, so it would build nothing.
/// </summary>
public class LampArrayLayoutTests
{
    // ---- helpers ----

    /// <summary>A zone with a real (non-empty) effect list, since an effect-less zone is filtered out of the
    /// layout entirely. <paramref name="withSubZoneApplier"/> is what actually enables sub-zones (see
    /// <see cref="RgbZone.HasSubZones"/>), not the count on its own — the split is deliberate here so the two
    /// inputs can be varied against each other.</summary>
    private static RgbZone Zone(string name, int subZones = 1, bool withSubZoneApplier = false,
                                int effectCount = 1)
    {
        var effects = Enumerable.Range(0, effectCount)
            .Select(i => new RgbModeInfo($"effect{i}", HasColor: true, HasSpeed: true, Handle: i))
            .ToList();
        return new RgbZone(name, subZones, effects,
            (_, _, _, _, _) => true,
            withSubZoneApplier ? (_, _, _) => true : null);
    }

    private static IRgbDevice Device(params RgbZone[] zones) => new FakeRgbDevice { Zones = zones };

    /// <summary>The machine this feature was built for: a 4-sub-zone keyboard and a single-lamp lightbar
    /// strip, which is the shape <c>EneHidController</c> advertises on an AN18-61.</summary>
    private static IRgbDevice KeyboardAndLightbar() =>
        Device(Zone("Keyboard", subZones: 4, withSubZoneApplier: true), Zone("Lightbar"));

    private static LampArrayLayout Build(IRgbDevice rgb, Func<RgbZone, bool>? include = null,
                                        int minUpdateIntervalMs = 100) =>
        LampArrayLayout.Build(rgb, include, minUpdateIntervalMs)
        ?? throw new InvalidOperationException("Build returned null for a surface that has zones");

    // ---- the whole description, pinned at once ----

    /// <summary>The realistic layout asserted field by field, because every field is a wire value Windows
    /// reads back: the mm→µm conversion, the centring arithmetic (including its integer truncation), the
    /// strip's Y band, the latency, the advertised update interval and the purposes.
    ///
    /// The five X positions are the load-bearing part. They come from
    /// <c>KeyboardWidth * (2i+1) / (2 * count)</c>, so they pin three separate things at once: that the
    /// multiplication happens BEFORE the division (reversing them yields 0 for every lamp), that truncation
    /// is toward zero, and that the zones advance left-to-right across the full keyboard width rather than
    /// being clustered. 41 is 41.25 truncated — a rounding change would show up here, which is intended: these
    /// numbers are the contract, not an implementation detail.</summary>
    [Fact]
    public void TheRealisticKeyboardPlusLightbarLayoutIsDescribedExactly()
    {
        var layout = Build(KeyboardAndLightbar());

        Assert.Equal(LampArrayKind.Keyboard, layout.Kind);
        Assert.Equal(330_000, layout.WidthUm);          // 330 mm across
        Assert.Equal(140_000, layout.HeightUm);         // keyboard (110) + gap (18) + strip (12)
        Assert.Equal(1_000, layout.DepthUm);
        Assert.Equal(100_000, layout.MinUpdateIntervalUs);
        Assert.Equal(5, layout.LampCount);

        // Four keyboard lamps across the keyboard box, then one lightbar lamp below.
        Assert.Equal([41_000, 123_000, 206_000, 288_000, 165_000], layout.Lamps.Select(l => l.XUm));
        Assert.Equal([55_000, 55_000, 55_000, 55_000, 134_000], layout.Lamps.Select(l => l.YUm));
        Assert.All(layout.Lamps, l => Assert.Equal(0, l.ZUm));
        Assert.All(layout.Lamps, l => Assert.Equal(30_000, l.UpdateLatencyUs));

        // ...and each lamp drives the sub-zone it was built for.
        Assert.Equal([(0, 0), (0, 1), (0, 2), (0, 3), (1, 0)], layout.Targets.Select(t => (t.ZoneIndex, t.SubZone)));
    }

    // ---- what gets exposed ----

    /// <summary>A zone advertising no effects is not part of the surface the host may paint. It is filtered
    /// before anything else, so it also does not consume a lamp index — the failure mode is lamps shifting by
    /// one and painting their neighbour.</summary>
    [Fact]
    public void AZoneWithNoEffectsIsNotExposed()
    {
        var layout = Build(Device(Zone("Dead", effectCount: 0), Zone("Keyboard", subZones: 4, withSubZoneApplier: true)));

        Assert.Equal(["Keyboard"], layout.Zones.Select(z => z.Name));
        Assert.Equal(4, layout.LampCount);
        Assert.All(layout.Targets, t => Assert.Equal(0, t.ZoneIndex));   // "Dead" left no gap behind
    }

    /// <summary>Nothing to expose is <c>null</c>, not an empty layout — the caller uses that to decide the
    /// feature is absent rather than to publish a device with zero lamps.</summary>
    [Fact]
    public void ASurfaceWithNothingToExposeBuildsNothing()
    {
        Assert.Null(LampArrayLayout.Build(Device()));
        Assert.Null(LampArrayLayout.Build(Device(Zone("Dead", effectCount: 0))));
    }

    /// <summary>The one filter with a policy behind it: a zone the app must not drive (the lightbar while it
    /// follows the performance profile, where the firmware owns it) has to disappear from the layout — not
    /// merely be skipped when painting, or the host would still paint a lamp nobody forwards.</summary>
    [Fact]
    public void EverythingFilteredOutBuildsNothing()
    {
        Assert.Null(LampArrayLayout.Build(KeyboardAndLightbar(), _ => false));
    }

    [Fact]
    public void TheIncludeFilterDecidesWhichZonesAreExposed()
    {
        var layout = Build(KeyboardAndLightbar(), z => z.Name != "Lightbar");

        Assert.Equal(["Keyboard"], layout.Zones.Select(z => z.Name));
        Assert.Equal(4, layout.LampCount);
    }

    // ---- how many lamps a zone contributes ----

    /// <summary>Sub-zones require an applier, not just a count: <see cref="RgbZone.HasSubZones"/> is
    /// <c>applySubZone != null &amp;&amp; subZones &gt; 1</c>, so a zone that says "4" but cannot be addressed
    /// per sub-zone must contribute ONE lamp — painting "its" lamp paints the whole region. Advertising four
    /// lamps backed by no applier would give the host three lamps whose writes go nowhere.</summary>
    [Fact]
    public void ASubZoneApplierIsWhatMakesASubZoneZoneMultiLamp()
    {
        Assert.Equal(1, Build(Device(Zone("Kbd", subZones: 4))).LampCount);
        Assert.Equal(4, Build(Device(Zone("Kbd", subZones: 4, withSubZoneApplier: true))).LampCount);
    }

    /// <summary>A sub-zone count that cannot describe a split (0, 1 or negative) still yields exactly one
    /// lamp rather than zero, a negative count, or a throw. The guard is <c>HasSubZones</c>'s
    /// <c>&gt; 1</c> plus <c>Math.Max(1, …)</c>; the test does not care which of the two provides it, only
    /// that no input produces an absurd lamp geometry.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void AnUnusableSubZoneCountStillYieldsExactlyOneLamp(int subZones)
    {
        var layout = Build(Device(Zone("Kbd", subZones: subZones, withSubZoneApplier: true)));

        Assert.Equal(1, layout.LampCount);
        Assert.Equal([(0, 0)], layout.Targets.Select(t => (t.ZoneIndex, t.SubZone)));
    }

    // ---- the invariant the bridge depends on ----

    /// <summary>The single most important invariant in this file: <c>Zones[target.ZoneIndex]</c> must be the
    /// zone that lamp was built from. <see cref="LampArrayLayout.Zones"/> is the FILTERED list while
    /// <see cref="LampTarget"/> carries an index into it, so the two have to be produced from the same
    /// filtered sequence — the obvious bug is indexing the unfiltered device.
    ///
    /// A zone is dropped from the MIDDLE here, which is what makes it a real test: with a trailing or leading
    /// removal some off-by-one errors still land on the right name. Every lamp is checked, so the failure
    /// names the lamp that went astray.</summary>
    [Fact]
    public void EveryLampTargetsTheZoneItWasBuiltFrom()
    {
        var layout = Build(Device(
            Zone("A", subZones: 2, withSubZoneApplier: true),
            Zone("B"),                                            // filtered out — the indices must not shift
            Zone("C", subZones: 3, withSubZoneApplier: true)),
            z => z.Name != "B");

        Assert.Equal(["A", "C"], layout.Zones.Select(z => z.Name));
        Assert.Equal(["A", "A", "C", "C", "C"],
            layout.Targets.Select(t => layout.Zones[t.ZoneIndex].Name));
    }

    /// <summary>Within a zone the lamps walk their sub-zones in order, one each, starting at 0. A repeated or
    /// skipped sub-zone index would leave one column of the keyboard permanently unpainted or double-painted
    /// by whichever lamp happened to overwrite it last.</summary>
    [Fact]
    public void EveryLampDrivesADistinctSubZoneOfItsZone()
    {
        var layout = Build(Device(Zone("Kbd", subZones: 4, withSubZoneApplier: true)));

        Assert.Equal([0, 1, 2, 3], layout.Targets.Select(t => t.SubZone));
        Assert.Equal(4, layout.Targets.Select(t => t.SubZone).Distinct().Count());
    }

    // ---- geometry ----

    /// <summary>The guarantee the source comment calls out by name — "laid out left-to-right in the right
    /// PROPORTIONS: that is what makes a 'wave' sweep the right way round". Reversing the order would still
    /// look like a plausible layout, which is why it needs asserting rather than eyeballing.</summary>
    [Fact]
    public void LampsRunLeftToRightAcrossTheKeyboard()
    {
        var xs = Build(Device(Zone("Kbd", subZones: 4, withSubZoneApplier: true))).Lamps.Select(l => l.XUm).ToList();

        Assert.Equal(xs.OrderBy(x => x), xs);                       // strictly left-to-right
        Assert.Equal(xs.Count, xs.Distinct().Count());
        Assert.All(xs, x => Assert.InRange(x, 1, 330_000));          // inside the keyboard box, not on its edge
        Assert.True(xs[0] < 165_000 && xs[^1] > 165_000, "the lamps should straddle the keyboard's centre line");
    }

    /// <summary>The lightbar sits on the front edge, BELOW the keyboard, in its own Y band rather than folded
    /// into the keyboard rectangle — "a vertical wipe reaches it last, like the real hardware". Folding it in
    /// would put a lightbar lamp in the middle of the keys.</summary>
    [Fact]
    public void TheKeyboardBandComesFirstAndStripsStackBelowIt()
    {
        var layout = Build(Device(
            Zone("Kbd", subZones: 2, withSubZoneApplier: true),
            Zone("Strip1"),
            Zone("Strip2")));

        var ys = layout.Lamps.Select(l => l.YUm).ToList();

        Assert.Equal([55_000, 55_000], ys.Take(2));                  // both keyboard lamps share the key band
        Assert.Equal([134_000, 164_000], ys.Skip(2));                // then each strip one band further down
        Assert.True(ys[2] > ys[0] && ys[3] > ys[2], "each strip must sit strictly below the previous one");
        Assert.All(layout.Lamps, l => Assert.InRange(l.YUm, 0, layout.HeightUm));
    }

    /// <summary>The layout's height ends at the BOTTOM of the last strip, not at the top of the next gap:
    /// <c>bottomMm</c> is only advanced for <c>zi &gt; 0</c> and tracks that strip's own bottom. Counting the
    /// trailing gap makes the device report a band of empty space the host then aims effects at.</summary>
    [Fact]
    public void TheLayoutHeightStopsAtTheLastStrip_NotAtItsTrailingGap()
    {
        Assert.Equal(110_000, Build(Device(Zone("Kbd"))).HeightUm);                             // keyboard only
        Assert.Equal(140_000, Build(Device(Zone("Kbd"), Zone("S1"))).HeightUm);                 // + one strip
        Assert.Equal(170_000, Build(Device(Zone("Kbd"), Zone("S1"), Zone("S2"))).HeightUm);     // + another
    }

    /// <summary>The mm→µm and ms→µs conversions happen exactly once, here — the source comment's
    /// "Units ON THE WIRE are micrometres and microseconds". A double conversion (or a missing one) is a
    /// device either 1000× too large or 1000× too small, which the host renders as lamps crammed into a
    /// corner. Asserted as multiples of 1000, plus the advertised interval, which is the one value the host
    /// is asked to honour rather than merely be bounded by.</summary>
    [Fact]
    public void EveryPositionIsConvertedToTheWireUnitOnce()
    {
        var layout = Build(KeyboardAndLightbar());

        Assert.All(layout.Lamps, l =>
        {
            Assert.Equal(0, l.XUm % 1000);
            Assert.Equal(0, l.YUm % 1000);
            Assert.Equal(0, l.ZUm % 1000);
            Assert.Equal(0, l.UpdateLatencyUs % 1000);
        });
        Assert.Equal(0, layout.WidthUm % 1000);
        Assert.Equal(0, layout.HeightUm % 1000);
        Assert.Equal(0, layout.DepthUm % 1000);
    }

    /// <summary>The advertised minimum interval is the caller's to choose and is carried through in µs —
    /// the cheapest fix for a controller that cannot take 60 Hz, since a well-behaved host then simply does
    /// not push frames faster.</summary>
    [Fact]
    public void TheAdvertisedUpdateIntervalIsCarriedThroughInMicroseconds()
    {
        Assert.Equal(100_000, Build(KeyboardAndLightbar()).MinUpdateIntervalUs);              // the default
        Assert.Equal(33_000, Build(KeyboardAndLightbar(), minUpdateIntervalMs: 33).MinUpdateIntervalUs);
    }

    // ---- kind and purposes ----

    /// <summary>A multi-sub-zone first surface IS the keyboard; a device that is a single lamp gets
    /// <see cref="LampArrayKind.Chassis"/> because describing it as a keyboard attracts key-shaped effects it
    /// cannot render. Note the rule reads the FIRST zone's sub-zone capability, not the total lamp count.</summary>
    [Theory]
    [InlineData(4, true, LampArrayKind.Keyboard)]
    [InlineData(2, true, LampArrayKind.Keyboard)]
    [InlineData(4, false, LampArrayKind.Chassis)]     // four sub-zones advertised, but no applier → one lamp
    [InlineData(1, false, LampArrayKind.Chassis)]
    public void AMultiSubZoneSurfaceIsAKeyboardAndASingleLampSurfaceIsAChassis(
        int subZones, bool withSubZoneApplier, LampArrayKind expected)
    {
        var layout = Build(Device(Zone("Kbd", subZones, withSubZoneApplier)));

        Assert.Equal(expected, layout.Kind);
    }

    /// <summary>Only the keyboard's lamps claim to light the keys; the strip is purely decorative. The
    /// purposes are advisory to the host, so getting them wrong does not throw — it just means an effect the
    /// host considers "illumination" skips the lightbar, or a decorative effect includes the keys.</summary>
    [Fact]
    public void OnlyTheFirstSurfaceLightsTheKeys()
    {
        var layout = Build(KeyboardAndLightbar());

        Assert.All(layout.Lamps.Take(4), l =>
        {
            Assert.True(l.Purposes.HasFlag(LampPurposes.Illumination));
            Assert.True(l.Purposes.HasFlag(LampPurposes.Accent));
        });
        Assert.Equal(LampPurposes.Accent, layout.Lamps[4].Purposes);
        Assert.False(layout.Lamps[4].Purposes.HasFlag(LampPurposes.Illumination));

        Assert.All(layout.Lamps, l => Assert.True(l.IsProgrammable));
    }

    // ---- LampColor: the per-lamp colour contract ----

    /// <summary>We advertise <c>IntensityLevelCount = 1</c>, so per spec the host BAKES brightness into RGB
    /// and the intensity channel degenerates to on/off: intensity 0 means "lamp off" and the RGB bytes are
    /// not to be rendered. Painting them anyway lights a lamp the host explicitly asked to be dark — the
    /// failure is a lit key, not an exception.</summary>
    [Fact]
    public void ZeroIntensityIsOff_RegardlessOfTheRgbSent()
    {
        Assert.Equal(new AccentColor(0, 0, 0), new LampColor(255, 128, 0, 0).Rgb);
        Assert.Equal(new AccentColor(0, 0, 0), new LampColor(0, 0, 0, 0).Rgb);
    }

    /// <summary>Any non-zero intensity renders the RGB exactly as sent — the host has already scaled it, so
    /// the app must not scale it a second time.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(255)]
    public void AnyNonZeroIntensityRendersTheRgbAsSent(byte intensity)
    {
        Assert.Equal(new AccentColor(255, 128, 0), new LampColor(255, 128, 0, intensity).Rgb);
    }

    /// <summary>The dedupe that keeps whole frames from reaching a paced HID-over-I²C controller: a gradient
    /// or breathing effect walks one channel at a time, and a sub-perceptual step must not earn a write.
    /// "Sub-perceptual" is the caller's epsilon, inclusive at the boundary.</summary>
    [Fact]
    public void IsCloseToIgnoresSubPerceptualDeltas()
    {
        var c = new LampColor(100, 100, 100, 255);

        Assert.True(c.IsCloseTo(new LampColor(100, 100, 100, 255), 5));   // identical
        Assert.True(c.IsCloseTo(new LampColor(105, 95, 100, 255), 5));    // each channel exactly at epsilon
        Assert.False(c.IsCloseTo(new LampColor(106, 100, 100, 255), 5));  // one channel past it
        Assert.False(c.IsCloseTo(new LampColor(100, 100, 89, 255), 5));   // ...and in the other direction
    }

    /// <summary>On/off is compared EXACTLY, not with the epsilon: a lamp going from dark to a very dim
    /// colour is a change the host asked for, and treating it as a no-op would leave that lamp dark for as
    /// long as the host kept sending it — the "lamp could stay one epsilon off forever" failure.</summary>
    [Fact]
    public void IsCloseToTreatsOffAndOnAsDifferent_EvenWithIdenticalRgb()
    {
        var off = new LampColor(100, 100, 100, 0);
        var on = new LampColor(100, 100, 100, 255);

        Assert.False(off.IsCloseTo(on, 5));
        Assert.False(on.IsCloseTo(off, 5));
        Assert.True(off.IsCloseTo(new LampColor(100, 100, 100, 0), 5));
        Assert.True(on.IsCloseTo(new LampColor(100, 100, 100, 1), 5));    // any non-zero intensity is "on"
    }
}
