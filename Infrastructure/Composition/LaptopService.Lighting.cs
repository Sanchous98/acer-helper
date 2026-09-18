using System.Threading;
using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Localization;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    // ---- LampArray / Windows Dynamic Lighting ----

    private IDynamicLighting? _lampArray;
    private bool _lampArrayBuilt;
    // Its OWN lock, deliberately not _state: this property is read from the UI thread on every lighting repaint
    // (LightingCoordinator.Paint), and _state is held across blocking EC/WMI work by the background pass — so
    // sharing it would put an ACPI-EC stall in front of a UI-thread paint. Nothing here touches Settings.
    private readonly Lock _lampGate = new();

    /// <summary>The virtual LampArray surface (Infrastructure/Lighting/LampArrayBridge.cs, reached through
    /// <see cref="IDynamicLighting"/>), or null when this build/OS/device can't offer it: no transport for the
    /// OS, no driver installed, or no RGB zones at all. Built lazily — creating it is free, but it must not
    /// happen before the settings are loaded, and most runs never need it. The build goes through the factory
    /// composition handed in rather than a `new`, so this class names no Infrastructure type.</summary>
    public IDynamicLighting? LampArray
    {
        get
        {
            lock (_lampGate)
            {
                if (_lampArrayBuilt) return _lampArray;
                _lampArrayBuilt = true;
                if (dynamicLightingFactory is { } factory && device.Lighting is { } rgb)
                    _lampArray = factory.Create(rgb, ZoneAvailableToHost);
                return _lampArray;
            }
        }
    }

    // Which zones a host may paint. A "follows performance profile" lightbar is the firmware's while that flag
    // is on (the app doesn't drive it either — see LightingViewModel), so it is not offered as lamps; flipping
    // the flag off and re-enabling the bridge picks it up.
    private bool ZoneAvailableToHost(RgbZone zone)
        => !(zone.CanFollowProfile && device.Lighting?.ProfileFollowKey is { } key && GetDeviceFlag(key, true));

    /// <summary>Turn the virtual LampArray on/off and persist the choice. Returns false if it could not be
    /// published (see <see cref="IDynamicLighting.LastError"/>) — the setting is then left off, so the UI row
    /// snaps back on its next read instead of claiming a device that isn't there. Blocking (opens the driver);
    /// call off the UI thread — the Options rows already do.
    ///
    /// WHAT IS STATED HERE AND WHAT MOVED: what gets REMEMBERED (the state the surface is in, not the wish) and
    /// what a machine with no surface does (refuse without remembering) are <see cref="ApplyDynamicLighting"/>
    /// (Application); the bridge call and the graph write are the two members below.</summary>
    public (bool ok, string? error) SetDynamicLighting(bool on)
        => ApplyDynamicLighting.Run(on, this);

    /// <summary>Publish or take down the surface, or null when this machine has none. The lazy
    /// <see cref="LampArray"/> build is what decides the null — it goes through the factory composition handed in
    /// rather than a <c>new</c>, so this class names no Infrastructure type.</summary>
    (bool ok, string? error)? ILightingSwitchTarget.Switch(bool on)
    {
        var la = LampArray;
        if (la == null) return null;

        // Disable() reports nothing and cannot fail; only Enable has an outcome to report.
        return on ? Attempt(la.Enable, () => la.LastError) : (true, (string?)null);
    }

    /// <summary>Remember the choice, under the graph lock.</summary>
    void ILightingSwitchTarget.Store(bool on)
    {
        lock (_state) { Settings.DynamicLighting = on; Save(); }
    }

    // ---- lighting (per-mode) ----

    /// <summary>The per-zone lighting state for the current mode (created empty on first use). Reads the
    /// current profile itself — prefer the overload below wherever the caller has just read it.</summary>
    public Dictionary<string, LightSettings> LightsForCurrentMode() =>
        LightsForCurrentMode(device.PowerProfiles?.Current());

    /// <summary>As <see cref="LightsForCurrentMode()"/> but reusing an already-read current profile, so a caller
    /// that has just read it does not pay a second EC round-trip for the key. Same shape and same reason as
    /// <see cref="CurrentModeKey(PerformanceProfile?)"/>; the two must agree, or a caller that amortized the read
    /// would key its lighting off a different mode than its presets.</summary>
    public Dictionary<string, LightSettings> LightsForCurrentMode(PerformanceProfile? cur)
    {
        lock (_state)
        {
            var key = CurrentModeKey(cur);
            return GetOrAdd(Settings.LightPresets, key).Zones;
        }
    }

    /// <summary>Create-if-missing a per-zone lighting entry, UNDER _state. The lighting view-models mutate the
    /// live Zones dict (returned above) on the UI thread; Save() now runs on the background pass and enumerates
    /// that same dict, so this structural insert must share _state with Save — otherwise a "collection modified"
    /// throws mid-serialization and the settings write is silently dropped. (Per-field edits to a LightSettings
    /// don't restructure the dict, so those stay unguarded.)</summary>
    public LightSettings EnsureLightZone(Dictionary<string, LightSettings> zones, string name)
    {
        lock (_state)
        {
            return GetOrAdd(zones, name);
        }
    }
}
