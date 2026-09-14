using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// The ENE RGB packet codec's pure arithmetic — the three <c>EneHidController</c> helpers that decide what goes
/// on the wire without needing a transport: brightness emulation for the surface that ignores the HID brightness
/// byte (<c>Scale</c>), report byte[5]'s direction defaults (<c>Dir</c>), and the coalescing key the writer queue
/// collapses on (<c>SameRegion</c>).
///
/// Worth a file of its own because all three fail SILENTLY on hardware this suite cannot see, and the source
/// comments record all three classes of failure as having already happened on an AN18-61: a mis-scaled colour is
/// a lightbar that dims when asked for maximum, a wrong byte[5] is a Wave that sweeps the wrong way or an effect
/// the firmware reads as static, and a wrong coalescing key either strands a keyboard paint ahead of the profile
/// flash (the keyboard then shows the profile palette instead of the chosen colour) or lets a colour drag grow
/// the queue without bound. None of them throws.
///
/// NOT covered here, deliberately: the packet itself. <c>Send</c> and <c>SetProfileFlash</c> are instance methods
/// that only reach the wire through <c>SetFeature</c>, and the only way to an instance is the constructor — which
/// returns early unless a real ENE HID interface opens. Reaching them would need this machine's actual hardware
/// AND would drive its lights, so the colour-byte ORDER difference the source comment calls out (<c>Send</c>
/// emits R,G,B; <c>SetProfileFlash</c> emits B,G,R) stays untested. It is a gap in coverage, not an open
/// question about the behaviour: the two orders are documented at the top of that file and reproduced by hand
/// from NitroSense.
///
/// <c>Scale</c>/<c>Dir</c>/<c>SameRegion</c> were widened from <c>private</c> to <c>internal</c> for this file —
/// visibility only, following the <c>RyzenCurveOptimizer.Encode</c>/<c>CoreArg</c>/<c>GpuMargin</c> precedent
/// (see SmuOffsetEncodingTests.cs).
/// </summary>
public class EneHidEncodingTests
{
    // The two flag bytes Send()/Dir() write into report byte[5]. Private in the source, so the literals are
    // repeated here on purpose: these are wire values, not implementation details.
    private const byte FlagStatic = 0x01;
    private const byte FlagEffect = 0x02;

    // The real effects, not hand-built stand-ins: the flags they carry are what Dir() branches on, and
    // RgbEffectsTests already pins them, so this file tests the encoder against the shipped catalogue.
    private static RgbEffect Effect(string name) => RgbEffects.Keyboard.Single(e => e.Name == name);

    // ================= Scale — the lightbar's brightness emulation =================

    /// <summary>Brightness is emulated by scaling the colour, because the lightbar's firmware ignores the HID
    /// brightness byte (<c>ApplyLightbar</c> sends <c>FullBright</c> and relies on this). Each channel scales
    /// independently and the division TRUNCATES — 255 at 50% is 127, not 128, and 255 at 1% is 2, not 3. The
    /// truncation is the contract rather than a detail: it is what makes 100% exactly the colour asked for, so
    /// the two surfaces (keyboard via the byte, lightbar via the scale) agree at full brightness.</summary>
    [Theory]
    [InlineData(100, 255, 128, 0, 255, 128, 0)]      // 100% is the colour, unchanged
    [InlineData(100, 1, 2, 3, 1, 2, 3)]
    [InlineData(50, 255, 128, 0, 127, 64, 0)]        // 127.5 -> 127
    [InlineData(50, 200, 100, 50, 100, 50, 25)]      // each channel on its own
    [InlineData(1, 255, 255, 255, 2, 2, 2)]          // 2.55 -> 2: truncation, NOT round-to-nearest
    [InlineData(98, 255, 255, 255, 249, 249, 249)]   // 249.9 -> 249: ...and it is not rounding at the top either
    [InlineData(33, 255, 255, 255, 84, 84, 84)]
    [InlineData(0, 255, 128, 0, 0, 0, 0)]            // 0% is off, whatever the colour
    public void ScaleIsEachChannelTimesThePercentageTruncated(
        byte brightness, byte r, byte g, byte b, byte expectedR, byte expectedG, byte expectedB)
    {
        Assert.Equal(new AccentColor(expectedR, expectedG, expectedB),
                     EneHidController.Scale(new AccentColor(r, g, b), brightness));
    }

