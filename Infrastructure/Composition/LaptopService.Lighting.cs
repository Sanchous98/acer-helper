using AcerHelper.Application;
using AcerHelper.Domain;

namespace AcerHelper.Infrastructure.Composition;

public sealed partial class LaptopService
{
    // ---- lighting (per-mode) ----

    /// <summary>The per-mode lighting for the CURRENT mode, as the door Application's two use cases read and
    /// write through (<see cref="ReadLightZone"/>, <see cref="ApplyLightZone"/>). Reads the current profile
    /// itself — prefer the overload below wherever the caller has just read it.
    ///
    /// IT HANDS OUT NO PART OF THE GRAPH, and that is the change this member carries: it used to return the
    /// stored <c>Dictionary&lt;string, LightSettings&gt;</c> itself, which the UI kept and edited in place. What
    /// comes back now is a KEY and a lock, and the state itself only ever crosses as
    /// <see cref="LightZoneState"/> — values, copied on the way out (see <see cref="LightZoneMode.Stored"/>).</summary>
    public ILightZoneMode LightsForCurrentMode() =>
        LightsForCurrentMode(device.PowerProfiles?.Current());

    /// <summary>As <see cref="LightsForCurrentMode()"/> but reusing an already-read current profile, so a caller
    /// that has just read it does not pay a second EC round-trip for the key. Same shape and same reason as
    /// <see cref="CurrentModeKey(PerformanceProfile?)"/>; the two must agree, or a caller that amortized the read
    /// would key its lighting off a different mode than its presets.
    ///
    /// THE MODE IS FIXED HERE, once, and every later read and write through the returned door lands in it. That
    /// is what today's code does too — the UI wrote into the dictionary this member handed it — and it is worth
    /// stating because the alternative (re-resolving the mode on every write) would both cost an EC read on the
    /// UI thread per edit and move an edit into a mode the user was not looking at.</summary>
    public ILightZoneMode LightsForCurrentMode(PerformanceProfile? cur)
    {
        lock (_state)
        {
            return new LightZoneMode(this, CurrentModeKey(cur));
        }
    }

    /// <summary>One performance mode's lighting, as <see cref="ILightZoneMode"/> asks for it. A private class
    /// rather than a handful of methods on the service: the mode key has to be carried, and the object that
    /// carries it is also what keeps the key off the caller's side of the boundary (Application never sees it).
    ///
    /// EVERY MEMBER TAKES THE GRAPH LOCK ITSELF, because the caller does not hold it: the UI edits on its own
    /// thread, and the background pass reads this same graph under <c>_state</c> while doing EC/WMI work. The
    /// structural part is why the lock is not optional — <see cref="Stored"/> enumerates the zone dictionary
    /// that a concurrent <see cref="Write"/> would restructure, and the serializer that runs under
    /// <see cref="Persist"/> enumerates it too. That is the hazard the old <c>EnsureLightZone</c> existed to
    /// close, and it is closed here for every member instead of for the one that happened to insert.</summary>
    private sealed class LightZoneMode(LaptopService service, string key) : ILightZoneMode
    {
        // THE MODE'S PRESET, CREATED WHEN THE DOOR IS TAKEN — which is what the read has always done, and the
        // one place in this path that still inserts on a read: a mode exists from the moment the app looks at
        // its lighting, so the per-mode shape is there before anything is written into it. The creation runs
        // inside the `_state` hold `LightsForCurrentMode` takes, and this is the same GetOrAdd every other
        // per-mode preset is created with.
        private readonly LightPreset bucket = GetOrAdd(service.Settings.LightPresets, key);

        public IReadOnlyDictionary<string, LightZoneState> Stored()
        {
            lock (service._state)
            {
                var zones = bucket.Zones;
                var copy = new Dictionary<string, LightZoneState>(zones.Count);
                foreach (var (name, stored) in zones) copy[name] = ToDomain(stored);
                return copy;
            }
        }

        /// <summary>Whether the DEVICE advertises the zone. Read outside the graph lock on purpose: the device's
        /// zone list is built once, at composition, and never restructured — this is not a read of the graph, and
        /// taking <c>_state</c> for it would put a lock the background pass holds across EC/WMI work in front of
        /// a question that needs no lock at all.</summary>
        public bool Advertises(string zone) => service.device.Lighting?.Zones.Any(z => z.Name == zone) == true;

        public void Write(string zone, LightZoneState state)
        {
            lock (service._state)
            {
                var zones = bucket.Zones;
                if (!zones.TryGetValue(zone, out var stored)) zones[zone] = stored = new LightSettings();
                CopyFrom(state, stored);
            }
        }

        public void Persist() => service.Save();
    }

    /// <summary>The stored entry as the domain's value, with the zone-colour array DUPLICATED. The copy is not
    /// tidiness: <see cref="ILightZoneMode.Stored"/> is what the UI keeps and re-applies, and a state carrying
    /// the stored array would leave the widest part of it shared with the graph — the same depth rule, and the
    /// same reason, as the closed accessors' <c>Snapshot()</c>.</summary>
    private static LightZoneState ToDomain(LightSettings stored) => new(
        stored.Configured, stored.EffectIndex, stored.Brightness, stored.Speed, stored.Direction, stored.Color,
        [.. stored.ZoneColors]);

    /// <summary>The value as the stored entry, field for field and with a fresh array. The whole zone is written
    /// at once — the UI builds every field it knows and hands the lot over — so nothing here is a per-field
    /// merge, and a field the caller left at its default is deliberately written as that default.</summary>
    private static void CopyFrom(LightZoneState state, LightSettings stored)
    {
        stored.Configured  = state.Configured;
        stored.EffectIndex = state.EffectIndex;
        stored.Brightness  = state.Brightness;
        stored.Speed       = state.Speed;
        stored.Direction   = state.Direction;
        stored.Color       = state.Color;
        stored.ZoneColors  = [.. state.ZoneColors];
    }
}
