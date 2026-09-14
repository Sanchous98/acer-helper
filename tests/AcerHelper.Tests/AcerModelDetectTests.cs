using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// Per-model quirk detection — which entry in <c>acer-models.json</c> a machine's DMI product name selects.
///
/// It matters more than its size suggests: the selected entry supplies <c>VendorName</c> and the keyboard RGB
/// layout, and the file exists so an unknown model can be supported WITHOUT a rebuild. Two of the rules below
/// are promises the source comments make about behaviour that has already gone wrong once, and the rest are
/// the precedence and matching semantics.
///
/// These tests are possible because <c>Detect</c> and <c>Merge</c> were split into pure functions taking the
/// config and the raw JSON. Testing the public <c>Detect(string?)</c> would instead test whatever happens to be
/// in the embedded resource and — worse — whether the machine running the suite has a file at
/// %AppData%\AcerHelper\acer-models.json, which is not a test of any rule here.
/// </summary>
public class AcerModelDetectTests
{
    private static AcerModelConfig Cfg(params AcerModel[] models) => new()
    {
        Models = [.. models],
        Default = new AcerModel { Name = "built-in default" },
    };

    private static AcerModel Model(string name, params string[] match) => new() { Name = name, Match = match };

    // ---- which entry wins ----

    /// <summary>User entries are inserted at the front of the list, and the first match wins, so a user
    /// override corrects a built-in without the built-in having to be removed.</summary>
    [Fact]
    public void AUserModelWinsOverABuiltIn()
    {
        var cfg = Cfg(Model("built-in AN18", "AN18"));

        AcerModels.Merge(cfg, """{ "models": [ { "match": ["AN18"], "name": "user AN18" } ] }""");

        Assert.Equal("user AN18", AcerModels.Detect("Acer Nitro AN18-61", cfg).Name);
    }

    [Fact]
    public void AnUnmatchedProductGetsTheDefault()
    {
        var cfg = Cfg(Model("built-in AN18", "AN18"));

        Assert.Equal("built-in default", AcerModels.Detect("Acer Swift SF14-51", cfg).Name);
    }

    /// <summary>The product name comes from DMI and is nullable on the way in. A null must reach the default
    /// rather than throwing — the constructor path that calls this runs during device creation.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AProductThatIsNullOrEmptyGetsTheDefault(string? product)
    {
        var cfg = Cfg(Model("built-in AN18", "AN18"));

        Assert.Equal("built-in default", AcerModels.Detect(product, cfg).Name);
    }

    /// <summary>Matching is a case-insensitive SUBSTRING, so a single entry can cover a family: the real DB
    /// entry for this machine carries both "AN18" and "Nitro 18".</summary>
    [Theory]
    [InlineData("AN18", "AN18")]                          // the pattern alone
    [InlineData("an18", "AN18")]                          // ...and lower-cased
    [InlineData("Acer Nitro AN18-61", "AN18")]            // embedded, as DMI reports it
    [InlineData("ACER NITRO AN18-61", "AN18")]            // ...upper-cased
    [InlineData("Acer Nitro 18 (AN18-61)", "Nitro 18")]   // the DB's second form, with a space
    public void MatchingIsACaseInsensitiveSubstring(string product, string pattern)
    {
        var cfg = Cfg(Model("hit", pattern));

        Assert.Equal("hit", AcerModels.Detect(product, cfg).Name);
    }

    /// <summary>The matcher's <c>!string.IsNullOrEmpty(s)</c> is load-bearing rather than defensive: an empty
    /// pattern matches every product name, so ONE such entry — in the built-in DB or in a user override —
    /// would shadow the entire table and hand every machine the wrong quirks. The empty entries are placed
    /// FIRST here, so a matcher without the guard returns one of them and this test says which.
    ///
    /// The entry with no patterns at all is the neighbouring case: <c>Any</c> over an empty list is false, so
    /// it is skipped without needing the guard.</summary>
    [Fact]
    public void AnEmptyOrAbsentPatternDoesNotShadowEveryOtherEntry()
    {
        var cfg = Cfg(
            new AcerModel { Name = "no patterns", Match = [] },
            Model("empty pattern", ""),
            Model("built-in AN18", "AN18"));

        Assert.Equal("built-in AN18", AcerModels.Detect("Acer Nitro AN18-61", cfg).Name);
    }

