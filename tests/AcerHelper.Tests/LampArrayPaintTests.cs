using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// <c>LampArrayBridge.Paint</c> — turning one host lamp frame into zone writes. This is the bridge's whole
/// decision surface: which zones get painted, per-sub-zone or as one report, which effect carries an arbitrary
/// colour, what is skipped as a no-op, and what the dedupe mirror is then left believing.
///
/// Every rule here fails SILENTLY on hardware. A zone wrongly collapsed to one report paints four columns in
/// one colour; a wrong sub-zone index lights the neighbouring column; a dedupe that skips a real change leaves
/// a lamp frozen on a stale colour until the host happens to send something further away; and a mirror that
/// records a write that never happened makes every LATER frame a no-op too — so the region stays dark for as
/// long as the host keeps painting it. No exception, no <c>LastError</c>.
///
/// NOT covered here, deliberately: the lifecycle and ownership half — <c>Enable</c>/<c>Disable</c>, the worker
/// thread, the host-takeover flip, <c>Reassert</c>, <c>ReleaseOwnership</c>. Those need a hand-written
/// <c>ILampArrayTransport</c> whose <c>WaitFrame</c> parks on an event, and the tests would then own a live
/// background thread; that is the next increment, not this one. What makes THIS one cheap is that <c>Paint</c>
/// reaches no transport, no thread and no clock — so the frame logic can be driven directly.
///
/// <c>Paint</c> was made <c>internal static</c> with its two fields (<c>_layout</c>, <c>_written</c>) taken as
/// parameters, and <c>StaticEffect</c> widened <c>private</c> -> <c>internal</c>. Shape and visibility only: the
/// null-guard, the read order and every effect are unchanged, and both callers pass the two fields from the same
/// place — inside the same <c>_apply</c> lock — that the method used to read them. Same precedent as
/// <c>RyzenCurveOptimizer.Encode</c>/<c>CoreArg</c>/<c>GpuMargin</c> (see SmuOffsetEncodingTests.cs).
/// </summary>
public class LampArrayPaintTests
{
    // The effect the bridge looks for: HasColor && !HasSpeed. The same rule RgbEffectsTests pins on the shipped
    // table, and the same one LampArrayBridge.StaticEffect selects on.
    private static RgbModeInfo Static() => new("Static", HasColor: true, HasSpeed: false, Handle: 0x02);

    /// <summary>An effect that animates, so it is never the bridge's choice for a static host frame — used to
    /// pin the fallback and the no-colour case. Optionally colourless, which is what the shipped animated
    /// effects are (they cycle the firmware's own palette).</summary>
    private static RgbModeInfo Animated(string name = "Breathing", bool hasColor = false)
        => new(name, HasColor: hasColor, HasSpeed: true, Handle: 0x04);

    /// <summary>Records what the bridge did to one zone. Zones are built with REAL effect lists: unlike
    /// <c>LampArrayLayout.Build</c> (which <c>RgbDevice</c> does not) the bridge's layout does inspect them, and
    /// <c>Build</c> drops an effect-less zone entirely — so <c>FakeRgbDevice.Zone</c>, which builds exactly that,
    /// cannot be used here.</summary>
    private sealed class Recorder
    {
        public List<(RgbModeInfo Effect, byte Brightness, byte Speed, byte Direction, AccentColor Color)> Effects { get; } = [];
        public List<(int Index, byte Brightness, AccentColor Color)> SubZones { get; } = [];

        public RgbZone Zone(string name, int subZones, bool withSubZoneApplier, params RgbModeInfo[] effects) =>
            new(name, subZones, effects,
                (e, b, s, d, c) => { Effects.Add((e, b, s, d, c)); return true; },
                withSubZoneApplier ? (i, b, c) => { SubZones.Add((i, b, c)); return true; } : null);
    }

