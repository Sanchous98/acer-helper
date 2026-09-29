using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;
using AcerHelper.Infrastructure.Vendors.Generic;

namespace AcerHelper.Tests;

/// <summary>
/// <c>AcerEcHidController.ModeFor</c> — the profile class → EC "system usage mode" byte mapping.
///
/// It is the only part of that class a test may touch, and the reason is worth stating plainly: constructing
/// <c>AcerEcHidController</c> opens the EC HID transport and starts a writer thread, and on a machine that HAS
/// the device — the owner's — <c>Apply</c> really writes the power envelope. A test that instantiated it would
/// drive hardware. The members these tests reach — <see cref="ModeFor"/> and the envelope builder <c>FrameFor</c>
/// — are <c>static</c> and pure, so they need none of that.
///
/// Worth covering because the byte is silently load-bearing: the source records the measured envelope
/// (0 = 115 W, 1 = 100 W, 2 = 85 W, 3 = 75 W, 4 = 75 W, 5+ acknowledged then ignored) and that an out-of-range
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
    [InlineData(ProfileKind.Turbo, 0)]          // 115 W
    [InlineData(ProfileKind.Performance, 1)]    // 100 W
    [InlineData(ProfileKind.Balanced, 2)]       // 85 W
    [InlineData(ProfileKind.Quiet, 3)]          // 75 W
    [InlineData(ProfileKind.Eco, 4)]            // 75 W
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
            var profileByte = AcerProfiles.ToByte(AcerProfiles.All.Single(p => AcerProfiles.TraitsOf(p).Kind == kind));
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

    /// <summary>The 65-byte envelope is built in ONE place, <c>FrameFor</c> — so its bytes are pinned here rather
    /// than at the writer's call site they used to be spelled at. A builder that moved a byte would silently
    /// move the WRITE that carries the power envelope, and the EC ACKs an out-of-range or garbled mode exactly
    /// like a good one: the profile switch would look like it worked and the envelope would not move (see the
    /// class docstring above).
    ///
    /// The write is Acer's own set template (`A0 00 A0 01 00 01 0{mode}`, feature 0x0001, cmd 0x01, the usage
    /// mode in parameter byte 6), zero-padded to 65: everything past the parameters is frame, not payload.</summary>
    [Fact]
    public void TheUsageModeEnvelopeIsBuiltInOnePlaceAndZeroPadded()
    {
        var write = AcerEcHidController.FrameFor(feature: 0x0001, cmd: 0x01, param6: 3);
        Assert.Equal(65, write.Length);
        Assert.Equal(new byte[] { 0xA0, 0x00, 0xA0, 0x01, 0x00, 0x01, 0x03 }, write[..7]);
        Assert.All(write[7..], b => Assert.Equal(0, b));
    }

    // ---- the GPU power LEVEL byte (the manual envelope pick) ----

    /// <summary>The level → EC byte mapping, one case per level, with the measured envelope. These are the SAME
    /// wire bytes <see cref="AcerEcHidController.ModeFor"/> produces for the profile class of the same name, and
    /// the two mappings are deliberately separate surfaces — this pins the level one on its own so a reorder of
    /// either shows up here rather than silently moving the envelope. The EC's fifth row (Eco, byte 4) is not a
    /// level: it enforces the same envelope as Quiet, so it is deliberately absent (see <c>GpuPowerLevel</c>).</summary>
    [Theory]
    [InlineData(GpuPowerLevel.Turbo, 0)]        // 115 W
    [InlineData(GpuPowerLevel.Performance, 1)]  // 100 W
    [InlineData(GpuPowerLevel.Balanced, 2)]     // 85 W
    [InlineData(GpuPowerLevel.Quiet, 3)]        // 75 W
    public void EachGpuPowerLevelMapsToItsEcUsageMode(GpuPowerLevel level, byte expected)
    {
        Assert.Equal(expected, AcerEcHidController.ModeForLevel(level));
    }

    /// <summary>The level byte and the profile-class byte agree for the shared name — the property that keeps
    /// "Turbo" meaning one row whether it was chosen as a profile or as an envelope level. If either table is
    /// reordered independently this reddens, which is the drift the two separate mappings exist to make visible
    /// rather than hide. The four LEVELS are compared; the profile axis keeps its Eco profile class, which is a
    /// different surface and has no matching power level.</summary>
    [Fact]
    public void ThePowerLevelByteAgreesWithTheProfileClassByteOfTheSameName()
    {
        Assert.Equal(AcerEcHidController.ModeFor(ProfileKind.Turbo), AcerEcHidController.ModeForLevel(GpuPowerLevel.Turbo));
        Assert.Equal(AcerEcHidController.ModeFor(ProfileKind.Performance), AcerEcHidController.ModeForLevel(GpuPowerLevel.Performance));
        Assert.Equal(AcerEcHidController.ModeFor(ProfileKind.Balanced), AcerEcHidController.ModeForLevel(GpuPowerLevel.Balanced));
        Assert.Equal(AcerEcHidController.ModeFor(ProfileKind.Quiet), AcerEcHidController.ModeForLevel(GpuPowerLevel.Quiet));
    }

    /// <summary>The EC's Eco row (byte 4) is NOT offered as a power level, because it enforces the same envelope
    /// as Quiet — a selector entry that moves nothing. This pins the omission: the level enum has no Eco member,
    /// so a cast of 4 is refused by the mapping like any other undefined value, and the advertised rows stop at
    /// Quiet. The profile axis is untouched: <c>ModeFor(ProfileKind.Eco)</c> still returns byte 4.</summary>
    [Fact]
    public void TheEcoEcRowIsNotAPowerLevel()
    {
        Assert.DoesNotContain(GpuPowerLevels.All, l => l.ToString() == "Eco");
        Assert.Equal((byte?)4, AcerEcHidController.ModeFor(ProfileKind.Eco));   // the profile axis keeps it...
        Assert.DoesNotContain(AcerEcHidController.PowerLevels, o => o.Level.ToString() == "Eco"); // ...the level axis does not
    }

    /// <summary>Every level maps into the range the EC honours (0..4): "5+ acknowledged then ignored" means a
    /// mapping that produced 5 would be ACKed and then do nothing, so a pick would look like it worked and the
    /// envelope would not move.</summary>
    [Fact]
    public void EveryPowerLevelMapsWithinTheRangeTheEcHonours()
    {
        Assert.All(GpuPowerLevels.All, level => Assert.InRange(AcerEcHidController.ModeForLevel(level), 0, 4));
    }

    /// <summary>The port's advertised rows and the byte mapping describe the same levels: a row offered by
    /// the port that <c>ModeForLevel</c> cannot map would be a selector entry that does nothing, and the reverse
    /// a byte reachable only by a profile switch. The two are one table read two ways, and this is the pin.</summary>
    [Fact]
    public void TheAdvertisedPowerRowsCoverEveryMappableLevel()
    {
        Assert.Equal(GpuPowerLevels.All, AcerEcHidController.PowerLevels.Select(o => o.Level));
    }

    /// <summary>An undefined enum value is refused by the mapping rather than wrapped into a valid byte — the
    /// total-mapping hazard <c>ModeFor</c> never had because it returns null. A level the enum does not define
    /// cannot come from the UI (the rows are declared), but a cast somewhere would otherwise turn it into row 4
    /// silently. The value 4 is the real migration case: it was the removed Eco level, and must NOT be wrapped
    /// into the EC's byte-4 row (which this app no longer offers as a level).</summary>
    [Theory]
    [InlineData((GpuPowerLevel)99)]
    [InlineData((GpuPowerLevel)(-1))]
    [InlineData((GpuPowerLevel)4)]
    public void AnUndefinedPowerLevelIsRefused_NotWrappedIntoAValidByte(GpuPowerLevel level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AcerEcHidController.ModeForLevel(level));
    }
}
