using AcerHelper.Features;

namespace AcerHelper.Vendors.Acer;

// Cross-platform RGB controller: the ENE HID device (VID 0x0CF2 / PID 0x5130, 11-byte feature report id
// 0xA4) — the same controller OpenRGB drives. The packets are identical on every OS; only the transport
// differs, so this file is the Acer packet codec + zone model, and the per-OS partials supply the three
// transport hooks (OpenTransport/SetFeature/Dispose): EneHidController.Windows.cs uses HidSharp (Win32 HID
// API), EneHidController.Linux.cs talks to hidraw directly — HidSharp's Linux enumeration only sees USB
// HID, and on several models (e.g. Nitro AN18-61) this controller hangs off HID-over-I2C. It exposes its
// physical regions as RgbZone bricks: "Keyboard" (multi sub-zone) and, on models that have it, "Lightbar".
// Keyboard brightness read-back isn't on this HID interface — it's the gaming WMI's job (Windows only) —
// so that reader is injected by AcerDevice (null on Linux). Lazily opens the stream; IDisposable.
internal sealed partial class EneHidController : IRgbController
{
    private const int VID = 0x0CF2, PID = 0x5130, FeatureLen = 11;

    // ENE RGB packet: A4 [TGT] [MODE] [BRI 0..0x64] [SPD] [FLAG] c0 c1 c2 [ZONEMASK] 00
    // Colour byte ORDER is mode-dependent (verified on Nitro AN18-61): the arbitrary-colour writes — keyboard
    // STATIC and the lightbar (A4 65) — render the three bytes as R,G,B (UI red 255,0,0 must go out as FF 00 00,
    // else the lightbar shows blue). The OPMODE profile-flash handler is a *separate* firmware path that instead
    // recognises its per-profile palette in B,G,R and whitelists it (see SetProfileFlash). Send() below emits the
    // R,G,B arbitrary-colour order; SetProfileFlash emits B,G,R itself and does not route through Send().
    // Zone masks: keyboard has 4 zones (0x0F = all); the lightbar has 5 (0x1F = all).
    private const byte ReportId = 0xA4, TgtKeyboard = 0x21, TgtLightbar = 0x65, OpMode = 0x06,
                       FlagStatic = 0x01, FlagEffect = 0x02, KbAllZones = 0x0F, LbAllZones = 0x1F, FullBright = 0x64;

    private readonly List<RgbZone> _zones = [];

    public EneHidController(int keyboardZones, bool hasLightbar, Func<int?>? readKeyboardBrightness)
    {
        if (!OpenTransport()) return;   // no ENE interface -> no zones -> composition skips lighting

        _zones.Add(new RgbZone("Keyboard", keyboardZones,
            RgbEffects.Keyboard.Select(e => e.ToModeInfo()).ToList(),
            ApplyKeyboard, ApplyKeyboardZone, readKeyboardBrightness));

        if (hasLightbar)
            _zones.Add(new RgbZone("Lightbar", subZones: 1,
                RgbEffects.Lightbar.Select(e => e.ToModeInfo()).ToList(),
                ApplyLightbar, canFollowProfile: true));
    }

    public IReadOnlyList<RgbZone> Zones => _zones;

    // Acer-owned settings key for the lightbar's "follows performance profile" preference, surfaced through the
    // neutral IRgbController port so the app persists it generically (Settings stays vendor-agnostic). Only
    // present when this device actually has a lightbar (a CanFollowProfile zone).
    public string? ProfileFollowKey => _zones.Any(z => z.CanFollowProfile) ? "acer.lightbarFollowsProfile" : null;

    private bool ApplyKeyboard(RgbModeInfo effect, byte brightness, byte speed, byte direction, AccentColor color)
    {
        var e = (RgbEffect)effect.Handle;
        return Send(TgtKeyboard, e.ModeByte, e.IsEffect, brightness, speed, Dir(e, direction), color, KbAllZones);
    }

    private bool ApplyKeyboardZone(int zoneIndex, byte brightness, AccentColor color)
        => Send(TgtKeyboard, RgbEffects.StaticModeByte, isEffect: false, brightness, speed: 0, FlagStatic, color, (byte)(1 << zoneIndex));

    // The performance-profile "operating mode" flash is a GLOBAL write (keyboard target 0x21, mode 0x06) that
    // paints BOTH the keyboard and the lightbar at once. Unlike the arbitrary-colour paths (which the firmware
    // renders R,G,B), the OPMODE handler recognises its per-profile palette in B,G,R and whitelists it — anything
    // else reverts to amber. So this path is NOT routed through Send() (that applies the R,G,B arbitrary order):
    // it emits the palette colour in B,G,R directly, reproducing byte-for-byte what NitroSense sends. This is how
    // the lightbar gets its per-profile colour when it "follows the profile" — it has no standalone software
    // colour, so we re-send this on each profile switch. See docs/lighting-an18-61.md.
    public bool SetProfileFlash(AccentColor color)
        => SetFeature([ReportId, TgtKeyboard, OpMode, 0x00, 0x00, FlagEffect, color.B, color.G, color.R, KbAllZones, 0x00]);

    private bool ApplyLightbar(RgbModeInfo effect, byte brightness, byte speed, byte direction, AccentColor color)
    {
        var e = (RgbEffect)effect.Handle;
        // The lightbar ignores the HID brightness byte, so emulate brightness by scaling the colour and send
        // full brightness (works for colour modes; self-cycling effects generate their own colours).
        return Send(TgtLightbar, e.ModeByte, e.IsEffect, FullBright, speed, Dir(e, direction), Scale(color, brightness), LbAllZones);
    }

    // Report byte[5] is the effect DIRECTION. For a directional effect (e.g. Wave) it's the user's choice
    // (1/2); otherwise it's the mode default the firmware expects — 0x02 for animated effects, 0x01 for static.
    private static byte Dir(RgbEffect e, byte direction)
        => e.HasDirection ? (direction is 1 or 2 ? direction : FlagStatic)
                          : (e.IsEffect ? FlagEffect : FlagStatic);

    private bool Send(byte target, byte mode, bool isEffect, byte brightness, byte speed, byte direction, AccentColor c, byte zoneMask)
        => SetFeature([ReportId, target, mode, brightness, isEffect ? speed : (byte)0,
                       direction, c.R, c.G, c.B, zoneMask, 0x00]);   // arbitrary colours render R,G,B on the wire

    private static AccentColor Scale(AccentColor c, byte brightness)
    {
        var b = Math.Clamp((int)brightness, 0, 100);
        return new AccentColor((byte)(c.R * b / 100), (byte)(c.G * b / 100), (byte)(c.B * b / 100));
    }

    // ---- transport, per-OS (found device? / send one feature report / release) ----
    private partial bool OpenTransport();
    private partial bool SetFeature(byte[] report);
    public partial void Dispose();
}
