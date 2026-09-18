using AcerHelper.Application;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The per-mode lighting as use cases, pinned through their contract with a STUB — no <c>LaptopService</c>, no
/// settings graph, no RGB device. The sibling of <c>AppliedEditUseCasesTests</c> for the one axis that family
/// left behind, and it is here for the same reason: the two rules below used to be the shape of a dictionary the
/// UI held and edited, so the only way to observe them was to build a machine, arrange a store and read the
/// graph back. Both rules are now stated in Application (Application/LightZone.cs) and a stub is enough to see
/// each of them.
///
/// WHAT A STUB CANNOT SEE, stated rather than left implied: that the write and the persist happen under the
/// graph lock, and that the values the door hands out share no array with the graph. Neither is observable from
/// this side — the first is the implementation's (<c>LightZoneMode</c>'s own docstring measures why the lock is
/// not optional) and the second is pinned where the graph is reachable
/// (<c>LaptopServicePresetGetOrAddTests.TheStateTheDoorHandsOut_SharesNoArrayWithTheGraph</c>).
///
/// THE LAST TEST IN THIS FILE IS NOT A USE CASE and is kept here because it guards the same boundary: the
/// Domain's defaults for a zone and the stored form's own initializers are two spellings of one thing, and the
/// read's "created from the defaults" rule depends on their agreeing.
/// </summary>
public class LightZoneUseCasesTests
{
    private const string Zone = "keyboard";

    /// <summary>A door that advertises whatever it was built with, stores values in a map, and RECORDS every
    /// write and persist in order — the order is load-bearing on the write path, so a stub that only held the
    /// resulting value could not see it.</summary>
    private sealed class StubZoneMode(IEnumerable<string> advertised) : ILightZoneMode
    {
        private readonly Dictionary<string, LightZoneState> _zones = [];
        private readonly HashSet<string> _advertised = [.. advertised];

        /// <summary>Every effect this door was asked to make, in order — writes and persists in one list, which
        /// is what makes "stored before persisted" an assertion rather than a claim about two counters.</summary>
        public List<string> Effects { get; } = [];

        public List<(string Zone, LightZoneState State)> Writes { get; } = [];

        public IReadOnlyDictionary<string, LightZoneState> Stored() => new Dictionary<string, LightZoneState>(_zones);
        public bool Advertises(string zone) => _advertised.Contains(zone);

        public void Write(string zone, LightZoneState state)
        {
            Effects.Add($"write {zone}");
            Writes.Add((zone, state));
            _zones[zone] = state;
        }

        public void Persist() => Effects.Add("persist");

        /// <summary>Arrange a zone the mode already holds.</summary>
        public StubZoneMode With(string zone, LightZoneState state) { _zones[zone] = state; return this; }
    }

    /// <summary>A value distinguishable field by field from <see cref="LightZoneState.Default"/>, so an
    /// assertion that the edit was handed over whole cannot pass by the two happening to be equal.</summary>
    private static readonly LightZoneState Edited = new(
        Configured: true, EffectIndex: 3, Brightness: 42, Speed: 9, Direction: 2, Color: 0x00FF00,
        ZoneColors: [0x111111, 0x222222, 0x333333, 0x444444]);

    /// <summary>A write to a zone this machine does not advertise is REFUSED — nothing is stored and nothing is
    /// persisted, and the caller is told so. The rule is what keeps the graph's zone keys inside the device's
    /// list: an entry for a zone the machine does not have is written to settings.json forever and is read back
    /// on every launch, a stored state nobody configured.
    ///
    /// MUTATION THAT REDDENS IT: dropping the <c>Advertises</c> check from <c>ApplyLightZone.Run</c> — the stub
    /// then records both the write and the persist.</summary>
    [Fact]
    public void AWriteToAZoneTheMachineDoesNotAdvertise_IsRefused_AndReachesNeitherTheGraphNorTheFile()
    {
        var mode = new StubZoneMode([Zone]);

        var taken = ApplyLightZone.Run("unknown-zone", Edited, mode);

        Assert.False(taken);
        Assert.Empty(mode.Effects);
        Assert.False(mode.Stored().ContainsKey("unknown-zone"));
    }

    /// <summary>A write to a zone the machine HAS is taken, stored whole, and then persisted — the order is the
    /// guarantee, not a coincidence of which statement was typed first: a value the user chose must be in the
    /// file, and folding the two acts would decide silently that a record is never observed before it is
    /// persisted.
    ///
    /// MUTATION THAT REDDENS IT: swapping the two calls in <c>ApplyLightZone.Run</c> (the recorded order
    /// reddens), or dropping the <c>Persist()</c> (the user's choice never reaches settings.json).</summary>
    [Fact]
    public void AWriteToAnAdvertisedZone_IsStoredWhole_AndThenPersisted()
    {
        var mode = new StubZoneMode([Zone]);

        Assert.True(ApplyLightZone.Run(Zone, Edited, mode));

        Assert.Equal([$"write {Zone}", "persist"], mode.Effects);
        // Field for field, array included: the whole zone crosses at once and no part of it is interpreted.
        Assert.Equal(Edited, mode.Stored()[Zone]);
        Assert.Equal([0x111111, 0x222222, 0x333333, 0x444444], mode.Stored()[Zone].ZoneColors);
    }

