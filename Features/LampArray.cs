namespace AcerHelper.Features;

// ---------------------------------------------------------------------------------------------------------
// HID LampArray — the vendor- and OS-neutral half of the "translation layer" that lets Windows Dynamic
// Lighting (Settings > Personalisation > Dynamic Lighting) and any LampArray-aware app paint this laptop's
// keyboard. The whole picture is in docs/lamparray.md; the short version:
//
//   Windows / an app  --HID feature reports-->  virtual HID LampArray device (driver/, VHF-based)
//                     --ILampArrayTransport-->  LampArrayBridge  -->  RgbZone.ApplySubZone / ApplyEffect
//
// Windows only enumerates lighting devices that expose the HID LampArray usage page (0x59) — there is NO
// user-mode "register a LampArray" API — so the device itself has to be a small kernel driver. That is
// exactly what Logitech's translation layer is (logi_lamparray.sys + vhf as a lower filter + a user-mode
// service doing the protocol conversion). Everything ABOVE the driver lives here and in LampArrayBridge, so
// the LampArray semantics, the geometry and the rate limiting can be iterated on in C# without rebuilding —
// let alone re-signing — a driver.
//
// Field/report semantics follow "Lighting And Illumination Page (0x59)" of HID Usage Tables 1.4 and
// Microsoft's reference implementation (github.com/microsoft/ArduinoHidForWindows, MIT). The units ON THE
// WIRE are micrometres and microseconds — hence the µm/µs in this model; conversion from mm/ms happens here,
// once, so neither the driver nor the bridge has to think about it.
// ---------------------------------------------------------------------------------------------------------

/// <summary>What kind of thing the lamps are attached to. Reported in the LampArrayAttributes report;
/// Windows uses it to group devices and to pick sensible effects (a Keyboard gets keyboard-shaped effects).
/// Values are the HID spec's, not ours.</summary>
public enum LampArrayKind
{
    Undefined = 0, Keyboard = 1, Mouse = 2, GameController = 3, Peripheral = 4,
    Scene = 5, Notification = 6, Chassis = 7, Wearable = 8, Furniture = 9, Art = 10,
}

/// <summary>What a lamp is for (informational — the host may use it to decide whether an effect should
/// touch it). Spec bit values.</summary>
[Flags]
public enum LampPurposes
{
    Undefined = 0, Control = 1, Accent = 2, Branding = 4, Status = 8, Illumination = 16, Presentation = 32,
}

/// <summary>One lamp's colour as the host sends it: 8 bits per channel plus an intensity channel.</summary>
public readonly record struct LampColor(byte R, byte G, byte B, byte Intensity)
{
    /// <summary>The colour to actually render. We advertise <c>IntensityLevelCount = 1</c> (this hardware has
    /// no per-lamp gain — brightness is a per-write byte for the whole keyboard), which per spec means the
    /// intensity channel degenerates to on/off and the host bakes brightness into RGB. So: intensity 0 is
    /// "lamp off", anything else means "render RGB as sent".</summary>
    public AccentColor Rgb => Intensity == 0 ? new AccentColor(0, 0, 0) : new AccentColor(R, G, B);

    /// <summary>Near-equality, used to drop no-op writes. The host re-sends whole frames at its own frame
    /// rate and a gradient/breathing effect walks a channel one step at a time; on a HID-over-I2C controller
    /// every avoided report matters (see LampArrayBridge), so sub-perceptual deltas don't earn a write.</summary>
    public bool IsCloseTo(LampColor o, int epsilon)
        => Math.Abs(R - o.R) <= epsilon && Math.Abs(G - o.G) <= epsilon && Math.Abs(B - o.B) <= epsilon
           && (Intensity == 0) == (o.Intensity == 0);
}

/// <summary>Everything the host must be told about one lamp (the LampAttributesResponse report). Positions
/// are µm from the device's top-left corner, latency in µs — the units the wire uses.</summary>
public sealed record LampInfo(
    int XUm, int YUm, int ZUm,
    int UpdateLatencyUs,
    LampPurposes Purposes,
    bool IsProgrammable = true,
    byte InputBinding = 0);

