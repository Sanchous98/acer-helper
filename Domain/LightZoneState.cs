namespace AcerHelper.Domain;

/// <summary>One RGB zone's lighting for ONE performance mode — the DOMAIN's vocabulary for it, and deliberately
/// not the stored <c>LightSettings</c>' field layout, which stays Infrastructure's: the persisted FORM is the
/// layer that writes the file (the owner's ruling), so this type is what CROSSES the boundary and
/// <c>LightSettings</c> is what lands on disk.
///
/// WHY IT EXISTS AT ALL, since a previous investigation priced exactly this and stopped. That investigation
/// measured the cost of a contract that HIDES THE GRAPH — a domain spelling for every stored form (seven
/// <c>LightSettings</c> fields, five preset dictionaries, the source slots, the flags) — and found it would put
/// Application back on the very shape the container moved out to escape. It was right about that contract. This
/// type is the other one: ONE zone's state, for the one axis the UI actually edits, which is the same distinction
/// <see cref="FanAxisState"/> already draws for the fans («два метода, ось аргументом» — not «тип на каждую форму
/// хранения»). The owner has ruled that THIS price is worth paying, because the alternative is what stood before
/// it: a live reference into the settings graph, held by the UI and written in place.
///
/// THE FIELDS ARE THE STORED ONES, VALUE FOR VALUE, and that is the whole of the mapping — no interpretation,
/// no re-encoding, no default filled in here. A colour is the packed 0xRRGGBB integer the store writes, a
/// direction is the 1/2 byte the transport takes, an effect is the index into the zone's advertised effect
/// list, and <see cref="Configured"/> is the same flag the store persists (false until the user changes
/// something, so a fresh install does not clobber the firmware's default on first launch). What is NOT here is
/// the key: a zone is identified by the name the device advertises, and the mode is the mode the door was
/// taken for (Application/LightZone.cs) — neither belongs inside the value.
///
/// <see cref="ZoneColors"/> IS AN ARRAY AND THEREFORE NOT A VALUE, exactly as <see cref="FanSettings.Curve"/>
/// is not. The rule is the same and is stated rather than implied: a state that CROSSES to a caller that keeps
/// it (the read, and the whole-mode snapshot the re-apply pass caches) carries a duplicated array, while the
/// state handed back inside one edit may alias. Only the first is reachable from here — the door duplicates on
/// every read — so no caller of this type ever holds the stored array.</summary>
public readonly record struct LightZoneState(
    bool Configured, int EffectIndex, int Brightness, int Speed, int Direction, int Color, int[] ZoneColors)
{
    /// <summary>What a zone looks like in a mode that has never held it — the values the store's own
    /// <c>LightSettings</c> initializers give a freshly created entry, stated here because the decision that
    /// NEEDS them is Application's: a mode with no entry yet is a zone in its defaults, and the read is what
    /// creates that entry (Application/LightZone.cs, <c>ReadLightZone</c>).
    ///
    /// THE TWO SPELLINGS ARE PINNED AGAINST EACH OTHER rather than trusted to stay in step: a freshly created
    /// stored entry and this value must agree field for field, or a zone the app has merely LOOKED at would
    /// differ from one it has just configured — and the test that holds them together is
    /// <c>LightZoneUseCasesTests.TheDomainsDefaults_AreTheStoredFormsOwn</c>, which reddens on any change to
    /// either side.</summary>
    public static LightZoneState Default => new(
        Configured: false, EffectIndex: 0, Brightness: 100, Speed: 5, Direction: 1, Color: 0xFF0000, ZoneColors: []);
}
