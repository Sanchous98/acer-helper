using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// <c>AcerEcHidController.ModeFor</c> — the profile class → EC "system usage mode" byte mapping.
///
/// It is the only part of that class a test may touch, and the reason is worth stating plainly: constructing
/// <c>AcerEcHidController</c> opens the EC HID transport and starts a writer thread, and on a machine that HAS
/// the device — the owner's — <c>Apply</c> really writes the power envelope. A test that instantiated it would
/// drive hardware. <see cref="ModeFor"/> is <c>public static</c> and pure, so it needs none of that.
///
/// Worth covering because the byte is silently load-bearing: the source records the measured envelope
/// (0 = 108 W, 1 = 93 W, 2 = 79 W, 3 = 71 W, 4 = 71 W, 5+ acknowledged then ignored) and that an out-of-range
/// mode is ACKed exactly like a good one and then dropped. So a wrong or reordered mapping does not fail — it
/// applies a different thermal/power limit than the user asked for, and nothing anywhere reports it.
///
/// The specific trap this file exists for: this is a SECOND numbering of the same profiles, unrelated to the
/// EC profile byte in <see cref="AcerProfiles"/>. Turbo is profile byte 0x05 but usage mode 0; Quiet is profile
/// byte 0x00 but usage mode 3 — the two run in opposite directions. See
/// <see cref="TheEcUsageModeIsNotTheProfileByte"/>.
/// </summary>
public class AcerEcUsageModeTests
{
    /// <summary>Every member of the enum. Used to assert the mapping's range and totality rather than only its
    /// known points, so a kind added later cannot slip through unexamined.</summary>
    private static readonly ProfileKind[] EveryKind = Enum.GetValues<ProfileKind>();

    /// <summary>The mapping, one case per kind, with the power envelope the source measured for each. The
    /// numbers are the contract with the EC, not an internal encoding.</summary>
    [Theory]
    [InlineData(ProfileKind.Turbo, 0)]          // 108 W
    [InlineData(ProfileKind.Performance, 1)]    // 93 W
    [InlineData(ProfileKind.Balanced, 2)]       // 79 W
    [InlineData(ProfileKind.Quiet, 3)]          // 71 W
    [InlineData(ProfileKind.Eco, 4)]            // 71 W
    public void EachProfileKindMapsToItsEcUsageMode(ProfileKind kind, byte expected)
    {
        Assert.Equal(expected, AcerEcHidController.ModeFor(kind));
    }

    /// <summary>The two numberings are different and confusingly so, which is exactly why it is pinned. Reusing
    /// the profile byte here — plausible-looking, since both are "the profile as a byte" — would move the
    /// envelope to a different limit for every profile. They disagree for all five kinds, and the orderings are
    /// opposite (the profile byte counts up from Quiet, the usage mode counts up from Turbo).</summary>
    [Fact]
    public void TheEcUsageModeIsNotTheProfileByte()
    {
        foreach (var kind in EveryKind.Where(k => k != ProfileKind.Other))
        {
            var profileByte = AcerProfiles.ToByte(AcerProfiles.All.Single(p => p.Kind == kind));
            var usageMode = AcerEcHidController.ModeFor(kind);

            Assert.NotNull(usageMode);
            Assert.NotEqual(profileByte, usageMode!.Value);
        }
    }

    /// <summary>An unrecognised vendor profile gets NO mode rather than a default one: the source's reason is
    /// that inventing a power envelope is worse than leaving the EC alone. <c>_ =&gt; null</c> also covers enum
    /// values that do not exist yet, so adding a <see cref="ProfileKind"/> later cannot silently start writing a
    /// mode nobody chose — it starts by writing nothing.</summary>
    [Theory]
    [InlineData(ProfileKind.Other)]
    [InlineData((ProfileKind)99)]
    [InlineData((ProfileKind)(-1))]
    public void AnUnrecognisedOrUnknownKindGetsNoMode(ProfileKind kind)
    {
        Assert.Null(AcerEcHidController.ModeFor(kind));
    }

    /// <summary>"5+ acknowledged then ignored" — so a mapping that produced 5 or more would be accepted by this
    /// code, ACKed by the EC, and silently do nothing: the profile switch would appear to work and the envelope
    /// would not move. Every mode the mapping can produce has to be one the EC actually honours.</summary>
    [Fact]
    public void EveryEcUsageModeIsWithinTheRangeTheEcHonours()
    {
        Assert.All(EveryKind, kind =>
        {
            var mode = AcerEcHidController.ModeFor(kind);
            if (mode is { } m) Assert.InRange(m, 0, 4);
        });
    }

    /// <summary>The modes ascend as power falls: a higher mode number is a lower envelope. This is the property
    /// a swap of two entries would break — Balanced and Quiet exchanged would still be a total, in-range mapping
    /// and would still pass every test above, while giving a Quiet machine the Balanced envelope. The order is
    /// asserted directly, so it fails even though each individual value is also checked.</summary>
    [Fact]
    public void TheModesAscendAsPowerFalls()
    {
        ProfileKind[] fromMostToLeastPower =
            [ProfileKind.Turbo, ProfileKind.Performance, ProfileKind.Balanced, ProfileKind.Quiet, ProfileKind.Eco];

        var modes = fromMostToLeastPower.Select(k => AcerEcHidController.ModeFor(k)!.Value).ToList();

        Assert.Equal(modes.OrderBy(m => m), modes);
        Assert.Equal(modes.Count, modes.Distinct().Count());
    }
}
