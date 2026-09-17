using AcerHelper.Domain;

namespace AcerHelper.Application;

// Application's side of the one seam where the app publishes a virtual lighting device. The implementation is
// the LampArray bridge (Infrastructure/Lighting/LampArrayBridge.cs): it opens the OS channel, runs the worker
// that turns host lamp frames into zone writes, and arbitrates who owns the backlight. Application needs two
// things from it — switching it on and off, and knowing when to yield — so that is all these contracts
// declare. They are the reason `LaptopService` names no Infrastructure type: without them the constructor's
// transport parameter and the `LampArray` property would both point Application upward, at the layer that is
// supposed to depend on it and not the other way round.

/// <summary>The virtual lighting surface this application publishes so an OS or a third-party app can paint the
/// machine's backlight (Windows Dynamic Lighting, any LampArray-aware app — see docs/lamparray.md). The LampArray
/// bridge is the only implementation. It owns a worker thread and the OS channel, so it is
/// <see cref="IDisposable"/>: disposing it takes the surface down and releases the transport with it.</summary>
public interface IDynamicLighting : IDisposable
{
    /// <summary>True while the virtual device is published and the worker is pumping.</summary>
    bool Enabled { get; }

    /// <summary>Why the last call failed, for the UI. Null after a successful call.</summary>
    string? LastError { get; }

    /// <summary>Publish the virtual device and start translating. False means unavailable (driver not
    /// installed, no permission) and <see cref="LastError"/> says why — the caller leaves the user's setting
    /// off, so the row snaps back on its next read instead of claiming a device that isn't there.</summary>
    bool Enable();

    /// <summary>Un-publish the virtual device and stop translating. Idempotent, and deliberately silent: a
    /// teardown that reports nothing cannot fail in a way the caller would have to handle. If a host held the
    /// surface, ownership is released as part of the teardown (see <see cref="OwnerChanged"/>) so the caller
    /// repaints its own lighting.</summary>
    void Disable();

    /// <summary>True while an external host holds the surface. Every lighting path of the app must yield while
    /// this is set, or each profile switch, resume and re-apply would stomp a frame the host owns.</summary>
    bool HostOwnsLighting { get; }

    /// <summary>Fires when <see cref="HostOwnsLighting"/> flips: true when a host takes the surface, false when
    /// it releases it (or the transport went away) and the app should paint its own again. Fires on the
    /// implementation's worker thread — marshal it before touching UI state.</summary>
    event Action<bool>? OwnerChanged;

    /// <summary>Repaint the hardware from the host's last frame, ignoring the implementation's write dedupe.
    /// Every caller of this is an event that clobbers the surface behind everyone's back — a profile switch
    /// forces the firmware's flash, sleep drops the state, a lid-open restores from black — so the repaint must
    /// not be suppressed by the colours happening to match. No-op unless a host owns the surface.</summary>
    void Reassert();
}

/// <summary>Hands the application this machine's lighting surface, or null where it cannot exist at all: no
/// transport for the OS (Linux has no consumer of HID LampArray in its desktop stack), or the Windows driver is
/// not installed. Application cannot construct the implementation itself without naming Infrastructure, so
/// composition supplies this instead; the two questions it answers are "is there one at all" (null) and "build
/// it over the zones this device advertises".</summary>
public interface IDynamicLightingFactory
{
    /// <summary>Build the surface over <paramref name="rgb"/>'s zones. <paramref name="include"/> filters out
    /// the zones the app must not drive — the lightbar while it follows the performance profile, which the
    /// firmware owns then.</summary>
    IDynamicLighting Create(IRgbDevice rgb, Func<RgbZone, bool> include);
}
