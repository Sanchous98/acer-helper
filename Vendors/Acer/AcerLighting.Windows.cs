using System.Management;
using AcerHelper.Features;
using AcerHelper.Os;
using HidSharp;

namespace AcerHelper.Vendors.Acer;

/// <summary>
/// RGB control of the Acer Nitro keyboard + lightbar over the ENE HID device (VID 0x0CF2 /
/// PID 0x5130, 11-byte feature report id 0xA4). HidSharp talks to the device directly (it is
/// cross-platform, so a Linux variant can reuse this logic later). Same protocol as OpenRGB.
///
///   Packet: A4 [TGT] [MODE] [BRI 0..0x64] [SPD] [FLAG] R G B [ZONEMASK] 00
/// </summary>
public sealed class AcerLighting : ILighting, IDisposable
{
    // Standard Acer ENE keyboard controller. If a model ever uses a different id, add a probe;
    // these have held across the Acer gaming line.
    private const int VID         = 0x0CF2;
    private const int PID         = 0x5130;
    private const int FEATURE_LEN = 11;

    private const byte REPORT_ID    = 0xA4;
    private const byte TGT_KEYBOARD = 0x21;
    private const byte TGT_LIGHTBAR = 0x65;
    private const byte FLAG_STATIC  = 0x01;
    private const byte FLAG_EFFECT  = 0x02;
    private const byte KB_ALL_ZONES = 0x0F;
    private const byte LB_ZONE      = 0x01;
    private const byte FULL_BRIGHT  = 0x64;   // 100%; the lightbar ignores the HID brightness byte

    // GetGamingKBBacklight query selector + output layout (probed on the Nitro 18, method id 21):
    //   in  gmInput  = 1 (0/2 return gmReturn=1 "unsupported")
    //   out gmOutput = byte[15], gmReturn = 0 on success; gmOutput[2] is the brightness (0..100).
    private const uint KbBacklightQuery = 1;
    private const int  KbBrightnessByte = 2;

    private readonly HidDevice? _device;
    private readonly WmiInvoker? _gaming;   // brightness read-back path (RGB itself is the write-only HID below)
    private HidStream? _stream;

    /// <summary>True if the ENE HID device was found (composition gate).</summary>
    public bool Available => _device != null;
    public string? LastError { get; private set; }

    public IReadOnlyList<RgbModeInfo> KeyboardEffects { get; } = RgbEffects.Keyboard.Select(e => e.ToModeInfo()).ToList();
    public IReadOnlyList<RgbModeInfo> LightbarEffects { get; }
    public int KeyboardZones { get; }

    /// <summary>Per-model RGB layout (from the quirks config): keyboard zone count and whether a
    /// lightbar exists. Presence of RGB itself is probed (the ENE device is found or not).</summary>
    public AcerLighting(int keyboardZones, bool hasLightbar, WmiInvoker? gaming = null)
    {
        KeyboardZones = keyboardZones;
        _gaming = gaming;
        LightbarEffects = hasLightbar ? RgbEffects.Lightbar.Select(e => e.ToModeInfo()).ToList() : [];
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(VID, PID))
            {
                try
                {
                    if (d.GetMaxFeatureReportLength() != FEATURE_LEN) continue;
                    _device = d; break;
                }
                catch { /* skip interfaces we can't query */ }
            }
            if (_device == null) LastError = $"ENE lighting interface ({VID:X4}:{PID:X4}, {FEATURE_LEN}-byte feature) not found.";
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public bool ApplyKeyboard(RgbModeInfo effect, byte brightness, byte speed, AccentColor color)
    {
        var e = (RgbEffect)effect.Handle;
        return Send(TGT_KEYBOARD, e.ModeByte, e.IsEffect, brightness, speed, color, KB_ALL_ZONES);
    }

    public bool ApplyKeyboardZone(int zoneIndex, byte brightness, AccentColor color)
        => Send(TGT_KEYBOARD, RgbEffects.StaticModeByte, isEffect: false, brightness, speed: 0, color, (byte)(1 << zoneIndex));

    public bool ApplyLightbar(RgbModeInfo effect, byte brightness, byte speed, AccentColor color)
    {
        var e = (RgbEffect)effect.Handle;
        // The lightbar's firmware ignores the HID brightness byte (verified: no impl — ours, the OpenRGB
        // plugin, or Acer's own service — can dim it via that byte). So emulate brightness by scaling the
        // colour channels and send full brightness. Works for colour modes (Static/Breathing); self-cycling
        // effects (Neon/Wave) generate their own colours in firmware and can't be dimmed this way.
        return Send(TGT_LIGHTBAR, e.ModeByte, e.IsEffect, FULL_BRIGHT, speed, Scale(color, brightness), LB_ZONE);
    }

    /// <summary>Emulate brightness (0..100) by scaling the RGB channels — for devices/targets that ignore
    /// the hardware brightness byte (the lightbar).</summary>
    private static AccentColor Scale(AccentColor c, byte brightness)
    {
        int b = Math.Clamp((int)brightness, 0, 100);
        return new AccentColor((byte)(c.R * b / 100), (byte)(c.G * b / 100), (byte)(c.B * b / 100));
    }

    private bool Send(byte target, byte modeByte, bool isEffect, byte brightness, byte speed, AccentColor color, byte zoneMask)
    {
        if (_device == null) return false;
        try
        {
            _stream ??= _device.Open();
            _stream.SetFeature([
                REPORT_ID, 
                target, 
                modeByte, 
                brightness,
                isEffect ? speed : (byte)0,
                isEffect ? FLAG_EFFECT : FLAG_STATIC,
                color.R,
                color.G,
                color.B,
                zoneMask,
                0x00
            ]);
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    public int? ReadKeyboardBrightness()
    {
        if (_gaming is not { Available: true }) return null;
        try
        {
            using ManagementBaseObject outp = _gaming.Invoke("GetGamingKBBacklight", new Dictionary<string, object>
            {
                ["gmInput"] = KbBacklightQuery,
            });
            if (Convert.ToByte(outp["gmReturn"]) != 0) return null;   // firmware reports the query unsupported
            var data = (byte[])outp["gmOutput"];
            if (data.Length <= KbBrightnessByte) return null;
            return Math.Clamp(data[KbBrightnessByte], (byte)0, (byte)100);
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public void Dispose() => _stream?.Dispose();
}
