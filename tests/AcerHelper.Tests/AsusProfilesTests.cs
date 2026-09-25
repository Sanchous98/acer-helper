using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Asus;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// The PURE half of the ASUS/asusd profiles integration: the profile vocabulary, the payload parsers (both
/// asusd wire forms) and the exact <c>busctl</c> argument lists. All of it lives in the un-suffixed
/// <c>Infrastructure/Vendors/Asus/AsusdPlatform.cs</c> precisely so this suite can reach it — the test project
/// targets net10.0-windows and the app removes <c>**/*.Linux.cs</c> from that TFM, so anything left in
/// <c>AsusdPlatform.Linux.cs</c> would be unreachable (AcerProfilePorts.cs states the rule at length).
///
/// THE VOCABULARY MUST MIRROR THE GENERIC SYSFS TABLE, and that is asserted rather than trusted: asusd and the
/// raw <c>platform_profile</c> node can serve the SAME machine on different boots, and the app persists the
/// profile id in settings.json, the tray and the per-mode presets. If the two sources disagreed about what
/// "balanced" is, a preset written with asusd present would silently fail to apply when the fallback is live.
/// </summary>
public class AsusProfilesTests
{
    // ---- the vocabulary ----

    [Theory]
    [InlineData("low-power",   "Low power",   ProfileKind.Eco)]
    [InlineData("quiet",       "Quiet",       ProfileKind.Quiet)]
    [InlineData("balanced",    "Balanced",    ProfileKind.Balanced)]
    [InlineData("performance", "Performance", ProfileKind.Performance)]
    [InlineData("custom",      "Custom",      ProfileKind.Other)]
    public void EachKernelTokenCarriesItsOwnNameAndClass(string token, string name, ProfileKind kind)
    {
        var profile = AsusProfiles.ToProfile(token);

        Assert.Equal(token, profile.Id);
        Assert.Equal(name, profile.DisplayName);
        Assert.Equal(kind, AsusProfiles.TraitsOf(profile).Kind);
    }

    /// <summary>A token the table does not know is carried through as itself with the neutral class — NOT
    /// mapped onto a nearby mode. This is the same answer the generic sysfs table gives, and it is what keeps a
    /// mode the app cannot classify from silently applying another mode's presets.</summary>
    [Fact]
    public void AnUnknownTokenIsItselfAndUnclassified()
    {
        var odd = AsusProfiles.ToProfile("turbo");

        Assert.Equal("turbo", odd.Id);
        Assert.Equal(ProfileKind.Other, AsusProfiles.TraitsOf(odd).Kind);
        Assert.Equal(ProfileTraits.Unknown.Accent, AsusProfiles.TraitsOf(odd).Accent);
        Assert.Null(AsusProfiles.TokenOf(odd));
    }

    /// <summary>The numeric codes are asusd's own enum (Balanced 0, Performance 1, Quiet 2, LowPower 3,
    /// Custom 4) and the token/code doors must round-trip for every row — a transposed pair here would write the
    /// wrong power envelope on the numeric wire with no error anywhere.</summary>
    [Theory]
    [InlineData("balanced",    0)]
    [InlineData("performance", 1)]
    [InlineData("quiet",       2)]
    [InlineData("low-power",   3)]
    [InlineData("custom",      4)]
    public void TheNumericCodesRoundTrip(string token, int code)
    {
        Assert.Equal(code, AsusProfiles.CodeFor(token));
        Assert.Equal(token, AsusProfiles.TokenForCode(code));
    }

    [Fact]
    public void ANumericCodeTheEnumDoesNotNameHasNoToken()
        => Assert.Null(AsusProfiles.TokenForCode(99));

    // ---- the payload parsers: both asusd wire forms ----

    /// <summary>String form: an older asusd reports the choices as <c>as</c> and the active value as
    /// <c>s</c>.</summary>
    [Fact]
    public void TheStringWireFormParses()
    {
        Assert.Equal(["quiet", "balanced", "performance"],
            AsusdValues.ParseProfileList("as 3 \"quiet\" \"balanced\" \"performance\""));
        Assert.Equal("balanced", AsusdValues.ParseProfile("s \"balanced\""));
    }

    /// <summary>Numeric form: current asusd declares PlatformProfile with <c>#[zvariant(signature = "u")]</c>,
    /// so the choices arrive as <c>au</c> (length first) and the active value as <c>u</c>. The array LENGTH must
    /// be dropped rather than read as a profile code.</summary>
    [Fact]
    public void TheNumericWireFormParses()
    {
        Assert.Equal(["quiet", "balanced", "performance"],
            AsusdValues.ParseProfileList("au 3 2 0 1"));
        Assert.Equal("balanced", AsusdValues.ParseProfile("u 0"));
        Assert.Equal("performance", AsusdValues.ParseProfile("u 1"));
    }

    [Fact]
    public void AnUnreadablePayloadNamesNothing()
    {
        Assert.Null(AsusdValues.ParseProfile(""));
        Assert.Empty(AsusdValues.ParseProfileList("au 0"));
    }

    [Theory]
    [InlineData("s \"balanced\"", "s")]
    [InlineData("u 0", "u")]
    [InlineData("as 3 \"a\"", "as")]
    [InlineData("au 1 0", "au")]
    [InlineData("", "")]
    public void TheSignatureIsTheFirstToken(string output, string signature)
        => Assert.Equal(signature, Asusd.SignatureOf(output));

    [Theory]
    [InlineData("u", true)]
    [InlineData("y", true)]
    [InlineData("au", true)]
    [InlineData("s", false)]
    [InlineData("as", false)]
    public void IntegerSignaturesAreRecognised(string signature, bool integer)
        => Assert.Equal(integer, Asusd.IsIntegerSignature(signature));

    // ---- the exact busctl argument lists ----

    /// <summary>Spelled out rather than assembled from the constants: a renamed interface, a capitalised
    /// property or a changed path is a call that fails at the bus or — worse — writes somewhere else, and a test
    /// that read its expectations out of the code under test could not see it. <c>--system</c> is absent because
    /// <c>Busctl.Call</c> adds it, exactly as CardwireGpuAccessTests pins for its one call.</summary>
    [Fact]
    public void TheCallsAreTheseExactArgumentLists()
    {
        Assert.Equal(["status", "xyz.ljones.Asusd"], Asusd.StatusArguments());

        Assert.Equal(
            ["get-property", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.Platform", "platform_profile"],
            Asusd.GetPropertyArguments(Asusd.PlatformInterface, Asusd.PlatformProfileProperty));

        Assert.Equal(
            ["set-property", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.Platform", "platform_profile", "s", "balanced"],
            Asusd.SetPropertyArguments(Asusd.PlatformInterface, Asusd.PlatformProfileProperty, "s", "balanced"));

        // The zero-argument method: no signature element at all, which is how busctl spells it.
        Assert.Equal(
            ["call", "xyz.ljones.Asusd", "/xyz/ljones", "xyz.ljones.Platform", "next_platform_profile"],
            Asusd.CallArguments(Asusd.PlatformInterface, Asusd.NextPlatformProfileMethod));

        Assert.DoesNotContain("--system", Asusd.StatusArguments());
    }

    [Fact]
    public void AFailureKeepsItsWordsOnOneLine()
    {
        Assert.Equal("Call failed: Access denied", Asusd.Describe(1, "Call failed: Access denied\n"));
        Assert.Equal("busctl exited with 127", Asusd.Describe(127, ""));
    }
}
