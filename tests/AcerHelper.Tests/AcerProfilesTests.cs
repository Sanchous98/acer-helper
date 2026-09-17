using AcerHelper.Domain;
using AcerHelper.Infrastructure.Vendors.Acer;

namespace AcerHelper.Tests;

/// <summary>
/// The EC byte ↔ performance-profile table: which byte the EC accepts, which name/kind/accent the UI shows
/// for it, and which profile each bit of the EC's supported-mask selects.
///
/// A wrong entry here is not a crash, it is the WRONG PROFILE: <c>AcerDevice.SetProfile</c> shifts
/// <see cref="AcerProfiles.ToByte"/>'s result straight into the <c>SetGamingMiscSetting</c> call, so a
/// mismatched byte silently sets a different power/thermal envelope than the user clicked. The table is pure
/// static data with no hardware path, so all of it is reachable from a test.
///
/// The mask rule is the non-obvious half and the reason this file is worth having: the bit position is the
/// EC BYTE VALUE, not the profile's index or display order, so Quiet — byte <c>0x00</c> — is bit 0 while Eco
/// — the first profile displayed — is bit 6.
/// </summary>
public class AcerProfilesTests
{
    private static AccentColor C(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Display order, which is not decoration: it is the order the profile list appears in, and the
    /// order the performance hotkey cycles through.</summary>
    [Fact]
    public void TheDisplayOrderIsTheTableOrder()
    {
        Assert.Equal(["Eco", "Quiet", "Balanced", "Performance", "Turbo"],
            AcerProfiles.All.Select(p => p.DisplayName));
    }

    /// <summary>The <c>Id</c> convention, pinned because two other things depend on it: it is what
    /// <see cref="AcerProfiles.ToByte"/> parses, and it is what gets persisted as <c>ProfileMemory.BaseId</c>
    /// and looked up again by <c>LaptopService</c>. It is the EC byte as a DECIMAL string — "0" for Quiet,
    /// not "0x00".</summary>
    [Fact]
    public void TheIdIsTheEcByteInDecimal()
    {
        Assert.Equal(["6", "0", "1", "4", "5"], AcerProfiles.All.Select(p => p.Id));
    }

    /// <summary>Every column of the table, one profile per case, so a failure names the profile rather than
    /// pointing at a diff. <c>Flash</c> is the colour the EC firmware paints the operating-mode indicator, and
    /// the comment in the source says these are the only colours the firmware accepts on that write — sending
    /// anything else reverts to amber, which is why they are worth freezing exactly.</summary>
    [Theory]
    [InlineData(0x06, "Eco", ProfileKind.Eco, 0x43A047, 0x00DC10)]
    [InlineData(0x00, "Quiet", ProfileKind.Quiet, 0x1B5E20, 0xFFFFFF)]
    [InlineData(0x01, "Balanced", ProfileKind.Balanced, 0xF57C00, 0xC7AE00)]
    [InlineData(0x04, "Performance", ProfileKind.Performance, 0xD32F2F, 0xC7092E)]
    [InlineData(0x05, "Turbo", ProfileKind.Turbo, 0x9C27B0, 0xFF00C7)]
    public void EachProfileCarriesItsKindAccentAndFlash(int b, string name, ProfileKind kind, int accent, int flash)
    {
        var fromTable = AcerProfiles.All.Single(p => p.Id == b.ToString());
        Assert.Equal(name, fromTable.DisplayName);
        Assert.Equal(kind, fromTable.Kind);
        Assert.Equal(C(accent), fromTable.Accent);
        Assert.Equal(C(flash), fromTable.FlashColor);

        // ...and ToDomain(byte) agrees with the table row it stands for.
        var fromByte = AcerProfiles.ToDomain((byte)b);
        Assert.Equal(name, fromByte.DisplayName);
        Assert.Equal(kind, fromByte.Kind);
        Assert.Equal(C(accent), fromByte.Accent);
        Assert.Equal(C(flash), fromByte.FlashColor);
    }

    /// <summary>The load-bearing invariant: <c>ToByte(ToDomain(b)) == b</c> for EVERY byte, not just the five
    /// in the table. An unknown byte gets a profile whose <c>Id</c> is still that byte in decimal — the
    /// <c>"0x.."</c> form is only the DISPLAY name — so a byte the EC reports but the table does not know
    /// still survives the round trip instead of throwing or, worse, coming back as a different profile.
    ///
    /// This is the direction that would break silently: a profile the app read from the EC and then wrote
    /// back must not change value on the way through the domain type.
    ///
    /// (Worth stating plainly, because a code-reading survey of this file concluded the opposite — that
    /// <see cref="AcerProfiles.ToByte"/> throws on the unknown-profile fallback. It does not: the fallback's
    /// <c>Id</c> is the decimal byte, and it is the fallback's DisplayName that is spelled "0x1F".)</summary>
    [Theory]
    [InlineData(0x00)] [InlineData(0x01)] [InlineData(0x04)] [InlineData(0x05)] [InlineData(0x06)]
    [InlineData(0x02)] [InlineData(0x07)] [InlineData(0x1F)] [InlineData(0x7F)] [InlineData(0x80)]
    [InlineData(0xFE)] [InlineData(0xFF)]
    public void ToByteAndToDomainRoundTripEveryByte(int b)
    {
        Assert.Equal((byte)b, AcerProfiles.ToByte(AcerProfiles.ToDomain((byte)b)));
    }

    /// <summary>An unrecognised byte is described as <see cref="ProfileKind.Other"/> and labelled with its hex
    /// value, so the UI shows something honest instead of picking a wrong profile. Accent and Flash are null:
    /// the app must not invent an accent for a profile it knows nothing about, and there is no flash colour it
    /// could legitimately send.</summary>
    [Fact]
    public void AnUnknownByteBecomesAnOtherProfileNamedAfterTheByte()
    {
        var p = AcerProfiles.ToDomain(0x1F);

        Assert.Equal(ProfileKind.Other, p.Kind);
        Assert.Equal("0x1F", p.DisplayName);
        Assert.Equal("31", p.Id);
        Assert.Null(p.Accent);
        Assert.Null(p.FlashColor);
    }

    /// <summary><see cref="AcerProfiles.ToByte"/> is public and parses the profile's <c>Id</c> as a byte, but
    /// another backend's profiles carry a NAME as their id (the Linux power-profiles backend uses
    /// strings like <c>"power-saver"</c>). Handing one of those to this method must fail LOUDLY: the
    /// alternative — a silent default — would write a wrong byte into the EC.
    ///
    /// Only <c>AcerDevice</c> calls this today, with profiles this table produced, so both failures are
    /// guards rather than live paths. They are pinned because they are the contract that keeps it that way.</summary>
    [Theory]
    [InlineData("power-saver", typeof(FormatException))]   // another backend's id, not a number at all
    [InlineData("300", typeof(OverflowException))]         // a number that is not a byte
    [InlineData("", typeof(FormatException))]
    public void ToByteRefusesAProfileThatDidNotComeFromThisTable(string id, Type expected)
    {
        var foreign = new PerformanceProfile(id, "Foreign", ProfileKind.Quiet);

        Assert.Throws(expected, () => AcerProfiles.ToByte(foreign));
    }

    /// <summary>Mask 0 means "the EC did not tell us", and the response is every profile — deliberately
    /// different from "the mask has no known bit", which yields nothing (see the next case). The bit position
    /// is the EC byte value, which is why bit 0 is Quiet (byte 0x00) and bit 6 is Eco, the FIRST profile in
    /// display order. The 0x53 row is the real supported-mask this hardware reports: bits 0, 1, 4 and 6, i.e.
    /// everything except Turbo — and the result is in DISPLAY order, not mask order.</summary>
    [Theory]
    [InlineData(0x00, "Eco,Quiet,Balanced,Performance,Turbo")]   // 0 = unknown → all
    [InlineData(0x01, "Quiet")]                                  // bit 0 → byte 0x00
    [InlineData(0x02, "Balanced")]                               // bit 1 → byte 0x01
    [InlineData(0x10, "Performance")]                            // bit 4 → byte 0x04
    [InlineData(0x20, "Turbo")]                                  // bit 5 → byte 0x05
    [InlineData(0x40, "Eco")]                                    // bit 6 → byte 0x06
    [InlineData(0x53, "Eco,Quiet,Balanced,Performance")]         // the real machine
    [InlineData(0x80, "")]                                       // no known bit → nothing at all
    public void TheMaskBitForAProfileIsItsEcByte(int mask, string expected)
    {
        Assert.Equal(expected, string.Join(",", AcerProfiles.FromMask((byte)mask).Select(p => p.DisplayName)));
    }
}
