using AcerHelper.Application;
using AcerHelper.Domain;

namespace AcerHelper.Tests.Fakes;

/// <summary>A hand-written <see cref="ILightZoneMode"/>: one performance mode's lighting as VALUES, plus the
/// advertised zone list and a record of every write.
///
/// IT STANDS WHERE THE LIVE DICTIONARY USED TO. Until the owner's ruling the lighting tests arranged a
/// <c>Dictionary&lt;string, LightSettings&gt;</c> and read their assertions off it, because that dictionary WAS
/// what the service handed the UI and what the UI edited. The state now crosses as
/// <see cref="LightZoneState"/>, so a test arranges values and reads back what was written — which is also why
/// an assertion here is about a WRITE rather than about a shared object.
///
/// <see cref="Writes"/> records the CALLS, in order, not only their effect: "the panel applied but stored
/// nothing" and "the panel stored the same value it already had" are the same state and different behaviour,
/// and several tests below turn on exactly that difference.</summary>
public sealed class FakeLightZones(IEnumerable<string> advertised, IDictionary<string, LightZoneState>? zones = null)
    : ILightZoneMode
{
    private readonly Dictionary<string, LightZoneState> _zones = zones is null ? [] : new(zones);
    private readonly HashSet<string> _advertised = [.. advertised];

    /// <summary>Every write the door was asked to make, in order.</summary>
    public List<(string Zone, LightZoneState State)> Writes { get; } = [];

    /// <summary>How many times the graph was asked to be written out.</summary>
    public int Persists { get; private set; }

    /// <summary>How many times the mode was READ. Counted because "this path takes no state from the graph" is a
    /// claim about a NON-read: a re-apply that re-read the mode every tick would look identical in the values it
    /// writes, and differs only here.</summary>
    public int StoredCalls { get; private set; }

    /// <summary>What the mode holds now — the stand-in for reading the settings graph back.</summary>
    public LightZoneState this[string zone] => _zones[zone];

    /// <summary>Whether the mode holds an entry for the zone at all.</summary>
    public bool Holds(string zone) => _zones.ContainsKey(zone);

    public IReadOnlyDictionary<string, LightZoneState> Stored()
    {
        StoredCalls++;
        return new Dictionary<string, LightZoneState>(_zones);
    }

    public bool Advertises(string zone) => _advertised.Contains(zone);

    public void Write(string zone, LightZoneState state)
    {
        Writes.Add((zone, state));
        _zones[zone] = state;
    }

    public void Persist() => Persists++;

    /// <summary>One advertised zone whose stored state is the given value — the arrangement every lighting test
    /// needs, and the one the service hands over in production.</summary>
    public static FakeLightZones Of(string zone, LightZoneState state) => new([zone], new Dictionary<string, LightZoneState> { [zone] = state });

    /// <summary>A door over no zones at all, for the device shapes that have no RGB panels (a plain backlight,
    /// or a lightbar the firmware follows).</summary>
    public static FakeLightZones Empty() => new([]);
}