    /// <summary>A 4-sub-zone keyboard above a 1-lamp lightbar — 5 lamps, targets (0,0)…(0,3),(1,0). The
    /// keyboard's effect list is overridable because the no-colour case is one of the things worth pinning.</summary>
    private static (LampArrayLayout Layout, Recorder Keyboard, Recorder Lightbar) Device(
        params RgbModeInfo[] keyboardEffects)
    {
        var keyboard = new Recorder();
        var lightbar = new Recorder();
        var rgb = new FakeRgbDevice
        {
            Zones =
            [
                keyboard.Zone("Keyboard", 4, true, keyboardEffects.Length > 0 ? keyboardEffects : [Static()]),
                lightbar.Zone("Lightbar", 1, false, Static()),
            ],
        };
        return (LampArrayLayout.Build(rgb)!, keyboard, lightbar);
    }

    private static readonly LampColor Red = new(255, 0, 0, 100);
    private static readonly LampColor Green = new(0, 255, 0, 100);
    private static readonly LampColor Blue = new(0, 0, 255, 100);
    private static readonly LampColor Yellow = new(255, 255, 0, 100);

    // ================= the uniform collapse =================

    /// <summary>Near-equal colours across a zone collapse into ONE all-zones report rather than one per
    /// sub-zone. That is the optimisation the whole class is built around, and on HID-over-I²C it is the
    /// difference between one report and four. The full argument list is asserted because every field is a byte
    /// or an enum and a transposition would compile: brightness stays at 100 (the host's intensity channel is
    /// already folded into the RGB, so the user's slider must not apply), speed 0, direction 1.</summary>
    [Fact]
    public void NearEqualLampsInAZoneCollapseToOneReport()
    {
        var (layout, keyboard, lightbar) = Device();
        var written = new LampColor[layout.LampCount];

        LampArrayBridge.Paint(layout, written, [Red, Red, Red, Red, Red], force: false);

        var call = Assert.Single(keyboard.Effects);
        Assert.Same(layout.Zones[0].Effects[0], call.Effect);   // the Static effect it picked, the same instance
        Assert.Equal(new AccentColor(255, 0, 0), call.Color);
        Assert.Equal(100, call.Brightness);
        Assert.Equal(0, call.Speed);
        Assert.Equal(1, call.Direction);
        Assert.Empty(keyboard.SubZones);                        // ...and NOT four sub-zone reports

        // The lightbar is a separate zone with its own single report.
        Assert.Single(lightbar.Effects);
        Assert.Equal(new AccentColor(255, 0, 0), lightbar.Effects[0].Color);
    }

    /// <summary>Colours that differ beyond the epsilon can't collapse, so each sub-zone is addressed on its
    /// own — and the index is <c>Targets[i].SubZone</c>, not <c>i</c>. Here they happen to coincide (the
    /// keyboard is the first zone and its targets run 0..3), which is exactly the kind of coincidence that lets
    /// a wrong index pass: the assertion is on the recorded index so that a future layout where they diverge
    /// still has the rule written down.</summary>
    [Fact]
    public void DistinctLampsAreWrittenPerSubZoneWithTheTargetSubZoneIndex()
    {
        var (layout, keyboard, lightbar) = Device();
        var written = new LampColor[layout.LampCount];

        LampArrayBridge.Paint(layout, written, [Red, Green, Blue, Yellow, Red], force: false);

        Assert.Empty(keyboard.Effects);                          // no whole-zone report
        Assert.Equal([0, 1, 2, 3], keyboard.SubZones.Select(s => s.Index));
        Assert.Equal([Red.Rgb, Green.Rgb, Blue.Rgb, Yellow.Rgb], keyboard.SubZones.Select(s => s.Color));
        Assert.All(keyboard.SubZones, s => Assert.Equal(100, s.Brightness));
        Assert.Single(lightbar.Effects);                         // the lightbar is unaffected by the keyboard
    }

    // ================= the dedupe mirror =================