/// <summary>Where a lamp physically lives in this app's RGB model: which <see cref="RgbZone"/> of the device,
/// and which sub-zone inside it. This is the mapping the bridge walks to turn a lamp frame into zone writes.</summary>
public readonly record struct LampTarget(int ZoneIndex, int SubZone);

/// <summary>A complete lamp frame from the host. <see cref="AutonomousMode"/> true means the host has
/// RELEASED control ("device, paint yourself again") — that is the signal for the app to take its own
/// lighting back; false means the host owns the surface and <see cref="Colors"/> is authoritative.</summary>
public readonly record struct LampFrame(uint Sequence, bool AutonomousMode, LampColor[] Colors);

/// <summary>The transport that makes a <see cref="LampArrayLayout"/> visible to the OS as a real HID
/// LampArray device and hands back the frames the host writes to it. On Windows this is the VHF driver
/// channel (Vendors/Generic/LampArrayTransport.Windows.cs); it is <c>null</c> where no such transport exists
/// (Linux would use /dev/uhid — see docs/lamparray.md), which simply leaves the feature absent.</summary>
public interface ILampArrayTransport : IDisposable
{
    /// <summary>Last failure, for the UI. Null after a successful call.</summary>
    string? LastError { get; }

    /// <summary>Publish the layout and make the virtual device appear (Windows then lists it under Dynamic
    /// Lighting). False = unavailable (driver not installed, no permission); see <see cref="LastError"/>.</summary>
    bool Start(LampArrayLayout layout);

    /// <summary>Make the virtual device disappear again. Idempotent.</summary>
    void Stop();

    /// <summary>Block until the host completes a lamp frame; false once the transport is stopped/disposed or
    /// the channel breaks. Frames are LAST-ONE-WINS: a consumer slower than the host never sees a backlog,
    /// only the newest state — which is what makes the bridge's rate limiting safe.</summary>
    bool WaitFrame(out LampFrame frame);
}

/// <summary>The device as the host sees it: a bounding box, a kind, and N lamps with positions — plus (for
/// our own use) the zone/sub-zone each lamp maps onto. Built from whatever <see cref="RgbZone"/>s the active
/// controllers advertise, so it adapts to the model instead of hard-coding a keyboard.</summary>
public sealed class LampArrayLayout
{
    // Nominal physical geometry. The Acer controllers expose ZONES, not keys, and nothing in firmware, WMI or
    // acer-models.json reports the keyboard's real dimensions — so we describe a plausible full-size gaming
    // keyboard and spread the zones across it. What matters to the host is not absolute accuracy but that the
    // lamps are laid out left-to-right in the right proportions: that is what makes a "wave" sweep across the
    // keyboard the right way round and a "gradient" fall in the right direction.
    private const int KeyboardWidthMm = 330, KeyboardHeightMm = 110;
    // A secondary zone (the Acer lightbar) sits on the front edge, below the keyboard: a shallow strip. Giving
    // it its own Y band (rather than folding it into the keyboard rectangle) keeps spatial effects sane — a
    // vertical wipe reaches it last, like the real hardware.
    private const int StripHeightMm = 12, StripGapMm = 18;
    // Every lamp reports the same latency: the ENE write path is a queued, paced feature report (see
    // EneHidController), so ~30 ms from "host wrote a frame" to "LEDs changed" is honest.
    private const int LampLatencyMs = 30;

    private LampArrayLayout(LampArrayKind kind, int widthUm, int heightUm, int depthUm, int minUpdateIntervalUs,
                            IReadOnlyList<LampInfo> lamps, IReadOnlyList<LampTarget> targets,
                            IReadOnlyList<RgbZone> zones)
    {
        Kind = kind;
        WidthUm = widthUm; HeightUm = heightUm; DepthUm = depthUm;
        MinUpdateIntervalUs = minUpdateIntervalUs;
        Lamps = lamps; Targets = targets; Zones = zones;
    }

    public LampArrayKind Kind { get; }
    public int WidthUm { get; }
    public int HeightUm { get; }
    public int DepthUm { get; }

