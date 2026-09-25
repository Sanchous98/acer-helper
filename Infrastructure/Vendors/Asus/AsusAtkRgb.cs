using AcerHelper.Domain;
using AcerHelper.Infrastructure.Lighting;

namespace AcerHelper.Infrastructure.Vendors.Asus;

// ASUS Windows keyboard lighting on the ATK channel, behind the existing IRgbDevice/RgbController abstraction —
// NO new domain model.
//
// SCOPE, HONESTLY: the ATK TUF/ROG keyboard-RGB device `ASUS_WMI_DEVID_TUF_RGB_MODE` (0x00100056; the second
// bank is 0x0010005A) takes a 6-byte setting `[0xb4, mode, r, g, b, speed]` (G-Helper `TUFKeyboardRGB`, whose
// mode numbers are the same Aura mode set asus-wmi's `kbd_rgb_mode_store` documents). That is a clean,
// documented codec, so it is implemented. The ROG per-key HID path (a different transport, a different device
// selection and a per-model key layout) is NOT implemented: no verified codec was available, so rather than
// guessing a HID usage page and writing reports to whatever device answers, the ROG path is refused here and
// recorded as unverified in docs/asus-support.md.
//
// UNVERIFIED ON HARDWARE (see AsusAtk.cs): the packet and the mode set are pinned by tests; the keyboard has
// not been lit.

/// <summary>The ATK TUF keyboard-RGB controller: one zone, a mode list, colour + speed applies.</summary>
internal sealed class AsusAtkRgbController : IRgbController
{
    private readonly AsusAtkDevice _atk;
    private readonly List<RgbZone> _zones = [];

    private AsusAtkRgbController(AsusAtkDevice atk)
    {
        _atk = atk;
        // One zone with no sub-zones: the ATK packet addresses the whole keyboard and its colour is global.
        _zones.Add(new RgbZone("Keyboard", subZones: 1, AsusAuraModes.TufBasicModes, ApplyEffect));
    }

    public IReadOnlyList<RgbZone> Zones => _zones;

    /// <summary>Build the controller only when the firmware exposes the TUF RGB device; otherwise null, so the
    /// Lighting section stays hidden rather than offering a control that cannot act.</summary>
    internal static AsusAtkRgbController? TryCreate(AsusAtkDevice atk)
        => atk.Supported(AsusAtk.TufRgbMode) ? new AsusAtkRgbController(atk) : null;

    private bool ApplyEffect(RgbModeInfo effect, byte brightness, byte speed, byte direction, AccentColor colour)
    {
        // 0xb4 = "apply and do not persist to BIOS"; the same byte G-Helper sends.
        var mode = effect.Handle is int code ? code : 0;
        byte[] setting = [0xb4, (byte)mode, colour.R, colour.G, colour.B, SpeedByte(speed)];
        return AsusAtk.DecodeSet(_atk.SetBuffer(AsusAtk.TufRgbMode, setting)) == 1;
    }

    /// <summary>The 0..100 speed slider as the packet's three steps.</summary>
    private static byte SpeedByte(byte percent) => (byte)(percent < 34 ? 0 : percent < 67 ? 1 : 2);

    /// <summary>No handle of ours is held; the ATK channel belongs to the device.</summary>
    public void Dispose() { }
}