    /// <summary>THE load-bearing assertion of the uniform path. The mirror records what the hardware now SHOWS
    /// — one colour across the zone — not the per-lamp values the host asked for. The lamps here are near-equal
    /// but NOT identical, so the two rules are distinguishable: recording <c>colors[i]</c> would put
    /// <c>slightlyOff</c> in the mirror, and the next frame's dedupe would compare against a colour the hardware
    /// never showed. This is the documented reason for the line, and it is the one thing a plausible
    /// "simplification" to <c>written[i] = colors[i]</c> would silently break.</summary>
    [Fact]
    public void TheMirrorRecordsTheZoneColourTheHardwareShows_NotThePerLampValues()
    {
        var (layout, _, _) = Device();
        var written = new LampColor[layout.LampCount];
        var slightlyOff = new LampColor(252, 2, 1, 100);         // within ColorEpsilon of Red

        Assert.True(Red.IsCloseTo(slightlyOff, 5));              // the premise: these collapse
        LampArrayBridge.Paint(layout, written, [Red, slightlyOff, slightlyOff, slightlyOff, Red], force: false);

        Assert.Equal(Red, written[0]);
        Assert.All(written.Take(4), w => Assert.Equal(Red, w));  // NOT slightlyOff
    }

    /// <summary>A frame within the epsilon of what the hardware already shows earns no report at all. The host
    /// re-sends whole frames at its own rate and walks gradients one channel step at a time, so without this
    /// every step would be a report on a bus that cannot take them.</summary>
    [Fact]
    public void AFrameWithinTheEpsilonOfTheLastWriteIsSkipped()
    {
        var (layout, keyboard, lightbar) = Device();
        var written = new LampColor[layout.LampCount];

        LampArrayBridge.Paint(layout, written, [Red, Red, Red, Red, Red], force: false);
        keyboard.Effects.Clear();
        lightbar.Effects.Clear();

        LampArrayBridge.Paint(layout, written, [new LampColor(252, 2, 1, 100), Red, Red, Red, Red], force: false);

        Assert.Empty(keyboard.Effects);
        Assert.Empty(keyboard.SubZones);
        Assert.Empty(lightbar.Effects);
    }

    /// <summary>The per-lamp dedupe is per LAMP: changing one sub-zone's colour writes that one and nothing
    /// else. Collapsing the check to "did the zone change" would repaint all four columns for every step of a
    /// one-column gradient.</summary>
    [Fact]
    public void OnlyTheSubZoneWhoseColourChangedIsRewritten()
    {
        var (layout, keyboard, _) = Device();
        var written = new LampColor[layout.LampCount];

        LampArrayBridge.Paint(layout, written, [Red, Green, Blue, Yellow, Red], force: false);
        keyboard.SubZones.Clear();

        LampArrayBridge.Paint(layout, written, [Red, Green, new LampColor(0, 0, 255, 100), Yellow, Red], force: false);

        Assert.Empty(keyboard.SubZones);                          // same colour -> already a no-op
        LampArrayBridge.Paint(layout, written, [Red, Green, new LampColor(128, 0, 255, 100), Yellow, Red], force: false);

        var call = Assert.Single(keyboard.SubZones);
        Assert.Equal(2, call.Index);                              // the third column, and only it
        Assert.Equal(new AccentColor(128, 0, 255), call.Color);
    }

    /// <summary><c>force</c> is the <c>Reassert</c> path: repaint from the last frame even though nothing
    /// changed, because the OS or the EC clobbered the surface behind our back (a profile switch forces the
    /// amber OPMODE flash, sleep drops the RGB, a lid-open restores from black) and the mirror still holds the
    /// pre-clobber colours — so without <c>force</c> the repaint would be skipped and the surface would stay
    /// wrong.</summary>
    [Fact]
    public void ForceRepaintsAnUnchangedFrame()
    {
        var (layout, keyboard, lightbar) = Device();
        var written = new LampColor[layout.LampCount];

        LampArrayBridge.Paint(layout, written, [Red, Red, Red, Red, Red], force: false);
        keyboard.Effects.Clear();
        lightbar.Effects.Clear();

        LampArrayBridge.Paint(layout, written, [Red, Red, Red, Red, Red], force: true);

        Assert.Single(keyboard.Effects);
        Assert.Single(lightbar.Effects);
    }

    // ================= colours that are not colours =================