    /// <summary>The shortest interval between updates we want the host to use, in µs. This is advertised, not
    /// merely enforced: a well-behaved host (Dynamic Lighting, and any app that honours
    /// <c>LampArray.MinUpdateInterval</c>) will not push frames faster, which is the cheapest possible fix for
    /// a controller that cannot take 60 Hz. The bridge enforces the same number regardless.</summary>
    public int MinUpdateIntervalUs { get; }

    public IReadOnlyList<LampInfo> Lamps { get; }

    /// <summary>Per lamp, the zone + sub-zone it drives (same order as <see cref="Lamps"/>).</summary>
    public IReadOnlyList<LampTarget> Targets { get; }

    /// <summary>The zones referenced by <see cref="Targets"/>, by index.</summary>
    public IReadOnlyList<RgbZone> Zones { get; }

    public int LampCount => Lamps.Count;

    /// <summary>Describe the device's controllable surface as a lamp array. <paramref name="include"/> filters
    /// out zones the app must not drive (the Acer lightbar while it "follows the performance profile" — the
    /// firmware owns it then). Returns null when nothing is left to expose.
    ///
    /// Layout rule: the FIRST included zone is treated as the keyboard and gets the full keyboard rectangle,
    /// its sub-zones spread evenly left-to-right; every further zone becomes a strip below it. That is
    /// vendor-neutral (no zone-name matching) and matches the physical reality of these laptops, where the
    /// multi-zone surface is the keyboard and anything extra is a front/rear lightbar.</summary>
    public static LampArrayLayout? Build(IRgbDevice rgb, Func<RgbZone, bool>? include = null,
                                         int minUpdateIntervalMs = 100)
    {
        var zones = rgb.Zones.Where(z => z.Effects.Count > 0 && (include?.Invoke(z) ?? true)).ToList();
        if (zones.Count == 0) return null;

        var lamps = new List<LampInfo>();
        var targets = new List<LampTarget>();
        int nextStripY = KeyboardHeightMm + StripGapMm;
        int bottomMm = KeyboardHeightMm;

        for (var zi = 0; zi < zones.Count; zi++)
        {
            var zone = zones[zi];
            // A zone we cannot address per sub-zone is one lamp — painting "its" lamp paints the whole region.
            var count = zone.HasSubZones ? Math.Max(1, zone.SubZones) : 1;
            var (top, height) = zi == 0 ? (0, KeyboardHeightMm) : (nextStripY, StripHeightMm);
            if (zi > 0) { nextStripY += StripHeightMm + StripGapMm; bottomMm = top + height; }

            for (var i = 0; i < count; i++)
            {
                // Centre of this sub-zone's column: zones run left-to-right (Acer zone mask bit 0 = leftmost).
                var xMm = KeyboardWidthMm * (2 * i + 1) / (2 * count);
                lamps.Add(new LampInfo(
                    Mm(xMm), Mm(top + height / 2), 0, Ms(LampLatencyMs),
                    // The keyboard's lamps light the keys (Illumination) and are decorative (Accent); a strip
                    // is purely decorative. Purposes are advisory, but honest values cost nothing.
                    zi == 0 ? LampPurposes.Illumination | LampPurposes.Accent : LampPurposes.Accent));
                targets.Add(new LampTarget(zi, i));
            }
        }

        // Kind: a multi-zone first surface IS the keyboard; a single-lamp-only device is better described as a
        // chassis light than as a keyboard (it would otherwise attract key-shaped effects it cannot render).
        var kind = zones[0].HasSubZones ? LampArrayKind.Keyboard : LampArrayKind.Chassis;

        return new LampArrayLayout(kind, Mm(KeyboardWidthMm), Mm(bottomMm), Mm(1),
                                   Ms(minUpdateIntervalMs), lamps, targets, zones);
    }

    private static int Mm(int mm) => mm * 1000;   // millimetres -> micrometres (wire unit)
    private static int Ms(int ms) => ms * 1000;   // milliseconds -> microseconds (wire unit)
}
