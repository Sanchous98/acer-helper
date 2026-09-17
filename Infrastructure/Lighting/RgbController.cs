using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Lighting;

// The transport half of the small OpenRGB-style RGB framework (its model half — RgbZone, IRgbDevice — is
// Domain/Rgb.cs, because that is what the UI binds to and what the app reasons about):
//   IRgbController — a hardware transport that produces the zones it can drive (ENE HID, a future LampArray, …).
//   RgbDevice      — an IRgbDevice assembled by concatenating one or more controllers' zones.
// Both drive hardware and own handles, which is why they live here rather than with the ports they satisfy;
// Domain names neither of them (only the IRgbDevice they produce).

/// <summary>A hardware RGB transport brick that produces the zones it can drive. One per transport (ENE HID
/// on both Windows and Linux — hidraw there, no kernel module, a future LampArray, …); a device may aggregate
/// several. IDisposable for controllers holding a handle (e.g. a HID stream).</summary>
public interface IRgbController : IDisposable
{
    IReadOnlyList<RgbZone> Zones { get; }

    /// <summary>Paint the "operating mode" indicator colour (see <see cref="IRgbDevice.SetProfileFlash"/>).
    /// Default: unsupported (controllers without a profile indicator don't override this).</summary>
    bool SetProfileFlash(AccentColor color) => false;

    /// <summary>Turn every zone this controller drives off (see <see cref="IRgbDevice.Blank"/>). Default:
    /// unsupported (no-op) — controllers that can blank their zones override this.</summary>
    bool Blank() => false;

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
        return controllers.Aggregate(false, (current, c) => current | c.SetProfileFlash(color));
    }

    public bool Blank()
    {
        return controllers.Aggregate(false, (current, c) => current | c.Blank());
    }

    public string? ProfileFollowKey => controllers.Select(c => c.ProfileFollowKey).FirstOrDefault(k => k != null);

    public void Dispose()
    {
        foreach (var c in controllers)
            try { c.Dispose(); } catch { /* best-effort teardown */ }
    }
}
