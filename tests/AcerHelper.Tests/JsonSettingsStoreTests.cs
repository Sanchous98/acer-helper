using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcerHelper.Domain;
using AcerHelper.Infrastructure.Composition;
using AcerHelper.Localization;
using AcerHelper.Tests.Fakes;

namespace AcerHelper.Tests;

/// <summary>
/// The settings store, which had no coverage at all until its path became a constructor argument.
///
/// It is worth covering because it is the substrate of every feature: a per-mode fan curve, a GPU offset, a
/// Curve-Optimizer preset and a light zone are all just fields in <see cref="Settings"/>, so a field that
/// fails to survive Save → Load is a FEATURE that silently forgets. That failure is invisible to the rest of
/// this suite, which injects <see cref="Fakes.FakeSettingsStore"/> and therefore never serializes anything.
///
/// The seam is not a convenience. <see cref="JsonSettingsStore.Load"/> MOVES a corrupt file aside and
/// <see cref="JsonSettingsStore.Save"/> renames over the live one, so a test of either path pointed at the
/// real %AppData% location would consume the user's settings. Every test here runs in its own scratch
/// directory — see <see cref="TempDir"/>.
/// </summary>
public class JsonSettingsStoreTests
{
    /// <summary>Every field populated, because the round trip has to have something to lose in each one. The
    /// values are deliberately NOT the defaults: a default is exactly what a failed deserialization leaves
    /// behind, so a test built from defaults would pass against a store that dropped everything.
    ///
    /// The KEYS are the real ones the app writes — <c>ccd:0</c>/<c>gfx</c> from the Curve Optimizer's
    /// <c>VoltageDomain.Key</c>, <c>Keyboard</c>/<c>Lightbar</c> for the two light zones, and
    /// <c>acer.lightbarFollowsProfile</c> for the device flag — taken from a real settings.json rather than
    /// invented. They are opaque strings either way, so this is fidelity rather than coverage: the test reads
    /// as the file the user actually has.</summary>
    private static Settings Populated() => new()
    {
        Language = AppLanguage.Russian,
        TurboToggles = true,
        Clamshell = true,
        Bluelight = 4,
        DynamicLighting = true,
        CardwireGpuAccess = true,
        OnAc = new ProfileMemory { BaseId = "balanced", Turbo = true },
        OnBattery = new ProfileMemory { BaseId = "power-saver" },
        FanPresets =
        {
            ["balanced"] = new FanPreset
            {
                Mode = 3, Cpu = 55, Gpu = 60,
                CpuUseCurve = true, GpuUseCurve = false,
                CpuCurve = [30, 40, 50, 60, 70],
                GpuCurve = [35, 45, 55, 65, 75],
            },
        },
        LightPresets =
        {
            ["balanced"] = new LightPreset
            {
                Zones =
                {
                    ["Keyboard"] = new LightSettings
                    {
                        Configured = true, EffectIndex = 2, Brightness = 80,
                        Speed = 7, Direction = 2, Color = 0x00FF00,
                        ZoneColors = [0x111111, 0x222222],
                    },
                },
            },
        },
        GpuOcPresets = { ["balanced"] = new GpuOcPreset { Core = 300, Mem = 1500 } },
        CoPresets =
        {
            ["balanced"] = new CoPreset
            {
                AllCore = -30,
                Domains = { ["ccd:0"] = -20, ["ccd:1"] = -20, ["gfx"] = -16 },
            },
        },
        CpuPowerModes = { ["balanced"] = "best-performance" },
        DeviceSettings = { ["acer.lightbarFollowsProfile"] = "1" },
    };

    /// <summary>Fetch a key that must be present, failing with the key's name rather than a
    /// <c>KeyNotFoundException</c>. The three assertions that use it are the ones that catch a per-mode preset
    /// that did not survive serialization, so the failure has to say which mode went missing.</summary>
    private static TValue Present<TValue>(IReadOnlyDictionary<string, TValue> map, string key)
    {
        Assert.True(map.TryGetValue(key, out var value), $"'{key}' did not survive the round trip");
        return value!;
    }

    [Fact]
    public void EverySettingSurvivesTheRoundTrip()
    {
        using var dir = new TempDir();
        new JsonSettingsStore(dir.SettingsPath).Save(Populated());

        // A SECOND store, so nothing survives merely by being the same object in memory.
        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);

        AssertNothingWasLost(loaded);