    /// <summary>Intensity 0 means "lamp off", and off is written as BLACK rather than as the RGB the host sent:
    /// the hardware advertises one intensity level, so the channel degenerates to on/off and is rendered through
    /// the colour. Passing the raw RGB instead would light the "off" lamp in whatever colour came with it.</summary>
    [Fact]
    public void ALampWithZeroIntensityIsPaintedBlackWhateverItsRgb()
    {
        var (layout, keyboard, _) = Device();
        var written = new LampColor[layout.LampCount];
        var off = new LampColor(255, 0, 0, 0);                    // red, but switched off

        LampArrayBridge.Paint(layout, written, [off, off, off, off, off], force: false);

        Assert.Equal(new AccentColor(0, 0, 0), Assert.Single(keyboard.Effects).Color);
        Assert.All(written.Take(4), w => Assert.Equal(new AccentColor(0, 0, 0), w.Rgb));
    }

    // ================= the guard =================

    /// <summary>A frame that arrives while the bridge is torn down — or a layout that was never built — must
    /// return quietly. <c>_layout</c>/<c>_written</c> are nulled by <c>Disable</c>, and this runs on the worker
    /// thread, where an unguarded dereference would take the process down rather than throw into a caller that
    /// could handle it.</summary>
    [Fact]
    public void ANullLayoutOrMirrorIsAQuietNoOp()
    {
        var (layout, keyboard, lightbar) = Device();
        var written = new LampColor[layout.LampCount];

        LampArrayBridge.Paint(null, written, [Red, Red, Red, Red, Red], force: false);
        LampArrayBridge.Paint(layout, null, [Red, Red, Red, Red, Red], force: false);

        Assert.Empty(keyboard.Effects);
        Assert.Empty(keyboard.SubZones);
        Assert.Empty(lightbar.Effects);
    }

    // ================= StaticEffect: which effect carries a host colour =================

    /// <summary>The rule is "an arbitrary colour, don't animate" — <c>HasColor &amp;&amp; !HasSpeed</c> — and it
    /// is the SAME rule the lighting UI uses to decide a zone shows colour swatches. It takes the FIRST match,
    /// so a second such effect would make the answer depend on list order. Asserted with the animated
    /// colour-capable effect placed FIRST, so "first match" cannot pass by accident.</summary>
    [Fact]
    public void TheColourEffectIsPreferredOverAnAnimatedOneThatAlsoTakesColour()
    {
        var staticEffect = Static();
        var animated = Animated("Breathing", hasColor: true);
        var zone = new Recorder().Zone("Keyboard", 4, true, animated, staticEffect);

        Assert.Same(staticEffect, LampArrayBridge.StaticEffect(zone));
    }

    /// <summary>The fallback: when nothing is static, the first colour-capable effect is used anyway. That is
    /// deliberate — a host frame still has to land somewhere — and it is why the animation "fights" the host,
    /// as the source comment says. Pinned so the fallback cannot be dropped as dead code.</summary>
    [Fact]
    public void WithNoStaticEffect_TheFirstColourCapableEffectIsUsed()
    {
        var colourless = Animated("Neon");
        var animated = Animated("Breathing", hasColor: true);
        var zone = new Recorder().Zone("Keyboard", 4, true, colourless, animated);

        Assert.Same(animated, LampArrayBridge.StaticEffect(zone));
    }

    /// <summary>No colour-capable effect at all means null: the zone cannot take an arbitrary colour, so it is
    /// not painted. Both shipped Acer tables begin with a colour-capable Static, so this is unreachable on the
    /// ENE device — but the class is public in <c>Domain</c> and any other <c>IRgbDevice</c> can reach it.</summary>
    [Fact]
    public void WithNoColourCapableEffectAtAllTheAnswerIsNull()
    {
        var zone = new Recorder().Zone("Keyboard", 4, true, Animated("Breathing"), Animated("Neon"));

        Assert.Null(LampArrayBridge.StaticEffect(zone));
    }