    /// <summary>The clamp, and the reason this helper is worth a test at all. The parameter is a BYTE (0..255)
    /// but it means a PERCENTAGE (0..100), so anything above 100 has to saturate. Without the clamp the product
    /// runs past the byte cast and WRAPS: 255 at 255% would be 650, i.e. 0x8A, so asking for the brightest
    /// setting would give 54% — the lightbar gets dimmer the harder the user pushes the slider, with no error
    /// anywhere. Full white is used so that every channel would wrap.</summary>
    [Theory]
    [InlineData(101)]   // unclamped: 257 -> 0x01
    [InlineData(128)]   // unclamped: 326 -> 0x46
    [InlineData(200)]   // unclamped: 510 -> 0xFE
    [InlineData(255)]   // unclamped: 650 -> 0x8A
    public void ABrightnessAboveOneHundredPercentDoesNotDarkenTheColour(byte brightness)
    {
        Assert.Equal(new AccentColor(255, 255, 255),
                     EneHidController.Scale(new AccentColor(255, 255, 255), brightness));
    }

    // ================= Dir — report byte[5] =================

    /// <summary>Wave is the only effect that reports the user's direction, and it reports it VERBATIM: 1 and 2
    /// are the two the UI offers (<c>LightingViewModel</c> writes <c>ReverseDirection ? 2 : 1</c>). Note that the
    /// 1 case cannot be told apart from the fallback below — 1 IS the static flag — so the 2 case carries the
    /// weight here.</summary>
    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(2, 0x02)]
    public void ADirectionalEffectReportsTheUsersDirection(byte direction, byte expected)
    {
        Assert.Equal(expected, EneHidController.Dir(Effect("Wave"), direction));
    }

    /// <summary>OBSERVED CURRENT behaviour — a guard, not a live path, and NOT obviously intended.
    ///
    /// <c>Dir</c> is <c>e.HasDirection ? (direction is 1 or 2 ? direction : FlagStatic) : …</c>, so a directional
    /// effect handed anything but 1 or 2 falls back to the STATIC flag (0x01) rather than to the effect flag
    /// (0x02) the sibling branch uses for animated effects — which reads oddly, since Wave IS animated and the
    /// source comment above <c>Dir</c> says the fallback is "the mode default the firmware expects — 0x02 for
    /// animated effects, 0x01 for static". Whether the firmware treats 0x01 as "not a direction, use the mode
    /// default" or as "treat this effect as static" is not knowable from here; nobody has watched a Wave with a
    /// bogus direction byte on the hardware.
    ///
    /// Unreachable from the shipped UI: <c>LightSettings.Direction</c> defaults to 1, is documented "1 or 2", and
    /// <c>LightingViewModel</c> normalises through <c>state.Direction == 2</c> before ever calling this. So this
    /// is a latent trap for a future caller that passes a raw persisted value or a 0-initialised byte — exactly
    /// the shape the SmuOffsetEncodingTests file records for <c>RyzenCurveOptimizer.Encode</c>'s positive branch.
    /// Pinned as observed so a change of mind is visible.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(255)]
    public void ADirectionalEffectWithAnUnusableDirection_FallsBackToTheStaticFlag(byte direction)
    {
        Assert.Equal(FlagStatic, EneHidController.Dir(Effect("Wave"), direction));
    }

    /// <summary>Every other animated effect reports the EFFECT flag whatever the direction byte says — the
    /// firmware reads this as "this is an effect, use the mode byte", and a static flag here would make the
    /// animation render as a still colour. The direction byte is not consulted at all for these.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void AnAnimatedEffectThatIsNotDirectionalReportsTheEffectFlag(byte direction)
    {
        Assert.Equal(FlagEffect, EneHidController.Dir(Effect("Breathing"), direction));
        Assert.Equal(FlagEffect, EneHidController.Dir(Effect("Neon"), direction));
    }

    /// <summary>The static write reports the static flag, which is what makes an arbitrary-colour write stay
    /// still instead of animating it. This is the path every per-sub-zone colour goes out on.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AStaticEffectReportsTheStaticFlag(byte direction)
    {
        Assert.Equal(FlagStatic, EneHidController.Dir(Effect("Static"), direction));
    }

    // ================= SameRegion — the writer queue's coalescing key =================

    /// <summary>A report in the shape <c>Send</c> builds: report id, target, mode, brightness, speed, direction,
    /// the three colour bytes, the zone mask, and the trailing zero. Only bytes 1, 2 and 9 are the region key.</summary>
    private static byte[] Report(byte target, byte mode, byte zoneMask, byte brightness = 0x64,
                                 byte speed = 0x00, byte direction = 0x01,
                                 byte r = 0xFF, byte g = 0x00, byte b = 0x00) =>
        [0xA4, target, mode, brightness, speed, direction, r, g, b, zoneMask, 0x00];

    private const byte TgtKeyboard = 0x21, TgtLightbar = 0x65;
    private const byte ModeStatic = 0x02, ModeOpModeFlash = 0x06;
    private const byte KbAllZones = 0x0F, LbAllZones = 0x1F;

    /// <summary>The fields a drag or a slider changes — brightness, speed, direction, colour — must NOT be part
    /// of the key, or the queue stops collapsing: every frame of a colour drag becomes a separate report and a
    /// stalled worker (see the HID-over-I²C note) comes back to a backlog of stale ones instead of the latest
    /// state. This is the test that would fail if someone "improved" the key to compare the whole packet.</summary>
    [Fact]
    public void SameRegionIgnoresTheFieldsADragChanges()
    {
        var baseline = Report(TgtKeyboard, ModeStatic, KbAllZones);

        Assert.True(EneHidController.SameRegion(baseline, Report(TgtKeyboard, ModeStatic, KbAllZones, brightness: 0x00)));
        Assert.True(EneHidController.SameRegion(baseline, Report(TgtKeyboard, ModeStatic, KbAllZones, speed: 0x05)));
        Assert.True(EneHidController.SameRegion(baseline, Report(TgtKeyboard, ModeStatic, KbAllZones, direction: 0x02)));
        Assert.True(EneHidController.SameRegion(baseline, Report(TgtKeyboard, ModeStatic, KbAllZones, r: 0x00, g: 0xFF, b: 0x7F)));

        // ...and it is symmetric, because the queue compares an incoming report against a queued one.
        Assert.True(EneHidController.SameRegion(
            Report(TgtKeyboard, ModeStatic, KbAllZones, brightness: 0x01), baseline));
    }

    /// <summary>The failure the source comment records by name: the profile flash (mode 0x06) and a keyboard
    /// paint (mode 0x02) target the same region — same target, same zone mask — so the MODE is the only thing
    /// keeping them apart. Collapse them and the coalescing either drops the paint or strands it ahead of the
    /// flash, leaving the keyboard on the profile palette instead of the colour the user picked.</summary>
    [Fact]
    public void TheProfileFlashIsNotTheSameRegionAsAKeyboardPaint()
    {
        Assert.False(EneHidController.SameRegion(
            Report(TgtKeyboard, ModeOpModeFlash, KbAllZones),
            Report(TgtKeyboard, ModeStatic, KbAllZones)));
    }

    /// <summary>Each keyboard sub-zone is its own region (the mask is <c>1 &lt;&lt; zoneIndex</c>), so dragging
    /// one sub-zone's colour must not cancel a pending write to another — the four sub-zone paints of a single
    /// apply are four distinct queued reports, and collapsing them would leave three columns unpainted.</summary>
    [Theory]
    [InlineData(0x01, 0x02)]
    [InlineData(0x01, 0x04)]
    [InlineData(0x01, 0x08)]
    [InlineData(0x02, 0x04)]
    [InlineData(0x02, 0x08)]
    [InlineData(0x04, 0x08)]
    [InlineData(0x0F, 0x01)]    // the all-zones paint is not the same region as any single column
    public void EachKeyboardSubZoneIsItsOwnRegion(byte maskA, byte maskB)
    {
        Assert.False(EneHidController.SameRegion(
            Report(TgtKeyboard, ModeStatic, maskA),
            Report(TgtKeyboard, ModeStatic, maskB)));
    }

    /// <summary>The keyboard and the lightbar are separate targets (0x21 / 0x65), so a lightbar write must never
    /// coalesce away a pending keyboard one. The zone masks happen to overlap in their low bits, which is exactly
    /// why the target byte has to be in the key.</summary>
    [Fact]
    public void TheKeyboardAndTheLightbarAreDifferentRegions()
    {
        Assert.False(EneHidController.SameRegion(
            Report(TgtKeyboard, ModeStatic, KbAllZones),
            Report(TgtLightbar, ModeStatic, LbAllZones)));

        // Even with the same mask, so only the target byte can separate them.
        Assert.False(EneHidController.SameRegion(
            Report(TgtKeyboard, ModeStatic, KbAllZones),
            Report(TgtLightbar, ModeStatic, KbAllZones)));
    }

    /// <summary>The length guard is what keeps the key's own indexing safe: every report this class builds is
    /// 11 bytes, and a shorter one is answered <c>false</c> rather than throwing an
    /// <c>IndexOutOfRangeException</c> inside the lock on the caller's (UI) thread. Byte 9 is the LAST byte the
    /// key reads, so a guard that is off by one — <c>&gt;= 9</c> — starts throwing on exactly the input it exists
    /// to reject.</summary>
    [Fact]
    public void AReportTooShortToCarryTheKeyIsNeverTheSameRegion()
    {
        byte[] nine = [0xA4, TgtKeyboard, ModeStatic, 0x64, 0x00, 0x01, 0xFF, 0x00, 0x00];   // no zone-mask byte

        Assert.False(EneHidController.SameRegion(nine, nine));                              // identical, still not a region
        Assert.False(EneHidController.SameRegion(nine, Report(TgtKeyboard, ModeStatic, KbAllZones)));
        Assert.False(EneHidController.SameRegion(Report(TgtKeyboard, ModeStatic, KbAllZones), nine));
        Assert.False(EneHidController.SameRegion([], []));                                  // and an empty one is not either
    }
}