        // The consent to use the discrete GPU is asserted HERE rather than inside AssertNothingWasLost, because
        // that helper is shared with the test that loads a REAL settings file written by a shipped version — and
        // such a file cannot carry a key that did not exist when it was written. A member that must survive is
        // asserted where the file is known to be complete.
        Assert.True(loaded.CardwireGpuAccess);

        // And the WHOLE instance, not the hand-written list above: Load builds the model through Settings'
        // constructor, which takes the persisted half over member by member, so a member added to Settings and
        // forgotten there comes back as its default — which this comparison sees and the list above only would
        // if someone remembered to extend it. (Mutation: delete one line of that constructor's copy — for
        // instance the DynamicLighting one — and this assertion reddens.)
        Assert.Equal(JsonSerializer.Serialize(Populated(), SettingsJsonContext.Default.Settings),
                     JsonSerializer.Serialize(loaded, SettingsJsonContext.Default.Settings));
    }

    /// <summary>The store's half of the hand-off: <see cref="JsonSettingsStore.Load"/> builds the session's
    /// model WITH the set it is handed, the same way the test fake does — the real path, not only the fake's.
    /// Both halves are asserted: the model holds the declarations, and the file's values still came through, so
    /// the two arguments of the constructor were not confused for one another.
    ///
    /// The set is NOT in the file and must not be: settings.json says what the user CHOSE, and the declarations
    /// are what the machine HAS (<c>EveryDeclaredPropertyStillReachesTheDisk</c> is the guard on that side).
    ///
    /// Mutation that reddens it: build the model without the set (Load returning <c>new Settings()</c>) — this
    /// test alone.</summary>
    [Fact]
    public void LoadBuildsTheModelWithTheDeclaredSetItIsHanded()
    {
        using var dir = new TempDir();
        new JsonSettingsStore(dir.SettingsPath).Save(Populated());
        var declared = new FlagSetting { Key = "lcd_override", Port = new FakeFlagPort() };

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([declared]);

        Assert.Equal([declared], loaded.DeclaredSettings);
        AssertNothingWasLost(loaded);
    }

    /// <summary>Every value the two tests around this one can lose, asserted in ONE place so that a file this
    /// build just wrote and a file the shipped build wrote are held to the same standard: however the settings
    /// arrived, none of these may come back as a default. Split out because the two failures differ — a writer
    /// that stops emitting a field, versus a reader that no longer recognises a name — while the claim is
    /// identical, and duplicating it would let the two guards drift apart.</summary>
    private static void AssertNothingWasLost(Settings loaded)
    {
        Assert.Equal(AppLanguage.Russian, loaded.Language);
        Assert.True(loaded.TurboToggles);
        Assert.True(loaded.Clamshell);
        Assert.Equal(4, loaded.Bluelight);
        Assert.True(loaded.DynamicLighting);

        Assert.Equal("balanced", loaded.OnAc.BaseId);
        Assert.True(loaded.OnAc.Turbo);
        Assert.Equal("power-saver", loaded.OnBattery.BaseId);
        Assert.False(loaded.OnBattery.Turbo);

        var fan = Present(loaded.FanPresets, "balanced");
        Assert.Equal(3, fan.Mode);
        Assert.Equal(55, fan.Cpu);
        Assert.Equal(60, fan.Gpu);
        Assert.True(fan.CpuUseCurve);
        Assert.False(fan.GpuUseCurve);
        Assert.Equal([30, 40, 50, 60, 70], fan.CpuCurve);
        Assert.Equal([35, 45, 55, 65, 75], fan.GpuCurve);

        // Two levels of nesting (mode -> zone -> per-zone colours): the deepest shape in Settings, and the
        // one a change to the JSON context would break first.
        var zone = Present(Present(loaded.LightPresets, "balanced").Zones, "Keyboard");
        Assert.True(zone.Configured);
        Assert.Equal(2, zone.EffectIndex);
        Assert.Equal(80, zone.Brightness);
        Assert.Equal(7, zone.Speed);
        Assert.Equal(2, zone.Direction);
        Assert.Equal(0x00FF00, zone.Color);
        Assert.Equal([0x111111, 0x222222], zone.ZoneColors);

        var gpu = Present(loaded.GpuOcPresets, "balanced");
        Assert.Equal(300, gpu.Core);
        Assert.Equal(1500, gpu.Mem);

        var co = Present(loaded.CoPresets, "balanced");
        Assert.Equal(-30, co.AllCore);                                          // sign survives
        Assert.Equal(-20, Present(co.Domains, "ccd:0"));
        Assert.Equal(-20, Present(co.Domains, "ccd:1"));
        Assert.Equal(-16, Present(co.Domains, "gfx"));

        Assert.Equal("best-performance", Present(loaded.CpuPowerModes, "balanced"));
        Assert.Equal("1", Present(loaded.DeviceSettings, "acer.lightbarFollowsProfile"));
    }

    /// <summary>THE RENAME GUARD — the one this file was missing, and the reason it is a FILE rather than a list
    /// of names written in this one. settings.json outlives the binary, so the NAME of a persisted property is a
    /// compatibility surface: renaming one silently drops that value from every file already on disk, and the next
    /// Save writes the loss back as though the user had made it.
    ///
    /// WHY A FIXTURE, NOT A LITERAL ARRAY. The obvious guard was written first — a literal array of the shipped
    /// names, right here — and a mutation killed it: renaming <see cref="Settings.DynamicLighting"/> across the
    /// repo (a find-and-replace over <c>*.cs</c>, which is what a rename IS) rewrote the guard's own literal along
    /// with the property, so the guard renamed itself and stayed green. The names therefore have to live where a
    /// rename of the C# type cannot reach them, and a JSON file is that place.
    ///
    /// The claim is the one a user cares about: a settings file written by the SHIPPED build loads with every value
    /// intact. The fixture's values are deliberately NOT defaults — a default is what a failed deserialization
    /// leaves behind, so a file built from defaults would pass against a store that dropped everything.
    ///
    /// A rename migrated the right way — <c>[JsonPropertyName("oldName")]</c>, leaving the old name on disk —
    /// PASSES. The test this replaced compared the JSON against the type's own property names and went RED on
    /// precisely that fix, which is how the wrong repair (deleting the guard) comes to look like the right one.
    ///
    /// Verified red by renaming <see cref="Settings.DynamicLighting"/>; verified GREEN by renaming it AND adding
    /// <c>[JsonPropertyName("DynamicLighting")]</c> — the second mutation is the one that matters.</summary>
    [Fact]
    public void AFullSettingsFileFromAShippedVersionStillLoads()
    {
        using var dir = new TempDir();
        File.Copy(Fixture("settings-0.33.0.json"), dir.SettingsPath);

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);

        Assert.False(File.Exists(dir.SettingsPath + ".bad"));   // a readable file, not a rescued one
        AssertNothingWasLost(loaded);
    }

    /// <summary>THE OMISSION GUARD, kept because the rename guard alone does not cover it: a member can sit on the
    /// type and still not reach the disk — <c>[JsonIgnore]</c>, or a setter the serializer can no longer use — and
    /// no literal list knows about a property that was just added. This reads each property's CONTRACTED json name
    /// and requires it on disk.
    ///
    /// Reading the attribute is what keeps this from being the tautology it replaced: because
    /// <c>[JsonPropertyName]</c> is honoured here, a rename migrated with an alias passes BOTH guards, so the two
    /// agree instead of contradicting each other. Scope: the top-level type only — the nested types are pinned by
    /// name in the test above rather than by reflection here.</summary>
    [Fact]
    public void EveryDeclaredPropertyStillReachesTheDisk()
    {
        using var doc = JsonDocument.Parse(FullyPopulatedJson());
        var onDisk = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var missing = typeof(Settings).GetProperties()
            .Where(p => !onDisk.Contains(JsonNameOf(p)))
            .Select(p => $"{p.Name} (as \"{JsonNameOf(p)}\")")
            .ToArray();

        Assert.True(missing.Length == 0,
            "these members are on Settings but no longer reach settings.json: " + string.Join(", ", missing));
    }

    /// <summary>The document both guards read: a FULLY populated Settings, serialized through the same
    /// source-generated context <see cref="JsonSettingsStore.Save"/> uses.</summary>
    private static string FullyPopulatedJson()
        => JsonSerializer.Serialize(Populated(), SettingsJsonContext.Default.Settings);

    /// <summary>A committed file of the test project, located from the COMPILER's path rather than the current
    /// directory: the test host runs with its working directory set to the output folder, and the fixture is not
    /// copied there. Reading it from the source tree is also what makes it a guard — a persisted name can be
    /// renamed across <c>*.cs</c> without touching this file (see the test above).</summary>
    private static string Fixture(string name, [CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "Fixtures", name);

    /// <summary>The json name the serializer is contracted to write for a member: its <c>[JsonPropertyName]</c>
    /// when it carries one, otherwise its CLR name.</summary>
    private static string JsonNameOf(PropertyInfo p)
        => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;

    [Fact]
    public void AMissingFileYieldsDefaults()
    {
        using var dir = new TempDir();

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);

        Assert.Equal(AppLanguage.System, loaded.Language);
        Assert.False(loaded.TurboToggles);
        Assert.False(loaded.Clamshell);
        Assert.Empty(loaded.FanPresets);
        Assert.Empty(loaded.LightPresets);
        Assert.Empty(loaded.GpuOcPresets);
        Assert.Empty(loaded.CoPresets);
        Assert.Empty(loaded.CpuPowerModes);
        Assert.Empty(loaded.DeviceSettings);
        Assert.False(File.Exists(dir.SettingsPath));   // a read creates nothing
    }

    /// <summary>The rescue the source comment promises, asserted instead of described: the defaults come back,
    /// the corrupt bytes are still readable at <c>.bad</c>, and the original path is left free so the next
    /// <c>Save</c> cannot silently overwrite what was just rescued.</summary>
    [Fact]
    public void CorruptJsonYieldsDefaultsAndPreservesTheBadCopy()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.SettingsPath, "{ this is not json");

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);

        Assert.Equal(AppLanguage.System, loaded.Language);
        Assert.Empty(loaded.FanPresets);
        Assert.Equal("{ this is not json", File.ReadAllText(dir.SettingsPath + ".bad"));
        Assert.False(File.Exists(dir.SettingsPath));
    }

    /// <summary>Pins <c>overwrite: true</c> on the rescue. Without it the second <c>File.Move</c> throws into
    /// the same empty <c>catch</c> that protects the first, so the store would quietly keep the FIRST
    /// corruption as the recoverable copy while the second is the one the user actually wants back.</summary>
    [Fact]
    public void ASecondCorruptionReplacesTheEarlierRescue()
    {
        using var dir = new TempDir();
        var store = new JsonSettingsStore(dir.SettingsPath);

        File.WriteAllText(dir.SettingsPath, "first corruption");
        store.Load([]);
        File.WriteAllText(dir.SettingsPath, "second corruption");
        store.Load([]);

        Assert.Equal("second corruption", File.ReadAllText(dir.SettingsPath + ".bad"));
    }

    // ---------------------------------------------------------------- the curve sanitiser at load

    /// <summary>A fan curve in the file that is not a curve is REPLACED WITH THE BUILT-IN RAMP, AND THE FILE IS
    /// REWRITTEN — one curve lost, not the settings file. This is the whole of the repair, and it exists because
    /// the domain refuses to hold such a value (<see cref="FanSettings"/>): a file left in that state would throw
    /// out of the sensor loop on every later load, with nothing the user could read to explain it.
    ///
    /// Three things are asserted, and they are three different claims: the MODEL that comes back holds a curve
    /// the domain accepts, the FILE on disk holds it too (so the next load does not have to repair again), and
    /// the rest of the user's file survived — the curve it replaced was the only casualty.
    ///
    /// The input is literal text on purpose, like the other compatibility cases in this file: it is a file a
    /// hand edit produces, not something this serializer would ever emit, which is exactly why the read path is
    /// the one under test.
    ///
    /// MUTATION THAT REDDENS IT: deleting the <c>SanitiseCurves</c> call from <c>Load</c> — the model then holds
    /// the three-entry curve and the file is untouched.</summary>
    [Fact]
    public void AMalformedFanCurveInTheFileIsRepaired_AndTheFileIsRewritten()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.SettingsPath, """
            {
              "Language": 1,
              "TurboToggles": true,
              "FanPresets": {
                "balanced": { "Mode": 3, "Cpu": 44, "Gpu": 45,
                              "CpuUseCurve": true, "GpuUseCurve": true,
                              "CpuCurve": [10, 20, 30], "GpuCurve": [] }
              }
            }
            """);

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);

        // 1. the model: curves the domain accepts, everything else as the file had it
        var preset = Present(loaded.FanPresets, "balanced");
        Assert.Equal([30, 45, 60, 80, 100], preset.CpuCurve);
        Assert.Equal([30, 45, 60, 80, 100], preset.GpuCurve);
        Assert.Equal(3, preset.Mode);
        Assert.Equal(44, preset.Cpu);
        Assert.True(preset.CpuUseCurve);
        Assert.True(loaded.TurboToggles);
        Assert.Equal(AppLanguage.English, loaded.Language);

        // 2. the file: reloaded from DISK, by a second store, so this cannot pass on the in-memory object
        var reread = new JsonSettingsStore(dir.SettingsPath).Load([]);
        Assert.Equal([30, 45, 60, 80, 100], Present(reread.FanPresets, "balanced").CpuCurve);
        Assert.Equal([30, 45, 60, 80, 100], Present(reread.FanPresets, "balanced").GpuCurve);

        // 3. ...and it is still a settings file, not a rescue: a repair is not a corruption
        Assert.False(File.Exists(dir.SettingsPath + ".bad"));
    }

    /// <summary>A curve that IS a curve is left exactly as it was, and the file is not written to at all. The
    /// control for the test above, and it needs its own case rather than an assertion inside it: a sanitiser that
    /// rewrote every file it read, or that replaced curves unconditionally, would pass the test above and corrupt
    /// every user's file on every start.
    ///
    /// The file is compared as TEXT, so a rewrite of any kind reddens it. The input is deliberately a shape this
    /// serializer does not produce (compact, and with keys in an order it would not choose), so an unnecessary
    /// <c>Save</c> cannot coincide with the original bytes.
    ///
    /// MUTATION THAT REDDENS IT: making <c>SanitiseCurves</c> return true unconditionally — the file is then
    /// reformatted and this comparison fails.</summary>
    [Fact]
    public void ACurveThatIsACurve_IsLeftAlone_AndTheFileIsNotRewritten()
    {
        using var dir = new TempDir();
        const string original = """
            {"FanPresets":{"balanced":{"Mode":3,"Cpu":44,"Gpu":45,
            "CpuCurve":[10,20,30,40,50],"GpuCurve":[90,80,70,60,50]}}}
            """;
        File.WriteAllText(dir.SettingsPath, original);

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);

        Assert.Equal([10, 20, 30, 40, 50], Present(loaded.FanPresets, "balanced").CpuCurve);
        Assert.Equal(original, File.ReadAllText(dir.SettingsPath));
    }

    /// <summary>The shapes a hand edit actually produces, each repaired to the ramp: a curve that is NULL (a
    /// JSON <c>null</c> assigned to an <c>int[]</c> property, which no length check alone would catch), the EMPTY
    /// array (the old persisted spelling of "use the built-in ramp", which the model can no longer hold), an
    /// OVER-LONG one — the case the old evaluator silently ACCEPTED and then read its last entry as the flat top,
    /// which is the open question this wave closed — a SHORT one, and one carrying a duty outside 0..100.
    /// Both halves are arranged malformed in every row, so the expectation is the same for each.</summary>
    [Theory]
    [InlineData("null", "null")]
    [InlineData("[]", "[]")]
    [InlineData("[10, 20, 30, 40, 50, 999]", "[10, 20, 30]")]
    [InlineData("[150, -20, 60, 80, 200]", "[30, 45, 60, 80, 101]")]
    public void EveryShapeOfMalformedStoredCurve_BecomesTheRamp(string cpuCurve, string gpuCurve)
    {
        using var dir = new TempDir();
        // Built by concatenation rather than as an interpolated raw string: the JSON's own braces and the
        // interpolation's collide, and a literal that has to be read twice is not worth the syntax.
        File.WriteAllText(dir.SettingsPath,
            "{\"FanPresets\":{\"balanced\":{\"Mode\":3,\"CpuCurve\":" + cpuCurve
            + ",\"GpuCurve\":" + gpuCurve + "}}}");

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);
        var preset = Present(loaded.FanPresets, "balanced");

        Assert.Equal([30, 45, 60, 80, 100], preset.CpuCurve);
        Assert.Equal([30, 45, 60, 80, 100], preset.GpuCurve);
        Assert.False(File.Exists(dir.SettingsPath + ".bad"));
    }

    /// <summary>The other half of "one curve lost, not the settings file": the malformed curve is replaced and
    /// the OTHER fan's curve is not touched. A sanitiser that replaced both halves whenever either was bad would
    /// pass every test above and quietly throw away a ramp the user had configured — the same class of loss it
    /// exists to prevent, one level down.</summary>
    [Fact]
    public void OnlyTheMalformedHalfOfAPresetIsReplaced()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.SettingsPath, """
            {"FanPresets":{"balanced":{"Mode":3,"Cpu":44,
              "CpuCurve":[10, 20, 30], "GpuCurve":[90, 80, 70, 60, 50]}}}
            """);

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);
        var preset = Present(loaded.FanPresets, "balanced");

        Assert.Equal([30, 45, 60, 80, 100], preset.CpuCurve);
        Assert.Equal([90, 80, 70, 60, 50], preset.GpuCurve);   // the user's own ramp, kept
        Assert.Equal(44, preset.Cpu);
    }

    /// <summary>The temp file is the one part of the atomic swap observable from outside: if the rename did
    /// not happen, <c>.tmp</c> is still lying there and the real file is not the one that was written.</summary>
    [Fact]
    public void SaveLeavesNoTempFileBehind()
    {
        using var dir = new TempDir();

        new JsonSettingsStore(dir.SettingsPath).Save(Populated());

        Assert.True(File.Exists(dir.SettingsPath));
        Assert.False(File.Exists(dir.SettingsPath + ".tmp"));
    }

    [Fact]
    public void SaveCreatesTheFolderItNeeds()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Root, "does", "not", "exist", "settings.json");

        new JsonSettingsStore(nested).Save(new Settings());

        Assert.True(File.Exists(nested));
    }

    /// <summary>settings.json outlives the binary, so the on-disk ENCODING is a compatibility surface, not an
    /// internal detail. <see cref="Settings.Language"/> is read and written as the enum's NUMBER (see its own
    /// comment, which gives Native AOT as the reason). This is the READING half: a file that spells the
    /// language as a number must still load, which is what keeps an existing settings.json usable.
    ///
    /// Honest scope — the reading half alone does NOT forbid a string converter, because
    /// <c>JsonStringEnumConverter</c> accepts integer values unless it is told not to. The half a converter
    /// change would actually break is the WRITE direction, pinned separately by
    /// <see cref="TheLanguageIsWrittenAsANumber_NotAName"/>; the two together are what hold the encoding.
    ///
    /// The file is literal text on purpose. Reading back something the same serializer just produced would
    /// assert self-consistency, not compatibility — and compatibility is the whole claim.</summary>
    [Fact]
    public void AFileWrittenByAnEarlierVersionStillLoads()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.SettingsPath, """
            {
              "Language": 1,
              "TurboToggles": true,
              "Bluelight": 2,
              "FanPresets": { "balanced": { "Mode": 3, "Cpu": 44, "Gpu": 45 } }
            }
            """);

        var loaded = new JsonSettingsStore(dir.SettingsPath).Load([]);

        Assert.Equal(AppLanguage.English, loaded.Language);   // 1 == English, not a string
        Assert.True(loaded.TurboToggles);
        Assert.Equal(2, loaded.Bluelight);
        Assert.Equal(44, Present(loaded.FanPresets, "balanced").Cpu);

        // A property the file does not mention keeps its default rather than throwing. This is the other half
        // of compatibility, and the reason a partial file is not mistaken for a corrupt one.
        Assert.False(loaded.DynamicLighting);
        Assert.False(loaded.CardwireGpuAccess);
        Assert.Empty(loaded.CpuPowerModes);
        Assert.False(File.Exists(dir.SettingsPath + ".bad"));
    }

    /// <summary>The write half of the same claim, and the one that actually bites: an OLDER build reading a
    /// file written by a NEWER one. Numeric is what every shipped version writes, so a change to a string
    /// converter would make this release's files unreadable to the previous release — silently, as a
    /// <c>JsonException</c> into the corrupt-file rescue, i.e. the user's settings replaced by factory
    /// defaults and a <c>.bad</c> file they would have to notice.
    ///
    /// Asserting the JSON VALUE KIND rather than comparing text: the property order or indentation of the
    /// document is not the claim, and a test that broke when they moved would be noise.</summary>
    [Fact]
    public void TheLanguageIsWrittenAsANumber_NotAName()
    {
        using var dir = new TempDir();
        new JsonSettingsStore(dir.SettingsPath).Save(new Settings { Language = AppLanguage.Russian });

        using var doc = JsonDocument.Parse(File.ReadAllText(dir.SettingsPath));

        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("Language").ValueKind);
    }
}

/// <summary>A scratch directory per test, under the system temp folder. Non-negotiable rather than tidy:
/// <see cref="JsonSettingsStore.Load"/> moves corrupt content aside and <c>Save</c> renames over the live
/// file, so a test pointed at the real location would destroy the user's settings.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "acer-helper-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>The settings.json path inside this directory.</summary>
    public string SettingsPath => Path.Combine(Root, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp dir; best effort */ }
    }
}
