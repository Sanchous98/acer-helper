namespace AcerHelper.Features;

// A small OpenRGB-style RGB framework. A device's lighting is assembled from bricks:
//   RgbZone        — a controllable region (its effect list + apply ops), optionally split into sub-zones.
//   IRgbController — a hardware transport that produces the zones it can drive (ENE HID, Linuwu sysfs, …).
//   RgbDevice      — an IRgbDevice assembled by concatenating one or more controllers' zones.
// The UI binds to IRgbDevice.Zones and renders one panel per zone, so it adapts to whatever the active
// controllers advertise — no keyboard/lightbar assumptions baked into the port.

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
/// capability port — <c>null</c> on <see cref="IDevice.Lighting"/> means no RGB; an empty zone list means
/// the transport was present but exposed nothing.</summary>
public interface IRgbDevice
{
    IReadOnlyList<RgbZone> Zones { get; }

    /// <summary>Paint the device's "operating mode" indicator (a global flash across the lit surfaces) with a
    /// colour, if any controller supports it. Used to reproduce the per-performance-profile lightbar colour on
    /// a profile switch (the firmware only accepts its per-profile palette here). Returns false if unsupported.</summary>
    bool SetProfileFlash(AccentColor color);

    /// <summary>The settings key under which the "follows performance profile" preference for a profile-indicator
    /// zone is stored, or null if the device has none. The key string is owned by the backend (opaque here), so
    /// the flag stays vendor-scoped while the app plumbing (LaptopService.GetDeviceFlag) stays generic.</summary>
    string? ProfileFollowKey { get; }
}

/// <summary>A hardware RGB transport brick that produces the zones it can drive. One per transport (ENE HID
/// on Windows, Linuwu sysfs on Linux, a future LampArray, …); a device may aggregate several. IDisposable
/// for controllers holding a handle (e.g. a HID stream).</summary>
public interface IRgbController : IDisposable
{
    IReadOnlyList<RgbZone> Zones { get; }

    /// <summary>Paint the "operating mode" indicator colour (see <see cref="IRgbDevice.SetProfileFlash"/>).
    /// Default: unsupported (controllers without a profile indicator don't override this).</summary>
    bool SetProfileFlash(AccentColor color) => false;

    /// <summary>Settings key for this controller's "follows performance profile" preference (see
    /// <see cref="IRgbDevice.ProfileFollowKey"/>); null when it has no profile-indicator zone.</summary>
    string? ProfileFollowKey => null;
}

/// <summary>Assembles an <see cref="IRgbDevice"/> from one or more controllers by concatenating their zones,
/// and owns them (disposes on device teardown).</summary>
public sealed class RgbDevice(params IRgbController[] controllers) : IRgbDevice, IDisposable
{
    public IReadOnlyList<RgbZone> Zones { get; } = controllers.SelectMany(c => c.Zones).ToList();

    public bool SetProfileFlash(AccentColor color)
    {
        var ok = false;
        foreach (var c in controllers) ok |= c.SetProfileFlash(color);
        return ok;
    }

    public string? ProfileFollowKey => controllers.Select(c => c.ProfileFollowKey).FirstOrDefault(k => k != null);

    public void Dispose()
    {
        foreach (var c in controllers)
            try { c.Dispose(); } catch { /* best-effort teardown */ }
    }
}
