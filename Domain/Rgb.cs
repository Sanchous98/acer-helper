namespace AcerHelper.Domain;

// The model half of a small OpenRGB-style RGB framework. A device's lighting is assembled from bricks, and
// these are the two the application reasons about:
//   RgbZone    — a controllable region (its effect list + apply ops), optionally split into sub-zones.
//   IRgbDevice — the device's RGB surface, built by concatenating the zones its transports produce.
// The UI binds to IRgbDevice.Zones and renders one panel per zone, so it adapts to whatever the active
// controllers advertise — no keyboard/lightbar assumptions baked into the port.
// The transport half of the framework — IRgbController, the brick that DRIVES hardware, and RgbDevice, which
// concatenates controllers' zones and owns them — is Infrastructure/Lighting/RgbController.cs: assembling
// transports and releasing their handles is integration, and the domain names neither of them.

/// <summary>One controllable lighting region: an effect list plus apply operations. A zone may be split
/// into <see cref="SubZones"/> individually-addressable regions (e.g. a 4-zone keyboard); when it can't,
/// <see cref="SubZones"/> is 1 and <see cref="ApplySubZone"/> is a no-op. Apply is live (no confirm).</summary>
public sealed class RgbZone(
    string name,
    int subZones,
    IReadOnlyList<RgbModeInfo> effects,
    Func<RgbModeInfo, byte, byte, byte, AccentColor, bool> applyEffect,   // (effect, brightness, speed, direction, colour)
    Func<int, byte, AccentColor, bool>? applySubZone = null,
    Func<int?>? readBrightness = null,
    bool canFollowProfile = false)
{
    public string Name => name;
    public int SubZones => subZones;
    public IReadOnlyList<RgbModeInfo> Effects => effects;

    /// <summary>True for a zone the firmware already paints per performance-profile (the Acer lightbar). Such a
    /// zone can be left entirely to the firmware — showing its per-profile palette colour seamlessly — instead
    /// of being user-driven; the app exposes a single "follows performance profile" switch for these zones and,
    /// while it's on, does not build a panel for them or send them anything.</summary>
    public bool CanFollowProfile => canFollowProfile;

    /// <summary>True when the zone exposes per-sub-zone colours (multi-zone + a sub-zone applier).</summary>
    public bool HasSubZones => applySubZone != null && subZones > 1;

    public bool ApplyEffect(RgbModeInfo effect, byte brightness, byte speed, byte direction, AccentColor color)
        => applyEffect(effect, brightness, speed, direction, color);

    public bool ApplySubZone(int index, byte brightness, AccentColor color)
        => applySubZone?.Invoke(index, brightness, color) ?? false;

    /// <summary>The zone's brightness the firmware currently reports (0..100), or null if it can't be read.
    /// The RGB itself is usually a write-only transport, but some zones expose a brightness read (e.g. the
    /// Acer keyboard via the gaming WMI) so the UI can sync to out-of-band Fn-key changes.</summary>
    public int? ReadBrightness() => readBrightness?.Invoke();
}

/// <summary>A device's RGB surface: a set of independently-controllable <see cref="RgbZone"/>s. This is the
/// capability port — a machine whose <c>Lighting</c> slot is <c>null</c> has no RGB; an empty zone list means
/// the transport was present but exposed nothing.</summary>
public interface IRgbDevice
{
    IReadOnlyList<RgbZone> Zones { get; }

    /// <summary>Paint the device's "operating mode" indicator (a global flash across the lit surfaces) with a
    /// colour, if any controller supports it. Used to reproduce the per-performance-profile lightbar colour on
    /// a profile switch (the firmware only accepts its per-profile palette here). Returns false if unsupported.</summary>
    bool SetProfileFlash(AccentColor color);

    /// <summary>Turn every controllable zone off (brightness 0). Used to blank a backlight that's hidden — e.g.
    /// the lid is shut but the machine stays awake in clamshell (keep-awake) mode — without disturbing the app's
    /// stored per-zone state; restore by re-applying the current mode's lighting. Returns false if no controller
    /// supports it.</summary>
    bool Blank();

    /// <summary>The settings key under which the "follows performance profile" preference for a profile-indicator
    /// zone is stored, or null if the device has none. The key string is owned by the backend (opaque here), so
    /// the flag stays vendor-scoped while the app plumbing (LaptopService.GetDeviceFlag) stays generic.</summary>
    string? ProfileFollowKey { get; }
}
