using System.Drawing;
using HidSharp;

namespace AcerHelper;

/// <summary>
/// Direct RGB control of the Acer Nitro keyboard + lightbar over the ENE HID
/// device (VID 0x0CF2 / PID 0x5130, 11-byte feature report id 0xA4). No Acer
/// service required. Same protocol as the OpenRGB plugin.
///
///   Packet: A4 [TGT] [MODE] [BRI 0..0x64] [SPD] [FLAG] R G B [ZONEMASK] 00
///     TGT      : 0x21 keyboard, 0x65 lightbar
///     FLAG     : 0x01 static, 0x02 effect
///     ZONEMASK : keyboard zone N = 1&lt;&lt;(N-1); 0x0F = all 4 zones; lightbar 0x01
/// </summary>
public sealed class AcerEneRgb : IDisposable
{
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

    public bool Available => _device != null;
    public string? LastError { get; private set; }

    public AcerEneRgb()
    {
        try
        {
            foreach (HidDevice d in DeviceList.Local.GetHidDevices(VID, PID))
            {
                try
                {
                    if (d.GetMaxFeatureReportLength() == FEATURE_LEN) { _device = d; break; }
                }
                catch { /* skip interfaces we can't query */ }
            }
            if (_device == null)
                LastError = "ENE lighting interface (0CF2:5130, 11-byte feature) not found.";
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    private bool Send(byte target, byte modeByte, bool isEffect, byte brightness, byte speed, Color color, byte zoneMask)
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

    /// <summary>Apply to the whole keyboard (all 4 zones, one colour).</summary>
    public bool ApplyKeyboard(byte modeByte, bool isEffect, byte brightness, byte speed, Color color)
        => Send(TGT_KEYBOARD, modeByte, isEffect, brightness, speed, color, KB_ALL_ZONES);

    /// <summary>Apply to a single keyboard zone (0..3, left to right).</summary>
    public bool ApplyKeyboardZone(int zoneIndex, byte modeByte, bool isEffect, byte brightness, byte speed, Color color)
        => Send(TGT_KEYBOARD, modeByte, isEffect, brightness, speed, color, (byte)(1 << zoneIndex));

    public bool ApplyLightbar(byte modeByte, bool isEffect, byte brightness, byte speed, Color color)
        => Send(TGT_LIGHTBAR, modeByte, isEffect, brightness, speed, color, LB_ZONE);

    public void Dispose() => _stream?.Dispose();
}