    /// <summary>OBSERVED CURRENT behaviour, and NOT obviously intended — a guard rather than a live path, and a
    /// latent bug in the mirror.
    ///
    /// The write is conditional on <c>StaticEffect(zone) is { }</c>, but the mirror update is not: a zone with
    /// no colour-capable effect records a colour it was never sent. The consequence is not a missed frame, it is
    /// a PERMANENTLY dark zone — the next frame within the epsilon is skipped as unchanged, and so is every
    /// frame after it, for as long as the host keeps painting that zone the same colour. Nothing throws and
    /// nothing reports.
    ///
    /// Unreachable on the shipped device: both <c>RgbEffects.Keyboard</c> and <c>RgbEffects.Lightbar</c> start
    /// with a colour-capable Static, so <c>StaticEffect</c> cannot return null for them. Pinned as observed so
    /// that fixing it (recording only what was written) is a visible change of behaviour and not a silent
    /// "cleanup"; see the same treatment of <c>EneHidController.Dir</c>'s unusable-direction fallback.</summary>
    [Fact]
    public void AZoneWithNoColourEffectIsNotPaintedButTheMirrorIsPoisonedAnyway()
    {
        var (layout, keyboard, _) = Device(Animated("Breathing"));   // effects present, none colour-capable
        var written = new LampColor[layout.LampCount];

        LampArrayBridge.Paint(layout, written, [Red, Red, Red, Red, Red], force: false);

        Assert.Equal(5, layout.LampCount);                           // the keyboard still got its four lamps
        Assert.Empty(keyboard.Effects);                              // ...none of which was painted
        Assert.All(written.Take(4), w => Assert.Equal(Red, w));      // ...yet the mirror says they were
    }

    // ================= the two gaps the layout above would hide =================

    /// <summary><c>force</c> has to bypass the dedupe on the sub-zone path too, not only on the uniform one.
    /// The test above exercises the uniform branch, so without this one a <c>force</c> dropped from the
    /// per-lamp check would go unnoticed — and the case it exists for (a clobbered surface being re-asserted)
    /// is exactly the one where the mirror still holds the pre-clobber colours and every lamp would be
    /// skipped.</summary>
    [Fact]
    public void ForceRepaintsUnchangedSubZonesToo()
    {
        var (layout, keyboard, _) = Device();
        var written = new LampColor[layout.LampCount];
        LampColor[] distinct = [Red, Green, Blue, Yellow, Red];

        LampArrayBridge.Paint(layout, written, distinct, force: false);
        keyboard.SubZones.Clear();

        LampArrayBridge.Paint(layout, written, distinct, force: true);

        Assert.Equal([0, 1, 2, 3], keyboard.SubZones.Select(s => s.Index));
    }

    /// <summary>The sub-zone index comes from <c>Targets[i].SubZone</c>, not from the lamp index <c>i</c>. In
    /// every layout the shipped device produces those two numbers happen to COINCIDE for the keyboard (it is
    /// zone 0 and its targets run 0..3), so a test built only on it would pass with the wrong expression. This
    /// one sub-divides the SECOND zone, whose lamps sit at indices 4..6 while its sub-zones are 0..2 — the two
    /// cannot be confused. A wrong index here lights the neighbouring column, silently.</summary>
    [Fact]
    public void TheSubZoneIndexComesFromTheTargetNotFromTheLampIndex()
    {
        var keyboard = new Recorder();
        var strip = new Recorder();
        var rgb = new FakeRgbDevice
        {
            Zones =
            [
                keyboard.Zone("Keyboard", 4, true, Static()),
                strip.Zone("Lightbar", 3, true, Static()),        // sub-zoned, so it gets three lamps
            ],
        };
        var layout = LampArrayLayout.Build(rgb)!;

        Assert.Equal(7, layout.LampCount);
        Assert.Equal([new LampTarget(1, 0), new LampTarget(1, 1), new LampTarget(1, 2)],
                     layout.Targets.Skip(4));                 // the premise: lamp index 4 is sub-zone 0

        var written = new LampColor[layout.LampCount];
        LampArrayBridge.Paint(layout, written, [Red, Green, Blue, Yellow, Red, Green, Blue], force: false);

        Assert.Empty(strip.Effects);
        Assert.Equal([0, 1, 2], strip.SubZones.Select(s => s.Index));   // NOT 4, 5, 6
        Assert.Equal([Red.Rgb, Green.Rgb, Blue.Rgb], strip.SubZones.Select(s => s.Color));
    }
}