    // ---- how a user override is merged ----

    [Fact]
    public void AUserDefaultReplacesTheBuiltInWhenTheKeyIsPresent()
    {
        var cfg = Cfg();

        AcerModels.Merge(cfg, """{ "default": { "name": "user default", "zones": 2 } }""");

        var d = AcerModels.Detect("anything", cfg);
        Assert.Equal("user default", d.Name);
        Assert.Equal(2, d.Zones);
    }

    /// <summary>A user file that only adds models must leave the built-in default alone. This is the case
    /// the key-presence rule exists for: the deserialized <c>Default</c> is NOT null here (the property has an
    /// initializer, which survives an absent key), so the null check alone would let an unspecified default
    /// through and silently adopt "Acer"/4 zones over the real built-in.</summary>
    [Fact]
    public void AUserFileThatOnlyAddsModels_KeepsTheBuiltInDefault()
    {
        var cfg = Cfg(Model("built-in AN18", "AN18"));

        AcerModels.Merge(cfg, """{ "models": [ { "match": ["XYZ"], "name": "user XYZ" } ] }""");

        Assert.Equal("user XYZ", AcerModels.Detect("XYZ-1", cfg).Name);
        Assert.Equal("built-in default", AcerModels.Detect("unmatched", cfg).Name);
    }

    /// <summary>The bug the source comment records as having actually happened: "the file has a default" used
    /// to be decided by comparing the deserialized object against factory values, so a user default that
    /// changed ONLY Zones/Lightbar — leaving Name at the factory "Acer" — looked untouched and was dropped.
    /// The file is the documented no-rebuild escape hatch for unknown models, so dropping it defeats the very
    /// feature. Key presence is the rule now, and this is the direction that used to fail.
    ///
    /// It also pins a genuinely surprising consequence, which is why the last two asserts are here and not in
    /// the test above: the user's <c>Default</c> object replaces the built-in WHOLESALE. A field the user file
    /// omits falls back to <see cref="AcerModel"/>'s own initializer, NOT to the built-in default's value.</summary>
    [Fact]
    public void AUserDefaultThatChangesOnlyZones_IsStillAdopted()
    {
        var cfg = Cfg();
        cfg.Default = new AcerModel { Name = "built-in default", Zones = 8, Lightbar = false };

        AcerModels.Merge(cfg, """{ "default": { "zones": 2 } }""");

        var d = AcerModels.Detect("unmatched", cfg);
        Assert.Equal(2, d.Zones);
        Assert.Equal("Acer", d.Name);      // from AcerModel's initializer, not "built-in default"
        Assert.True(d.Lightbar);           // ...and likewise, not the built-in's false
    }

    /// <summary><c>"default": null</c> must not null <c>cfg.Default</c>. <c>Detect</c> returns that object for
    /// every unmatched product and the caller dereferences <c>.Name</c> immediately, so a null here is an NRE
    /// during device construction — on exactly the unknown machines the default exists to serve.</summary>
    [Fact]
    public void AnExplicitNullDefaultKeepsTheBuiltIn_SoDetectCannotReturnNull()
    {
        var cfg = Cfg();

        AcerModels.Merge(cfg, """{ "default": null }""");

        Assert.Equal("built-in default", AcerModels.Detect("unmatched", cfg).Name);
    }

    /// <summary>The key is looked for case-insensitively, and it has to stay in step with deserialization:
    /// <c>PropertyNameCaseInsensitive</c> makes the serializer bind <c>Default</c>, so a case-sensitive key
    /// check would see a non-null user default and still refuse to adopt it.</summary>
    [Theory]
    [InlineData("default")]
    [InlineData("Default")]
    [InlineData("DEFAULT")]
    public void TheDefaultKeyIsRecognizedInAnyCase(string key)
    {
        var cfg = Cfg();

        AcerModels.Merge(cfg, "{ \"" + key + "\": { \"name\": \"user default\" } }");

        Assert.Equal("user default", AcerModels.Detect("unmatched", cfg).Name);
    }
}
