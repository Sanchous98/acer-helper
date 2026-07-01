using AcerHelper.Features;
using HidSharp;

namespace AcerHelper.Vendors.Acer;

// Cross-platform RGB controller: the ENE HID device (VID 0x0CF2 / PID 0x5130, 11-byte feature report id
// 0xA4) — the same controller OpenRGB drives. USB HID is OS-agnostic (HidSharp uses the Win32 HID API on
// Windows and hidraw on Linux), so this ONE class serves both platforms — no per-OS split; the packets are
// identical. It IS the transport (finds the device, sends feature reports) AND the Acer packet codec, and it
// exposes its physical regions as RgbZone bricks: "Keyboard" (multi sub-zone) and, on models that have it,
// "Lightbar". Keyboard brightness read-back isn't on this HID interface — it's the gaming WMI's job (Windows
// only) — so that reader is injected by AcerDevice (null on Linux). Lazily opens the stream; IDisposable.
// NOTE (Linux): reaching hidraw without root needs a udev rule, else enumeration succeeds but SetFeature fails.
internal sealed class EneHidController : IRgbController
{
    private const int VID = 0x0CF2, PID = 0x5130, FeatureLen = 11;

    // ENE RGB packet: A4 [TGT] [MODE] [BRI 0..0x64] [SPD] [FLAG] R G B [ZONEMASK] 00
    private const byte ReportId = 0xA4, TgtKeyboard = 0x21, TgtLightbar = 0x65,
                       FlagStatic = 0x01, FlagEffect = 0x02, KbAllZones = 0x0F, LbZone = 0x01, FullBright = 0x64;

    private readonly HidDevice? _device;
    private HidStream? _stream;
    private readonly List<RgbZone> _zones = [];

    public EneHidController(int keyboardZones, bool hasLightbar, Func<int?>? readKeyboardBrightness)
    {
        _device = FindDevice();
        if (_device == null) return;   // no ENE interface -> no zones -> composition skips lighting

        _zones.Add(new RgbZone("Keyboard", keyboardZones,
            RgbEffects.Keyboard.Select(e => e.ToModeInfo()).ToList(),
            ApplyKeyboard, ApplyKeyboardZone, readKeyboardBrightness));

        if (hasLightbar)
            _zones.Add(new RgbZone("Lightbar", subZones: 1,
                RgbEffects.Lightbar.Select(e => e.ToModeInfo()).ToList(),
                ApplyLightbar));
    }

    public IReadOnlyList<RgbZone> Zones => _zones;

    private bool ApplyKeyboard(RgbModeInfo effect, byte brightness, byte speed, AccentColor color)
    {
        var e = (RgbEffect)effect.Handle;
        return Send(TgtKeyboard, e.ModeByte, e.IsEffect, brightness, speed, color, KbAllZones);
    }

    private bool ApplyKeyboardZone(int zoneIndex, byte brightness, AccentColor color)
        => Send(TgtKeyboard, RgbEffects.StaticModeByte, isEffect: false, brightness, speed: 0, color, (byte)(1 << zoneIndex));

    private bool ApplyLightbar(RgbModeInfo effect, byte brightness, byte speed, AccentColor color)
    {
        var e = (RgbEffect)effect.Handle;
        // The lightbar ignores the HID brightness byte, so emulate brightness by scaling the colour and send
        // full brightness (works for colour modes; self-cycling effects generate their own colours).
        return Send(TgtLightbar, e.ModeByte, e.IsEffect, FullBright, speed, Scale(color, brightness), LbZone);
    }

    private bool Send(byte target, byte mode, bool isEffect, byte brightness, byte speed, AccentColor c, byte zoneMask)
    {
        if (_device == null) return false;
        try
        {
            (_stream ??= _device.Open()).SetFeature([ReportId, target, mode, brightness, isEffect ? speed : (byte)0,
                                                     isEffect ? FlagEffect : FlagStatic, c.R, c.G, c.B, zoneMask, 0x00]);
            return true;
        }
        catch { return false; }
    }

    private static AccentColor Scale(AccentColor c, byte brightness)
    {
        var b = Math.Clamp((int)brightness, 0, 100);
        return new AccentColor((byte)(c.R * b / 100), (byte)(c.G * b / 100), (byte)(c.B * b / 100));
    }

    private static HidDevice? FindDevice()
    {
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(VID, PID))
                try { if (d.GetMaxFeatureReportLength() == FeatureLen) return d; }
                catch { /* skip interfaces we can't query */ }
        }
        catch { /* no device */ }
        return null;
    }

    public void Dispose() => _stream?.Dispose();
}