    /// <summary>A mode that has never held a zone is not an absence to report: the read answers with the zone's
    /// DEFAULTS and puts the entry in the mode, once. It has to be, because of what the UI does with the answer —
    /// the first act on a zone is to show it, and the first edit afterwards must update the entry the user was
    /// looking at rather than race a second creation.
    ///
    /// The defaults are the Domain's (<see cref="LightZoneState.Default"/>) and NOT the zero value of the struct,
    /// which would be a zone at brightness 0 that the app would then drive; the two are different values and the
    /// assertion reads a field that distinguishes them.
    ///
    /// MUTATION THAT REDDENS IT: answering with <c>default(LightZoneState)</c> (brightness 0), or with the
    /// defaults WITHOUT writing them (the mode would hold no entry, and the next read would create a second
    /// one).</summary>
    [Fact]
    public void AReadOfAModeThatHasNoZone_CreatesItFromTheDefaults_AndSavesNothing()
    {
        var mode = new StubZoneMode([Zone]);

        var read = ReadLightZone.Run(Zone, mode);

        Assert.Equal(LightZoneState.Default, read);
        Assert.Equal(100, read.Brightness);                  // ...and not the struct's zero
        Assert.False(read.Configured);                       // looked at is not configured
        Assert.Equal([$"write {Zone}"], mode.Effects);       // created, and NOT persisted — nobody configured it
        Assert.Equal(LightZoneState.Default, mode.Stored()[Zone]);
    }

    /// <summary>A second read gives back what is stored, and writes nothing: an entry the user has configured
    /// surviving a later read is the whole reason the creation is conditional.
    ///
    /// MUTATION THAT REDDENS IT: writing the defaults unconditionally in <c>ReadLightZone.Run</c>.</summary>
    [Fact]
    public void AReadOfAZoneTheModeAlreadyHolds_ReturnsIt_AndWritesNothing()
    {
        var mode = new StubZoneMode([Zone]).With(Zone, Edited);

        var read = ReadLightZone.Run(Zone, mode);

        Assert.Equal(Edited, read);
        Assert.Empty(mode.Effects);
    }

    /// <summary>A read for a zone the machine does not advertise creates NOTHING — the same rule the write
    /// refuses with, on the other half of the path. The caller takes its zone names from the device, so the
    /// branch is unreachable from the tree's own callers; it is stated rather than left to whichever way
    /// <c>TryGetValue</c> happened to fall.
    ///
    /// MUTATION THAT REDDENS IT: dropping the <c>Advertises</c> check from <c>ReadLightZone.Run</c>.</summary>
    [Fact]
    public void AReadOfAZoneTheMachineDoesNotAdvertise_CreatesNothing()
    {
        var mode = new StubZoneMode([Zone]);

        var read = ReadLightZone.Run("unknown-zone", mode);

        Assert.Equal(LightZoneState.Default, read);
        Assert.Empty(mode.Effects);
    }

    /// <summary>
    /// THE TWO SPELLINGS OF "WHAT A ZONE LOOKS LIKE BEFORE ANYONE HAS SET IT" MUST AGREE. The read's creation
    /// fills a stored entry with the Domain's <see cref="LightZoneState.Default"/>, while a stored entry that a
    /// hand-edited or older settings.json leaves a field short falls back to the store's own initializers
    /// (<c>LightSettings</c>). Those are two different code paths to one value, and the owner accepted the
    /// duplication knowingly — so it is a GUARD rather than a comment, and this is the guard.
    ///
    /// MUTATION THAT REDDENS IT: changing either side alone — the Domain's default brightness, or
    /// <c>LightSettings</c>'s own initializer.
    /// </summary>
    [Fact]
    public void TheDomainsDefaults_AreTheStoredFormsOwn()
    {
        var stored = new LightSettings();
        var zone = LightZoneState.Default;

        Assert.Equal(stored.Configured, zone.Configured);
        Assert.Equal(stored.EffectIndex, zone.EffectIndex);
        Assert.Equal(stored.Brightness, zone.Brightness);
        Assert.Equal(stored.Speed, zone.Speed);
        Assert.Equal(stored.Direction, zone.Direction);
        Assert.Equal(stored.Color, zone.Color);
        Assert.Equal(stored.ZoneColors, zone.ZoneColors);
    }
}
