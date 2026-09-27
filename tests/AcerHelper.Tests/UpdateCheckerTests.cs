using AcerHelper.Domain;
using AcerHelper.Infrastructure;

namespace AcerHelper.Tests;

/// <summary>
/// <see cref="UpdateChecker"/> after task D2b split its one GitHub call into two readers over ONE parse
/// (docs/vendor-plugins.md §5.2/§5.3):
///   * <see cref="UpdateChecker.CheckAsync"/> — the app-update contract, UNCHANGED: null unless the APP is out of
///     date (still, or offline, or no release);
///   * <see cref="UpdateChecker.FetchLatestReleaseAsync"/> — the NEW, UN-GATED reader the plugin check uses: the
///     release's assets whenever the call succeeds, even when the app is current.
///
/// THE SEAM IS THE HTTP BOUNDARY ONLY. The internal constructor takes a fetch delegate, so a fixture drives the
/// real parse, the real asset projection and the real version gate with no network. The tests below pin both
/// halves of the split and, in particular, the property the whole task is built on: the un-gated reader returns
/// assets for a release the app considers "already current".
/// </summary>
public class UpdateCheckerTests
{
    /// <summary>A minimal GitHub "latest release" body: the tag, a page URL, a body and one asset. The shape is
    /// the slice <c>GithubRelease</c> reads (snake_case names).</summary>
    private static string ReleaseJson(string tag, string assetName = "something.bin",
                                      string assetUrl = "https://example.invalid/something.bin") =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://example.invalid/releases/{{tag}}",
          "body": "release notes",
          "assets": [ { "name": "{{assetName}}", "browser_download_url": "{{assetUrl}}" } ]
        }
        """;

    /// <summary>A checker over an injected body; null means "the fetch failed".</summary>
    private static UpdateChecker Checker(string? json) => new(_ => Task.FromResult(json));

    /// <summary>
    /// THE UN-GATED READER (§5.2): with the release's tag EQUAL to the running version — the exact case
    /// <see cref="UpdateChecker.CheckAsync"/> returns null for — <see cref="UpdateChecker.FetchLatestReleaseAsync"/>
    /// still returns the release's assets. This is the whole reason the method exists: the plugin check must not
    /// be gated on the app's own version comparison.
    ///
    /// MUTATION: make FetchLatestReleaseAsync route through CheckAsync (add the version comparison) — it returns
    /// null here and this goes red.</summary>
    [Fact]
    public async Task TheUnGatedFetchReturnsAssetsEvenWhenTheAppIsCurrent()
    {
        var json = ReleaseJson("v" + AppInfo.Version, "AcerHelper.Vendor.acer-nitro-win-x64.dll",
                               "https://example.invalid/acer.dll");

        var assets = await Checker(json).FetchLatestReleaseAsync();

        Assert.NotNull(assets);
        var asset = Assert.Single(assets!);
        Assert.Equal("AcerHelper.Vendor.acer-nitro-win-x64.dll", asset.Name);
        Assert.Equal("https://example.invalid/acer.dll", asset.Url);
    }

    /// <summary>The same current-app release through <see cref="UpdateChecker.CheckAsync"/> is STILL null — the
    /// app-update contract is untouched by the split (§5.2: keep CheckAsync's public behaviour).</summary>
    [Fact]
    public async Task CheckAsyncIsStillNullWhenTheAppIsCurrent()
    {
        var json = ReleaseJson("v" + AppInfo.Version);

        Assert.Null(await Checker(json).CheckAsync());
        // ...while the un-gated reader sees the very same body.
        Assert.NotNull(await Checker(json).FetchLatestReleaseAsync());
    }

    /// <summary>A NEWER release is still an update through <see cref="UpdateChecker.CheckAsync"/>, carrying its
    /// assets, version and changelog — the behaviour the split must preserve.</summary>
    [Fact]
    public async Task CheckAsyncStillReturnsANewerRelease()
    {
        var json = ReleaseJson("v99.0.0", "AcerHelper-setup.msi", "https://example.invalid/setup.msi");

        var info = await Checker(json).CheckAsync();

        Assert.NotNull(info);
        Assert.Equal("99.0.0", info!.Version);              // the human tag, leading 'v' trimmed
        Assert.Equal("release notes", info.Changelog);
        Assert.Equal("AcerHelper-setup.msi", Assert.Single(info.Assets).Name);
    }

    /// <summary>The un-gated reader also returns assets for a NEWER release (it is not app-current-specific): the
    /// plugin path reads the same release either way.</summary>
    [Fact]
    public async Task TheUnGatedFetchReturnsAssetsForANewerReleaseToo()
    {
        Assert.NotNull(await Checker(ReleaseJson("v99.0.0")).FetchLatestReleaseAsync());
    }

    /// <summary>
    /// THE ONE-CALL COMBINED READ (§5.2): <see cref="UpdateChecker.CheckDetailedAsync"/> returns BOTH halves from
    /// a single fetch — the un-gated assets AND (only when newer) the app release. The app tick drives both from
    /// this one call, so the app check and the plugin check are one GitHub round-trip.
    ///
    /// MUTATION: return no assets when the app is current — the plugin check would go blind on a current app and
    /// this goes red.</summary>
    [Fact]
    public async Task TheCombinedCheckReturnsAssetsAndTheAppOfferFromOneFetch()
    {
        // App current: Info is null, but Assets are still there (the plugin path's whole requirement).
        var current = await Checker(ReleaseJson("v" + AppInfo.Version, "p.dll", "https://example.invalid/p.dll"))
            .CheckDetailedAsync();
        Assert.Null(current.Info);
        Assert.Equal("p.dll", Assert.Single(current.Assets!).Name);

        // App outdated: both halves populated.
        var newer = await Checker(ReleaseJson("v99.0.0", "p.dll", "https://example.invalid/p.dll"))
            .CheckDetailedAsync();
        Assert.Equal("99.0.0", newer.Info!.Version);
        Assert.Equal("p.dll", Assert.Single(newer.Assets!).Name);
    }

    /// <summary>A failed fetch (null body) degrades BOTH readers to null — the fail-closed posture, never a
    /// throw.</summary>
    [Fact]
    public async Task AFailedFetchIsNullForBothReaders()
    {
        Assert.Null(await Checker(null).CheckAsync());
        Assert.Null(await Checker(null).FetchLatestReleaseAsync());
    }

    /// <summary>A malformed body (not a release: no tag/page) is null for the un-gated reader, so a GitHub error
    /// page or a truncated body never reaches the plugin flow as an empty asset list.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]                                     // a release with no tag_name
    [InlineData("""{"tag_name":"v1.0.0"}""")]              // no html_url
    public async Task AMalformedBodyIsNull(string body)
        => Assert.Null(await Checker(body).FetchLatestReleaseAsync());

    /// <summary>Asset projection drops an asset missing its name or URL (the same filter <c>CheckAsync</c> always
    /// used), so a half-described asset never reaches the plugin selector as a candidate with a null URL.</summary>
    [Fact]
    public async Task AssetsWithoutANameOrUrlAreDropped()
    {
        const string json = """
        {
          "tag_name": "v99.0.0",
          "html_url": "https://example.invalid/releases/v99.0.0",
          "assets": [
            { "name": "keep.bin", "browser_download_url": "https://example.invalid/keep.bin" },
            { "name": null, "browser_download_url": "https://example.invalid/no-name.bin" },
            { "name": "no-url.bin", "browser_download_url": null }
          ]
        }
        """;

        var assets = await Checker(json).FetchLatestReleaseAsync();

        Assert.Equal("keep.bin", Assert.Single(assets!).Name);
    }
}
