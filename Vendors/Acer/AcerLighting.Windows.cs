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

    private readonly HidDevice? _device;
    private HidStream? _stream;

    /// <summary>Per-model RGB layout (from the quirks config): keyboard zone count and whether a
    /// lightbar exists. Presence of RGB itself is probed (the ENE device is found or not).</summary>
    public AcerLighting(int keyboardZones, bool hasLightbar)
    {
        KeyboardZones = keyboardZones;
        LightbarEffects = hasLightbar ? RgbEffects.Lightbar.Select(e => e.ToModeInfo()).ToList() : [];
        try
        {
            foreach (HidDevice d in DeviceList.Local.GetHidDevices(VID, PID))
            {
                try { if (d.GetMaxFeatureReportLength() == FEATURE_LEN) { _device = d; break; } }
                catch { /* skip interfaces we can't query */ }
            }
            if (_device == null) LastError = $"ENE lighting interface ({VID:X4}:{PID:X4}, {FEATURE_LEN}-byte feature) not found.";
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    /// <summary>True if the ENE HID device was found (composition gate).</summary>
    public bool Available => _device != null;
    public string? LastError { get; private set; }

    public IReadOnlyList<RgbModeInfo> KeyboardEffects { get; } = RgbEffects.Keyboard.Select(e => e.ToModeInfo()).ToList();
    public IReadOnlyList<RgbModeInfo> LightbarEffects { get; }
    public int KeyboardZones { get; }

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
        return Send(TGT_LIGHTBAR, e.ModeByte, e.IsEffect, brightness, speed, color, LB_ZONE);
    }

    private bool Send(byte target, byte modeByte, bool isEffect, byte brightness, byte speed, AccentColor color, byte zoneMask)
    {
        if (_device == null) return false;
        try
        {
            _stream ??= _device.Open();
            byte[] buf = new byte[FEATURE_LEN];
            buf[0]  = REPORT_ID;
            buf[1]  = target;
            buf[2]  = modeByte;
            buf[3]  = brightness;                 // 0..0x64
            buf[4]  = isEffect ? speed : (byte)0;
            buf[5]  = isEffect ? FLAG_EFFECT : FLAG_STATIC;
            buf[6]  = color.R;
            buf[7]  = color.G;
            buf[8]  = color.B;
            buf[9]  = zoneMask;
            buf[10] = 0x00;
            _stream.SetFeature(buf);
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    public void Dispose() => _stream?.Dispose();
}
